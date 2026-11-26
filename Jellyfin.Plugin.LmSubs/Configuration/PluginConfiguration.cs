using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LmSubs.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{

    public string AsrProvider { get; set; } = "openai";

    public string AsrBaseUrl { get; set; } = string.Empty;

    public string AsrApiKey { get; set; } = string.Empty;

    public string AsrModel { get; set; } = "whisper-1";

    public string AsrLanguage { get; set; } = string.Empty;

    public string AsrPrompt { get; set; } = string.Empty;

    public int ChunkSeconds { get; set; } = 600;

    public string TranslateProvider { get; set; } = "openai";

    public string TranslateBaseUrl { get; set; } = string.Empty;

    public string TranslateApiKey { get; set; } = string.Empty;

    public string TranslateModel { get; set; } = "gpt-4o-mini";

    public int ContextPairs { get; set; } = 8;

    public int TranslateBatchSize { get; set; } = 20;

    // Estimated input-token budget per translate request (system + context + lines). 0 = unlimited.
    public int ContextSize { get; set; } = 6000;

    public bool AutoTranslate { get; set; } = true;

    public string BilingualMode { get; set; } = "bilingual";

    public string FfmpegPath { get; set; } = string.Empty;

    public int AudioBitrateKbps { get; set; } = 48;

    public int RequestTimeoutSeconds { get; set; } = 300;

    public string TranslateExtraPrompt { get; set; } = string.Empty;

    // Empty = follow the server's log level. Otherwise Debug/Information/Warning/Error/None.
    public string LogLevel { get; set; } = string.Empty;

    public string EffectiveAsrBaseUrl =>
        string.IsNullOrWhiteSpace(AsrBaseUrl) ? ProviderPresets.DefaultBaseUrl(AsrProvider) : AsrBaseUrl;

    public string EffectiveTranslateBaseUrl =>
        string.IsNullOrWhiteSpace(TranslateBaseUrl) ? ProviderPresets.DefaultBaseUrl(TranslateProvider) : TranslateBaseUrl;
}
