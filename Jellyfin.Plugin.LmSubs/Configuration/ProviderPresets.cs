namespace Jellyfin.Plugin.LmSubs.Configuration;

public record ProviderPreset(string Id, string Name, string BaseUrl);

public static class ProviderPresets
{
    public const string CustomId = "custom";

    public static readonly ProviderPreset[] All =
    {
        new("openai", "OpenAI", "https://api.openai.com/v1"),
        new("siliconflow", "SiliconFlow", "https://api.siliconflow.cn/v1"),
        new("deepseek", "DeepSeek", "https://api.deepseek.com/v1"),
        new("moonshot", "Moonshot (Kimi)", "https://api.moonshot.cn/v1"),
        new("zhipu", "Zhipu (GLM)", "https://open.bigmodel.cn/api/paas/v4"),
        new("dashscope", "DashScope (Qwen)", "https://dashscope.aliyuncs.com/compatible-mode/v1"),
        new("groq", "Groq", "https://api.groq.com/openai/v1"),
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1"),
        new("together", "Together", "https://api.together.xyz/v1"),
        new("ollama", "Ollama (local)", "http://localhost:11434/v1"),
        new("lmstudio", "LM Studio (local)", "http://localhost:1234/v1"),
        new(CustomId, "OpenAI Compatible (custom)", string.Empty)
    };

    public static string DefaultBaseUrl(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return string.Empty;
        }

        foreach (var p in All)
        {
            if (string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase))
            {
                return p.BaseUrl;
            }
        }

        return string.Empty;
    }
}
