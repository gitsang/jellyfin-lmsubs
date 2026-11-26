using System.Collections.Concurrent;
using Jellyfin.Plugin.LmSubs.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LmSubs.Services;

/// <summary>State of the generation pipeline for one item.</summary>
public class GenerationJob
{
    public Guid ItemId { get; init; }

    public string ItemName { get; set; } = string.Empty;

    /// <summary>queued | running | done | error | cancelled.</summary>
    public string Status { get; set; } = "queued";

    public string Stage { get; set; } = string.Empty;

    public double Progress { get; set; }

    public string? Error { get; set; }

    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public DateTime? FinishedUtc { get; set; }

    public string TargetLanguage { get; set; } = string.Empty;

    /// <summary>Playback position this pass was started from.</summary>
    public double StartSeconds { get; init; }

    public CancellationTokenSource Cts { get; } = new();
}

/// <summary>
/// Chunk-based, position-prioritized ASR + translation pipeline.
///
/// Selecting the "LM 字幕" track starts the pipeline near the current playback
/// position; completed ranges are cached on disk and never re-sent to the API.
/// Each chunk is appended to the subtitle files as soon as it finishes, so the
/// player picks up new subtitles as it re-requests the growing file.
///
/// Selecting "LM 字幕（实时）" always re-transcribes from the current position
/// and overwrites the cache (force mode).
/// </summary>
public class SubtitleService
{
    private readonly ILibraryManager _libraryManager;
    private readonly SubtitleStore _store;
    private readonly OpenAiClient _openAi;
    private readonly AudioExtractor _extractor;
    private readonly ILogger<SubtitleService> _logger;

    private readonly ConcurrentDictionary<Guid, GenerationJob> _jobs = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _itemLocks = new();
    private readonly ConcurrentDictionary<Guid, double> _lastStartSeconds = new();

    public SubtitleService(
        ILibraryManager libraryManager,
        SubtitleStore store,
        OpenAiClient openAi,
        AudioExtractor extractor,
        ILogger<SubtitleService> logger)
    {
        _libraryManager = libraryManager;
        _store = store;
        _openAi = openAi;
        _extractor = extractor;
        _logger = logger;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public SubtitleStore Store => _store;

    // ------------------------------------------------------------------ media resolution

    private Video GetVideo(Guid itemId) =>
        _libraryManager.GetItemById(itemId) as Video
        ?? throw new KeyNotFoundException($"找不到视频项 {itemId}");

    public (string Path, int? AudioStreamIndex, double Duration) ResolveMedia(Guid itemId, int? audioStreamIndex)
    {
        var video = GetVideo(itemId);
        var sources = video.GetMediaSources(true);
        var source = sources.FirstOrDefault() ?? throw new InvalidOperationException("媒体项没有可用的 MediaSource");
        var path = source.Path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            throw new FileNotFoundException($"媒体文件不存在或 Jellyfin 无法访问: {path}");
        }

        double duration = source.RunTimeTicks.HasValue ? TimeSpan.FromTicks(source.RunTimeTicks.Value).TotalSeconds : 0;
        return (path, audioStreamIndex, duration);
    }

    /// <summary>
    /// Best-effort current playback position for an item, learned from subtitle
    /// delivery requests (startTimeTicks) and the REST API.
    /// </summary>
    public void ReportPlaybackPosition(Guid itemId, double seconds)
    {
        if (seconds >= 0)
        {
            _lastStartSeconds[itemId] = seconds;
        }
    }

    public double GetPlaybackPosition(Guid itemId) =>
        _lastStartSeconds.TryGetValue(itemId, out var s) ? s : 0;

    // ------------------------------------------------------------------ language helper

    /// <summary>Best-effort ISO-639-2 code for the configured target language (metadata only).</summary>
    public static string ThreeLetterLanguage(string language)
    {
        var l = (language ?? string.Empty).ToLowerInvariant();
        if (l.Contains("chinese") || l.Contains("中文") || l.Contains("汉语") || l.Contains("漢"))
        {
            return "chi";
        }

        if (l.Contains("english") || l.Contains("英"))
        {
            return "eng";
        }

        if (l.Contains("japan") || l.Contains("日"))
        {
            return "jpn";
        }

        if (l.Contains("korean") || l.Contains("韩") || l.Contains("韓"))
        {
            return "kor";
        }

        if (l.Contains("french") || l.Contains("法"))
        {
            return "fre";
        }

        if (l.Contains("german") || l.Contains("德"))
        {
            return "ger";
        }

        if (l.Contains("spanish") || l.Contains("西班牙"))
        {
            return "spa";
        }

        if (l.Contains("russian") || l.Contains("俄"))
        {
            return "rus";
        }

        return "und";
    }

