// Loaded by jellyfin-web as an ES module via data-controller="__plugin/LmSubsConfigJs".
// Inline <script> in plugin pages is NOT executed (innerHTML insertion), hence this file.

const PLUGIN_ID = '9b1f2a44-3c5d-4e6f-8a7b-9c0d1e2f3a4b';

// Keep in sync with ProviderPresets.cs
const PROVIDERS = [
    { id: 'openai', name: 'OpenAI', url: 'https://api.openai.com/v1' },
    { id: 'siliconflow', name: 'SiliconFlow', url: 'https://api.siliconflow.cn/v1' },
    { id: 'deepseek', name: 'DeepSeek', url: 'https://api.deepseek.com/v1' },
    { id: 'moonshot', name: 'Moonshot (Kimi)', url: 'https://api.moonshot.cn/v1' },
    { id: 'zhipu', name: 'Zhipu (GLM)', url: 'https://open.bigmodel.cn/api/paas/v4' },
    { id: 'dashscope', name: 'DashScope (Qwen)', url: 'https://dashscope.aliyuncs.com/compatible-mode/v1' },
    { id: 'groq', name: 'Groq', url: 'https://api.groq.com/openai/v1' },
    { id: 'openrouter', name: 'OpenRouter', url: 'https://openrouter.ai/api/v1' },
    { id: 'together', name: 'Together', url: 'https://api.together.xyz/v1' },
    { id: 'ollama', name: 'Ollama (local)', url: 'http://localhost:11434/v1' },
    { id: 'lmstudio', name: 'LM Studio (local)', url: 'http://localhost:1234/v1' },
    { id: 'custom', name: 'OpenAI Compatible (custom)', url: '' }
];

const TEXT_FIELDS = [
    'AsrBaseUrl', 'AsrApiKey', 'AsrModel', 'AsrLanguage', 'AsrPrompt',
    'TranslateBaseUrl', 'TranslateApiKey', 'TranslateModel',
    'TranslateExtraPrompt', 'FfmpegPath'
];
const NUM_FIELDS = [
    'ChunkSeconds', 'MaxParallelChunks', 'ContextPairs', 'TranslateBatchSize',
    'AudioBitrateKbps', 'RequestTimeoutSeconds'
];
const SELECT_FIELDS = ['AsrProvider', 'TranslateProvider'];

export default function (view) {
    const $ = (sel) => view.querySelector(sel);

    function fillProviderSelect(sel) {
        sel.innerHTML = '';
        PROVIDERS.forEach((p) => {
            const opt = document.createElement('option');
            opt.value = p.id;
            opt.textContent = p.name;
            sel.appendChild(opt);
        });
    }

    function providerUrl(id) {
        const p = PROVIDERS.find((x) => x.id === id);
        return p ? p.url : '';
    }

    function bindProvider(providerSel, baseUrlInput) {
        const update = () => {
            const url = providerUrl(providerSel.value);
            baseUrlInput.placeholder = url || 'https://your-endpoint.example/v1';
        };
        providerSel.addEventListener('change', update);
        return update;
    }

    fillProviderSelect($('#AsrProvider'));
    fillProviderSelect($('#TranslateProvider'));
    const updateAsrPlaceholder = bindProvider($('#AsrProvider'), $('#AsrBaseUrl'));
    const updateTranslatePlaceholder = bindProvider($('#TranslateProvider'), $('#TranslateBaseUrl'));

    function load() {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(PLUGIN_ID).then((config) => {
            TEXT_FIELDS.forEach((k) => { $('#' + k).value = config[k] || ''; });
            NUM_FIELDS.forEach((k) => { $('#' + k).value = config[k]; });
            SELECT_FIELDS.forEach((k) => { $('#' + k).value = config[k] || 'custom'; });
            $('#BilingualMode').value = config.BilingualMode || 'bilingual';
            $('#AutoTranslate').checked = config.AutoTranslate !== false;
            updateAsrPlaceholder();
            updateTranslatePlaceholder();
            Dashboard.hideLoadingMsg();
        }).catch(() => Dashboard.hideLoadingMsg());
    }

    function collect() {
        const c = {};
        TEXT_FIELDS.forEach((k) => { c[k] = $('#' + k).value.trim(); });
        NUM_FIELDS.forEach((k) => { c[k] = parseInt($('#' + k).value, 10) || 0; });
        SELECT_FIELDS.forEach((k) => { c[k] = $('#' + k).value; });
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
            Dashboard.alert('Save failed: ' + err);
        });
        return false;
    });

    $('#LmSubsTestBtn').addEventListener('click', () => {
        const out = $('#LmSubsTestResult');
        out.textContent = 'Testing…';
        save().then(() => ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('LmSubs/Test'),
            dataType: 'json'
        })).then((r) => {
            out.textContent = 'ASR: ' + (r.asrOk ? '✓' : '✗ ' + (r.asrError || '')) +
                '   Translate: ' + (r.translateOk ? '✓' : '✗ ' + (r.translateError || ''));
        }).catch((err) => {
            Dashboard.hideLoadingMsg();
            out.textContent = 'Test failed: ' + err;
        });
    });

    load();
}
