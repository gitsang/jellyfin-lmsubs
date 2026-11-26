// LMSubs config page controller.
// jellyfin-web loads this as an ES module via data-controller="__plugin/LmSubsConfigJs"
// and instantiates `new default(view, params)` on every page show.

const PLUGIN_ID = '9b1f2a44-3c5d-4e6f-8a7b-9c0d1e2f3a4b';

const TEXT_FIELDS = [
    'AsrBaseUrl', 'AsrApiKey', 'AsrModel', 'AsrLanguage', 'AsrPrompt',
    'TranslateBaseUrl', 'TranslateApiKey', 'TranslateModel', 'TargetLanguage',
    'TranslateExtraPrompt', 'FfmpegPath'
];
const NUM_FIELDS = [
    'ChunkSeconds', 'MaxParallelChunks', 'ContextPairs', 'TranslateBatchSize',
    'AudioBitrateKbps', 'RequestTimeoutSeconds'
];

export default function (view) {
    const $ = (sel) => view.querySelector(sel);

    function load() {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(PLUGIN_ID).then((config) => {
            TEXT_FIELDS.forEach((k) => { $('#' + k).value = config[k] || ''; });
            NUM_FIELDS.forEach((k) => { $('#' + k).value = config[k]; });
            $('#BilingualMode').value = config.BilingualMode || 'bilingual';
            $('#AutoTranslate').checked = config.AutoTranslate !== false;
            Dashboard.hideLoadingMsg();
        }).catch(() => Dashboard.hideLoadingMsg());
    }

    function collect() {
        const c = {};
        TEXT_FIELDS.forEach((k) => { c[k] = $('#' + k).value.trim(); });
        NUM_FIELDS.forEach((k) => { c[k] = parseInt($('#' + k).value, 10) || 0; });
        c.BilingualMode = $('#BilingualMode').value;
        c.AutoTranslate = $('#AutoTranslate').checked;
        return c;
    }

    function save() {
        Dashboard.showLoadingMsg();
        return ApiClient.getPluginConfiguration(PLUGIN_ID).then((config) => {
            Object.assign(config, collect());
            return ApiClient.updatePluginConfiguration(PLUGIN_ID, config);
        });
    }

    $('#LmSubsConfigForm').addEventListener('submit', (e) => {
        e.preventDefault();
        save().then((result) => {
            Dashboard.processPluginConfigurationUpdateResult(result);
        }).catch((err) => {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('保存失败: ' + err);
        });
        return false;
    });

    $('#LmSubsTestBtn').addEventListener('click', () => {
        const out = $('#LmSubsTestResult');
        out.textContent = '测试中…';
        // Save first so the server tests the current form values.
        save().then(() => ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('LmSubs/Test'),
            dataType: 'json'
        })).then((r) => {
            out.textContent = 'ASR: ' + (r.asrOk ? '✓' : '✗ ' + (r.asrError || '')) +
                '   翻译: ' + (r.translateOk ? '✓' : '✗ ' + (r.translateError || ''));
        }).catch((err) => {
            Dashboard.hideLoadingMsg();
            out.textContent = '测试失败: ' + err;
        });
    });

    load();
}
