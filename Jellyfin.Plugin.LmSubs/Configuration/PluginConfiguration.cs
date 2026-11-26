using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LmSubs.Configuration;

/// <summary>
/// Plugin configuration. All endpoints are OpenAI-compatible.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ---------------- ASR (speech -> text) ----------------

    /// <summary>OpenAI-compatible base URL for ASR, e.g. https://api.openai.com/v1 (plugin appends /audio/transcriptions).</summary>
    public string AsrBaseUrl { get; set; } = "https://api.openai.com/v1";

    public string AsrApiKey { get; set; } = string.Empty;

    /// <summary>ASR model id, e.g. whisper-1, gpt-4o-transcribe, SenseVoiceSmall.</summary>
    public string AsrModel { get; set; } = "whisper-1";

    /// <summary>Optional source-language hint (ISO-639-1, e.g. en / ja). Empty = auto detect.</summary>
    public string AsrLanguage { get; set; } = string.Empty;

    /// <summary>Optional ASR prompt (terminology / style hints).</summary>
    public string AsrPrompt { get; set; } = string.Empty;

    /// <summary>Seconds of audio per ASR request (chunks are cut with ffmpeg).</summary>
    public int ChunkSeconds { get; set; } = 600;

    /// <summary>Concurrent ASR chunk requests.</summary>
    public int MaxParallelChunks { get; set; } = 2;

    // ---------------- Translation (LLM) ----------------

    /// <summary>OpenAI-compatible base URL for translation (plugin appends /chat/completions).</summary>
    public string TranslateBaseUrl { get; set; } = "https://api.openai.com/v1";

    public string TranslateApiKey { get; set; } = string.Empty;

    public string TranslateModel { get; set; } = "gpt-4o-mini";

    /// <summary>Default target language for generated translations.</summary>
    public string TargetLanguage { get; set; } = "Chinese (Simplified)";

    /// <summary>Rolling context: keep the last N "source -> translation" pairs so names/terms stay consistent.</summary>
    public int ContextPairs { get; set; } = 8;

    /// <summary>Subtitle lines per translation request.</summary>
    public int TranslateBatchSize { get; set; } = 20;

    // ---------------- Behavior ----------------

    /// <summary>Automatically queue a translation after full generation finishes.</summary>
    public bool AutoTranslate { get; set; } = true;

    /// <summary>Translation style: "bilingual" (source + translation on two lines) or "replace".</summary>
    public string BilingualMode { get; set; } = "bilingual";

    /// <summary>Custom ffmpeg binary path. Empty = use Jellyfin's configured encoder path, then "ffmpeg" from PATH.</summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>Audio bitrate (kbps) for extracted ASR chunks (16 kHz mono mp3).</summary>
    public int AudioBitrateKbps { get; set; } = 48;

    /// <summary>HTTP timeout in seconds for a single ASR / translation request.</summary>
    public int RequestTimeoutSeconds { get; set; } = 300;

    /// <summary>Extra instruction appended to the translation system prompt.</summary>
    public string TranslateExtraPrompt { get; set; } = string.Empty;
}
