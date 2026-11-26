/*
 * LMSubs — Jellyfin web player integration for the LMSubs plugin.
 *
 * Adds a "LM字幕" button to the video OSD with:
 *   - 全量生成: server-side ASR over the whole file (+ optional LLM translation),
 *     progress polling, then loads the result as a WebVTT track.
 *   - 实时模式: on-demand ASR+translation of short windows ahead of the playback
 *     position (no full pass needed; results are cached server-side).
 *   - 加载已生成: load previously generated source / translated subtitles.
 *
 * Install: serve this file from the jellyfin-web root and add
 *   <script src="lmsubs.js" defer></script>
 * to index.html (see scripts/install-web.sh). No build step required.
 */
(function () {
    'use strict';

    var POLL_MS = 2500;
    var RT_WINDOW = 30;        // seconds per realtime ASR window
    var RT_PREFETCH = 12;      // start fetching next window when this many seconds remain
    var RT_MAX_CUES = 400;     // cap live cue list

    var state = {
        itemId: null,
        video: null,
        track: null,
        pollTimer: null,
        rtTimer: null,
        rt: null,               // { busy, nextStart, cues, translate, lang }
        panel: null,
        button: null
    };

    // ------------------------------------------------------------ jellyfin api helpers

    function apiClient() {
        return window.ApiClient || null;
    }

    function apiUrl(path, query) {
        var ac = apiClient();
        var url = ac ? ac.getUrl(path) : ('/' + path);
        if (query) {
            url += (url.indexOf('?') >= 0 ? '&' : '?') + query;
        }
        return url;
    }

    function apiFetch(path, options) {
        var ac = apiClient();
        options = options || {};
        options.headers = options.headers || {};
        if (ac) {
            // fetch-based auth header (works on modern jellyfin-web)
            var token = ac.accessToken ? ac.accessToken() : null;
            if (token) {
                options.headers['X-Emby-Token'] = token;
            }
        }
        if (options.body && typeof options.body !== 'string') {
            options.body = JSON.stringify(options.body);
            options.headers['Content-Type'] = 'application/json';
        }
        return fetch(apiUrl(path), options).then(function (r) {
            if (!r.ok) {
                return r.text().then(function (t) {
                    var msg = t;
                    try { msg = JSON.parse(t).error || t; } catch (e) { /* raw */ }
                    throw new Error('HTTP ' + r.status + ': ' + msg);
                });
            }
            return r.json();
        });
    }

    function toast(text) {
        try {
            if (window.Dashboard && Dashboard.showToast) { Dashboard.showToast({ title: 'LMSubs', text: text }); return; }
        } catch (e) { /* fall through */ }
        console.log('[LMSubs]', text);
    }

    // ------------------------------------------------------------ item / video discovery

    function currentItemId() {
        var ac = apiClient();
        // Modern web: item id is in the URL hash/query (?id=...) on the video page.
        var m = /[?&]id=([0-9a-f]{32})/i.exec(location.href);
        if (m) { return m[1]; }
        if (ac && ac._serverInfo && ac._currentItem) { return ac._currentItem.Id; }
        return null;
    }

    function findVideo() {
        var videos = document.querySelectorAll('video');
        for (var i = 0; i < videos.length; i++) {
            if (videos[i].offsetParent !== null || videos[i].readyState > 0) { return videos[i]; }
        }
        return videos.length ? videos[0] : null;
    }

    // ------------------------------------------------------------ VTT track management

    function removeTrack() {
        if (state.track && state.track.parentNode) {
            state.track.parentNode.removeChild(state.track);
        }
        state.track = null;
    }

    function attachVttTrack(vttText, label) {
        var video = state.video;
        if (!video) { return; }
        removeTrack();
        var blob = new Blob([vttText], { type: 'text/vtt' });
        var track = document.createElement('track');
        track.kind = 'subtitles';
        track.label = label || 'LMSubs';
        track.srclang = 'zh';
        track.src = URL.createObjectURL(blob);
        track.default = true;
        video.appendChild(track);
        // Force showing: hide other tracks.
        setTimeout(function () {
            for (var i = 0; i < video.textTracks.length; i++) {
                video.textTracks[i].mode = (video.textTracks[i] === track.track) ? 'showing' : 'hidden';
            }
        }, 100);
        state.track = track;
    }

    function loadGenerated(translated, lang) {
        var q = 'translated=' + (translated ? 'true' : 'false');
        if (lang) { q += '&lang=' + encodeURIComponent(lang); }
        return fetch(apiUrl('LmSubs/Subtitles/' + state.itemId, q)).then(function (r) {
            if (!r.ok) { throw new Error('还没有生成' + (translated ? '译文' : '原文') + '字幕'); }
            return r.text();
        }).then(function (vtt) {
            stopRealtime();
            attachVttTrack(vtt, translated ? 'LMSubs 翻译' : 'LMSubs 原文');
            toast('已加载' + (translated ? '翻译' : '原文') + '字幕');
        });
    }

    function fmtTime(sec) {
        sec = Math.max(0, sec);
        var h = Math.floor(sec / 3600), m = Math.floor(sec / 60) % 60, s = sec % 60;
        function p(n, w) { return String(n).padStart(w || 2, '0'); }
        return p(h) + ':' + p(m) + ':' + p(Math.floor(s)) + '.' + p(Math.floor((s % 1) * 1000), 3);
    }

    function cuesToVtt(cues) {
        var out = 'WEBVTT\n\n';
        cues.forEach(function (c) {
            out += fmtTime(c.start) + ' --> ' + fmtTime(c.end) + '\n' + c.text + '\n\n';
        });
        return out;
    }

    // ------------------------------------------------------------ full generation

    function startGeneration(translate) {
        stopPolling();
        setPanelStatus('提交任务…');
        apiFetch('LmSubs/Generate', {
            method: 'POST',
            body: { itemId: state.itemId, translate: translate }
        }).then(function () {
            pollStatus();
        }).catch(function (e) {
            setPanelStatus('失败: ' + e.message);
        });
    }

    function stopPolling() {
        if (state.pollTimer) { clearTimeout(state.pollTimer); state.pollTimer = null; }
    }

    function pollStatus() {
        apiFetch('LmSubs/Status/' + state.itemId).then(function (s) {
            if (s.status === 'done') {
                setPanelStatus('完成 ✓');
                loadGenerated(s.translate !== false, s.targetLanguage).catch(function (e) {
                    setPanelStatus(e.message);
                });
                return;
            }
            if (s.status === 'error') { setPanelStatus('出错: ' + (s.error || '未知')); return; }
            if (s.status === 'cancelled') { setPanelStatus('已取消'); return; }
            if (s.status === 'none') { setPanelStatus('尚未生成'); return; }
            var pct = Math.round((s.progress || 0) * 100);
            setPanelStatus((s.stage || s.status) + ' — ' + pct + '%');
            state.pollTimer = setTimeout(pollStatus, POLL_MS);
        }).catch(function (e) {
            setPanelStatus('查询失败: ' + e.message);
        });
    }

    // ------------------------------------------------------------ realtime mode

    function startRealtime(translate) {
        stopRealtime();
        removeTrack();
        state.rt = { busy: false, nextStart: Math.max(0, state.video.currentTime - 2), cues: [], translate: translate };
        attachVttTrack(cuesToVtt([]), 'LMSubs 实时');
        state.rtTimer = setInterval(realtimeTick, 1000);
        realtimeTick();
        toast('实时模式已开启（按需转写当前播放位置附近的音频）');
    }

    function stopRealtime() {
        if (state.rtTimer) { clearInterval(state.rtTimer); state.rtTimer = null; }
        state.rt = null;
    }

    function realtimeTick() {
        var rt = state.rt, video = state.video;
        if (!rt || !video || video.paused && rt.cues.length > 0) { return; }
        var t = video.currentTime;

        // Seeked away from covered range → reset ahead position.
        if (t + 2 < rt.nextStart - RT_WINDOW || t > rt.nextStart + RT_WINDOW) {
            rt.nextStart = Math.max(0, t - 2);
        }

        if (rt.busy || t < rt.nextStart - RT_PREFETCH) { return; }
        rt.busy = true;
        var start = rt.nextStart;
        rt.nextStart = start + RT_WINDOW;
        setPanelStatus('实时转写 ' + fmtTime(start).slice(0, 8) + ' …');
        apiFetch('LmSubs/Realtime', {
            method: 'POST',
            body: {
                itemId: state.itemId,
                start: start,
                window: RT_WINDOW,
                translate: rt.translate
            }
        }).then(function (res) {
            if (!state.rt) { return; }
            rt.cues = rt.cues.concat(res.cues || []);
            if (rt.cues.length > RT_MAX_CUES) { rt.cues = rt.cues.slice(-RT_MAX_CUES); }
            rebuildLiveTrack();
            setPanelStatus('实时模式运行中（已缓存 ' + rt.cues.length + ' 条）');
        }).catch(function (e) {
            setPanelStatus('实时转写失败: ' + e.message);
        }).finally(function () {
            if (state.rt) { state.rt.busy = false; }
        });
    }

    function rebuildLiveTrack() {
        var rt = state.rt;
        if (!rt || !state.video) { return; }
        var label = 'LMSubs 实时';
        // Replace track content by re-attaching (cheap at 1 Hz max).
        var wasMode = 'showing';
        attachVttTrack(cuesToVtt(rt.cues), label);
        void wasMode;
    }

    // ------------------------------------------------------------ UI

    function setPanelStatus(text) {
        if (state.panel) {
            var el = state.panel.querySelector('.lmsubs-status');
            if (el) { el.textContent = text; }
        }
    }

    function buildPanel() {
        var panel = document.createElement('div');
        panel.className = 'lmsubs-panel';
        panel.style.cssText = 'position:fixed;right:24px;bottom:96px;z-index:10000;background:rgba(20,20,20,.95);' +
            'color:#eee;border:1px solid #444;border-radius:8px;padding:14px 16px;min-width:260px;font-size:14px;' +
            'box-shadow:0 4px 24px rgba(0,0,0,.5);display:none;font-family:sans-serif;';
        panel.innerHTML =
            '<div style="font-weight:bold;margin-bottom:10px;">LMSubs 语音字幕</div>' +
            '<div style="display:flex;flex-direction:column;gap:8px;">' +
            '<button data-act="gen-bilingual" class="lmsubs-btn">全量生成（转写＋翻译）</button>' +
            '<button data-act="gen-source" class="lmsubs-btn">全量生成（仅转写）</button>' +
            '<button data-act="rt-on" class="lmsubs-btn">实时模式（转写＋翻译）</button>' +
            '<button data-act="rt-src" class="lmsubs-btn">实时模式（仅转写）</button>' +
            '<button data-act="load-trans" class="lmsubs-btn">加载已生成译文</button>' +
            '<button data-act="load-src" class="lmsubs-btn">加载已生成原文</button>' +
            '<button data-act="stop" class="lmsubs-btn">停止 / 关闭字幕</button>' +
            '</div>' +
            '<div class="lmsubs-status" style="margin-top:10px;color:#9c9;font-size:12px;min-height:16px;"></div>';
        Array.prototype.forEach.call(panel.querySelectorAll('.lmsubs-btn'), function (b) {
            b.style.cssText = 'background:#333;color:#eee;border:1px solid #555;border-radius:5px;' +
                'padding:7px 10px;cursor:pointer;text-align:left;font-size:13px;';
            b.addEventListener('mouseenter', function () { b.style.background = '#454545'; });
            b.addEventListener('mouseleave', function () { b.style.background = '#333'; });
        });
        panel.addEventListener('click', function (ev) {
            var act = ev.target && ev.target.getAttribute('data-act');
            if (!act) { return; }
            ev.stopPropagation();
            switch (act) {
                case 'gen-bilingual': startGeneration(true); break;
                case 'gen-source': startGeneration(false); break;
                case 'rt-on': startRealtime(true); break;
                case 'rt-src': startRealtime(false); break;
                case 'load-trans': loadGenerated(true).catch(function (e) { setPanelStatus(e.message); }); break;
                case 'load-src': loadGenerated(false).catch(function (e) { setPanelStatus(e.message); }); break;
                case 'stop':
                    stopPolling(); stopRealtime(); removeTrack();
                    setPanelStatus('已停止');
                    break;
            }
        });
        document.body.appendChild(panel);
        return panel;
    }

    function ensureButton() {
        // Find the video OSD button row (jellyfin-web videoosd).
        var osd = document.querySelector('.videoOsdBottom .buttons, .videoOsdBottom');
        if (!osd) { return; }
        if (state.button && state.button.isConnected) { return; }

        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'autoSize paper-icon-button-light lmsubs-osd-btn';
        btn.title = 'LMSubs 语音字幕';
        btn.setAttribute('is', 'paper-icon-button-light');
        btn.innerHTML = '<span class="material-icons" style="font-size:20px;">translate</span>' +
            '<span style="font-size:11px;margin-left:2px;">LM</span>';
        btn.style.cssText = 'color:#fff;display:inline-flex;align-items:center;';
        btn.addEventListener('click', function (ev) {
            ev.stopPropagation();
            state.itemId = currentItemId() || state.itemId;
            state.video = findVideo() || state.video;
            if (!state.itemId) { toast('无法识别当前播放的媒体项'); return; }
            if (!state.panel) { state.panel = buildPanel(); }
            state.panel.style.display = state.panel.style.display === 'none' ? 'block' : 'none';
            if (state.panel.style.display === 'block') {
                apiFetch('LmSubs/Status/' + state.itemId).then(function (s) {
                    if (s.status === 'none') { setPanelStatus('尚未生成'); }
                    else if (s.status === 'done') { setPanelStatus('已有缓存字幕，可直接加载'); }
                    else { setPanelStatus((s.stage || s.status) + ' — ' + Math.round((s.progress || 0) * 100) + '%'); pollStatus(); }
                }).catch(function () { /* plugin not installed / older server */ });
            }
        });
        osd.appendChild(btn);
        state.button = btn;
    }

    // Watch for the player OSD appearing/disappearing.
    var observer = new MutationObserver(function () {
        try { ensureButton(); } catch (e) { console.warn('[LMSubs]', e); }
    });
    observer.observe(document.body, { childList: true, subtree: true });

    // Close panel when leaving the player.
    setInterval(function () {
        var onPlayer = !!document.querySelector('.videoOsdBottom');
        if (!onPlayer) {
            if (state.panel) { state.panel.style.display = 'none'; }
            if (state.rtTimer) { stopRealtime(); }
            stopPolling();
            state.itemId = null;
        }
    }, 3000);

    console.log('[LMSubs] web integration loaded');
})();