    // ------------------------------------------------------------------ job control

    public GenerationJob? GetJob(Guid itemId) => _jobs.TryGetValue(itemId, out var j) ? j : null;

    public bool Cancel(Guid itemId)
    {
        if (_jobs.TryGetValue(itemId, out var job) && job.Status is "queued" or "running")
        {
            job.Cts.Cancel();
            job.Status = "cancelled";
            job.FinishedUtc = DateTime.UtcNow;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Ensure the pipeline is running (or has completed) for an item.
    /// force=true (实时轨) always starts a fresh pass from startPosition and overwrites.
    /// Returns immediately; work happens in the background.
    /// </summary>
    public GenerationJob? EnsureRunning(Guid itemId, double? startPosition, bool force, string? targetLanguage = null, bool translate = true)
    {
        var config = Config;
        var lang = string.IsNullOrWhiteSpace(targetLanguage) ? config.TargetLanguage : targetLanguage!;

        if (startPosition.HasValue)
        {
            ReportPlaybackPosition(itemId, startPosition.Value);
        }

        var start = startPosition ?? GetPlaybackPosition(itemId);

        // Serialize job control per item; events arrive concurrently.
        var controlLock = _itemLocks.GetOrAdd(itemId, _ => new SemaphoreSlim(1, 1));
        controlLock.Wait();
        try
        {
            if (_jobs.TryGetValue(itemId, out var existing))
            {
                if (existing.Status is "queued" or "running")
                {
                    if (!force)
                    {
                        return existing;
                    }

                    // live re-trigger while running: only restart on a real seek (>20s)
                    var runningStart = existing.StartSeconds;
                    if (Math.Abs(start - runningStart) <= 20)
                    {
                        return existing;
                    }

                    existing.Cts.Cancel();
                }
                else if (!force && existing.Status == "done" && IsComplete(itemId))
                {
                    return existing; // fully cached — no API calls
                }
            }

            var (path, streamIdx, duration) = ResolveMedia(itemId, null);
            var job = new GenerationJob
            {
                ItemId = itemId,
                TargetLanguage = lang,
                StartSeconds = start,
                ItemName = GetVideo(itemId).Name ?? itemId.ToString()
            };

            _jobs[itemId] = job;
            _ = Task.Run(() => RunPipelineAsync(job, path, streamIdx, duration, start, force, translate));
            return job;
        }
        finally
        {
            controlLock.Release();
        }
    }

    /// <summary>True when the on-disk cues cover the whole media duration.</summary>
    public bool IsComplete(Guid itemId)
    {
        try
        {
            var (_, _, duration) = ResolveMedia(itemId, null);
            if (duration <= 0)
            {
                return false;
            }

            var cues = _store.LoadCues(itemId, Config.TargetLanguage);
            return cues.Count > 0 && cues.Max(c => c.End) >= duration - Math.Max(30, duration * 0.02);
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ pipeline

    private async Task RunPipelineAsync(
        GenerationJob job, string mediaPath, int? audioStreamIndex,
        double duration, double startSeconds, bool force, bool translate)
    {
        var itemLock = _itemLocks.GetOrAdd(job.ItemId, _ => new SemaphoreSlim(1, 1));
        try
        {
            await itemLock.WaitAsync(job.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            job.Status = "cancelled";
            job.FinishedUtc = DateTime.UtcNow;
            return;
        }

        // A newer job may have superseded this one while we waited for the lock.
        if (job.Cts.IsCancellationRequested || !ReferenceEquals(GetJob(job.ItemId), job))
        {
            itemLock.Release();
            job.Status = "cancelled";
            job.FinishedUtc = DateTime.UtcNow;
            return;
        }

        var config = Config;
        var tempDir = Path.Combine(Path.GetTempPath(), "lmsubs-" + job.ItemId.ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var ct = job.Cts.Token;
            job.Status = "running";

            if (duration <= 0)
            {
                duration = await _extractor.GetDurationSecondsAsync(mediaPath, config, ct).ConfigureAwait(false);
            }

            if (duration <= 0)
            {
                throw new InvalidOperationException("无法确定媒体时长（ffprobe 不可用？可在配置里指定 ffmpeg 路径）");
            }

            var chunk = Math.Clamp(config.ChunkSeconds, 30, 3600);
            var chunkCount = (int)Math.Ceiling(duration / chunk);
            var startChunk = Math.Clamp((int)(Math.Max(0, startSeconds) / chunk), 0, chunkCount - 1);

            // Chunk order: current position first, then forward to the end, then wrap
            // around to cover everything before the start position.
            var order = Enumerable.Range(startChunk, chunkCount - startChunk)
                .Concat(Enumerable.Range(0, startChunk))
                .ToList();

            // Coverage from cached cues (normal mode). Force mode ignores the cache.
            var covered = new bool[chunkCount];
            if (!force)
            {
                var cachedCues = _store.LoadCues(job.ItemId, job.TargetLanguage);
                foreach (var i in order)
                {
                    var cs = (double)i * chunk;
                    var ce = Math.Min(cs + chunk, duration);
                    // A chunk counts as covered when cues reach its end (silent tails included).
                    covered[i] = cachedCues.Any(c => c.End >= ce - 1.0 && c.Start < ce);
                }
            }

            var pending = order.Where(i => !covered[i]).ToList();
            if (pending.Count == 0)
            {
                job.Status = "done";
                job.Progress = 1;
                job.Stage = "已全部缓存";
                job.FinishedUtc = DateTime.UtcNow;
                return;
            }

            var doneCount = chunkCount - pending.Count;
            var sourceCues = force ? new List<SubtitleCue>() : _store.LoadSourceCues(job.ItemId);
            var translatedCues = _store.LoadCues(job.ItemId, job.TargetLanguage);
            var context = new List<(string Src, string Dst)>();
            var ctxMax = Math.Clamp(config.ContextPairs, 0, 30);

            foreach (var i in pending)
            {
                ct.ThrowIfCancellationRequested();
                var cs = (double)i * chunk;
                var len = Math.Min(chunk, duration - cs);
                job.Stage = $"转写 {fmt(cs)}–{fmt(cs + len)}（{doneCount + 1}/{chunkCount}）";

                var file = Path.Combine(tempDir, $"chunk_{i:0000}.mp3");
                await _extractor.ExtractAsync(mediaPath, audioStreamIndex, cs, len, file, config, ct).ConfigureAwait(false);
                List<AsrSegment> segments;
                try
                {
                    segments = await _openAi.TranscribeAsync(file, cs, len, config, ct).ConfigureAwait(false);
                }
                finally
                {
                    try { File.Delete(file); } catch { /* best effort */ }
                }

                var cues = SubtitleText.Normalize(segments);
                if (cues.Count > 0)
                {
                    sourceCues.AddRange(cues);
                    _store.SaveSourceCues(job.ItemId, sourceCues);

                    var finalCues = translate
                        ? await TranslateCuesWithContextAsync(cues, job.TargetLanguage, context, ctxMax, ct).ConfigureAwait(false)
                        : cues;

                    // Merge into the served track; force mode replaces the chunk's time range.
                    translatedCues = MergeRange(translatedCues, finalCues, cs, cs + len, replace: force);
                    _store.SaveCues(job.ItemId, job.TargetLanguage, translatedCues);
                    _store.Render(job.ItemId, null, translated: true, live: false, job.TargetLanguage, translatedCues);
                    if (force)
                    {
                        _store.Render(job.ItemId, null, translated: true, live: true, job.TargetLanguage, translatedCues);
                    }
                }
                else if (force)
                {
                    // silent chunk: drop stale content in this range
                    translatedCues = MergeRange(translatedCues, Array.Empty<SubtitleCue>(), cs, cs + len, replace: true);
                    _store.SaveCues(job.ItemId, job.TargetLanguage, translatedCues);
                    _store.Render(job.ItemId, null, translated: true, live: false, job.TargetLanguage, translatedCues);
                    _store.Render(job.ItemId, null, translated: true, live: true, job.TargetLanguage, translatedCues);
                }

                doneCount++;
                job.Progress = (double)doneCount / chunkCount;
                _logger.LogDebug("LMSubs: {Item} chunk {Index}/{Total} done ({Count} cues)",
                    job.ItemName, i + 1, chunkCount, cues.Count);
            }

            // Final render: both track files always reflect the merged result.
            _store.Render(job.ItemId, null, translated: true, live: false, job.TargetLanguage, translatedCues);
            _store.Render(job.ItemId, null, translated: true, live: true, job.TargetLanguage, translatedCues);

            job.Status = "done";
            job.Progress = 1;
            job.Stage = "完成";
            job.FinishedUtc = DateTime.UtcNow;
            _logger.LogInformation("LMSubs: pipeline finished for {Item}", job.ItemName);
        }
        catch (OperationCanceledException)
        {
            job.Status = "cancelled";
            job.FinishedUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LMSubs: pipeline failed for {Item}", job.ItemName);
            job.Status = "error";
            job.Error = ex.Message;
            job.FinishedUtc = DateTime.UtcNow;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
            itemLock.Release();
        }

        static string fmt(double s) => TimeSpan.FromSeconds(s).ToString(@"hh\:mm\:ss");
    }

    /// <summary>Merge new cues; replace=true removes old cues inside [start,end) first (live overwrite).</summary>
    internal static List<SubtitleCue> MergeRange(List<SubtitleCue> existing, IEnumerable<SubtitleCue> incoming, double start, double end, bool replace)
    {
        var result = replace
            ? existing.Where(c => c.End <= start + 0.01 || c.Start >= end - 0.01).ToList()
            : existing.ToList();

        foreach (var c in incoming)
        {
            if (string.IsNullOrWhiteSpace(c.Text))
            {
                continue;
            }

            var dup = result.Any(e => Math.Abs(e.Start - c.Start) < 0.25 && string.Equals(e.Text, c.Text, StringComparison.Ordinal));
            if (!dup)
            {
                result.Add(c);
            }
        }

        return result.OrderBy(c => c.Start).ThenBy(c => c.End).ToList();
    }

    private async Task<List<SubtitleCue>> TranslateCuesWithContextAsync(
        List<SubtitleCue> cues, string targetLanguage,
        List<(string Src, string Dst)> context, int ctxMax, CancellationToken ct)
    {
        var config = Config;
        var batchSize = Math.Clamp(config.TranslateBatchSize, 1, 100);
        var output = new List<SubtitleCue>(cues.Count);

        for (var offset = 0; offset < cues.Count; offset += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = cues.Skip(offset).Take(batchSize).ToList();
            var texts = batch.Select(c => c.Text).ToList();
            var translations = await _openAi.TranslateBatchAsync(texts, context, targetLanguage, config, ct).ConfigureAwait(false);

            for (var i = 0; i < batch.Count; i++)
            {
                var src = batch[i].Text;
                var dst = i < translations.Count ? translations[i] : src;
                var text = string.Equals(config.BilingualMode, "replace", StringComparison.OrdinalIgnoreCase)
                    ? dst
                    : src + "\n" + dst;
                output.Add(batch[i] with { Text = text });

                if (ctxMax > 0 && !string.IsNullOrWhiteSpace(dst) && dst != src)
                {
                    context.Add((src, dst));
                    if (context.Count > ctxMax)
                    {
                        context.RemoveAt(0);
                    }
                }
            }
        }

        return output;
    }

    /// <summary>Translate the cached source subtitles in one pass (REST helper).</summary>
    public async Task<string> TranslateCachedAsync(Guid itemId, string? targetLanguage, CancellationToken ct)
    {
        var config = Config;
        var lang = string.IsNullOrWhiteSpace(targetLanguage) ? config.TargetLanguage : targetLanguage!;
        var cues = _store.LoadSourceCues(itemId);
        if (cues.Count == 0)
        {
            throw new FileNotFoundException("还没有原文字幕，请先选择「LM 字幕」开始转写");
        }

        var context = new List<(string Src, string Dst)>();
        var translated = await TranslateCuesWithContextAsync(cues, lang, context, Math.Clamp(config.ContextPairs, 0, 30), ct).ConfigureAwait(false);
        _store.SaveCues(itemId, lang, translated);
        _store.Render(itemId, null, translated: true, live: false, lang, translated);
        _store.Render(itemId, null, translated: true, live: true, lang, translated);
        return SubtitleText.ToSrt(translated);
    }
}
