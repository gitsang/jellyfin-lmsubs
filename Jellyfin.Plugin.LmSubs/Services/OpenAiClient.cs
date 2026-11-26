using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.LmSubs.Configuration;

namespace Jellyfin.Plugin.LmSubs.Services;

public record AsrSegment(double StartSeconds, double EndSeconds, string Text);

public class OpenAiClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;

    public OpenAiClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public static string BuildEndpoint(string baseUrl, string suffix)
    {
        var b = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (b.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return b;
        }

        return b + suffix;
    }

    private HttpClient CreateClient(string apiKey, int timeoutSeconds)
    {
        var client = _httpClientFactory.CreateClient("LmSubs");
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 30, 3600));
        client.DefaultRequestHeaders.Authorization =
            string.IsNullOrWhiteSpace(apiKey) ? null : new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    public async Task<List<AsrSegment>> TranscribeAsync(
        string audioFilePath,
        double chunkOffsetSeconds,
        double chunkDurationSeconds,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var url = BuildEndpoint(config.EffectiveAsrBaseUrl, "/audio/transcriptions");
        using var client = CreateClient(config.AsrApiKey, config.RequestTimeoutSeconds);

        async Task<HttpResponseMessage> PostAsync(string responseFormat)
        {
            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(await File.ReadAllBytesAsync(audioFilePath, cancellationToken).ConfigureAwait(false));
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            form.Add(fileContent, "file", Path.GetFileName(audioFilePath));
            form.Add(new StringContent(config.AsrModel), "model");
            form.Add(new StringContent(responseFormat), "response_format");
            if (!string.IsNullOrWhiteSpace(config.AsrLanguage))
            {
                form.Add(new StringContent(config.AsrLanguage), "language");
            }

            if (!string.IsNullOrWhiteSpace(config.AsrPrompt))
            {
                form.Add(new StringContent(config.AsrPrompt), "prompt");
            }

            return await client.PostAsync(url, form, cancellationToken).ConfigureAwait(false);
        }

        var response = await PostAsync("verbose_json").ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Some servers reject verbose_json or the timestamps parameter; retry as plain json/text.
            response = await PostAsync("json").ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"ASR HTTP {(int)response.StatusCode}: {Truncate(body, 500)}");
            }
        }

        var segments = ParseTranscription(body, chunkOffsetSeconds, chunkDurationSeconds);
        return segments;
    }

    internal static List<AsrSegment> ParseTranscription(string body, double offset, double duration)
    {
        var result = new List<AsrSegment>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in segs.EnumerateArray())
                {
                    var text = s.TryGetProperty("text", out var t) ? t.GetString() : null;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    var start = s.TryGetProperty("start", out var st) && st.TryGetDouble(out var sv) ? sv : 0;
                    var end = s.TryGetProperty("end", out var en) && en.TryGetDouble(out var ev) ? ev : start + 2;
                    result.Add(new AsrSegment(offset + start, offset + Math.Max(end, start + 0.2), text.Trim()));
                }
            }

            if (result.Count == 0 && root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("text", out var whole) && whole.ValueKind == JsonValueKind.String)
            {
                var text = whole.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    result.Add(new AsrSegment(offset, offset + duration, text.Trim()));
                }
            }
        }
        catch (JsonException)
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                result.Add(new AsrSegment(offset, offset + duration, body.Trim()));
            }
        }

        return result;
    }

    public async Task<List<string>> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        IReadOnlyList<(string Src, string Dst)> context,
        string targetLanguage,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
        {
            return new List<string>();
        }

        var url = BuildEndpoint(config.EffectiveTranslateBaseUrl, "/chat/completions");
        using var client = CreateClient(config.TranslateApiKey, config.RequestTimeoutSeconds);

        var system = new StringBuilder();
        system.Append("You are a professional subtitle translator. Translate each numbered subtitle line into ")
              .Append(targetLanguage).Append(". Rules: keep the numbering exactly; translate every line; ")
              .Append("keep tone and register; do not merge, split, explain or censor; keep names consistent with the context. ")
              .Append("Return ONLY a JSON object mapping the line number (string) to the translation, e.g. {\"1\":\"...\",\"2\":\"...\"}.");
        if (!string.IsNullOrWhiteSpace(config.TranslateExtraPrompt))
        {
            system.Append(' ').Append(config.TranslateExtraPrompt);
        }

        var messages = new List<object> { new { role = "system", content = system.ToString() } };

        if (context.Count > 0)
        {
            var ctxInput = new Dictionary<string, string>();
            var ctxOutput = new Dictionary<string, string>();
            for (var i = 0; i < context.Count; i++)
            {
                ctxInput[(i + 1).ToString()] = context[i].Src;
                ctxOutput[(i + 1).ToString()] = context[i].Dst;
            }

            messages.Add(new { role = "user", content = JsonSerializer.Serialize(ctxInput) });
            messages.Add(new { role = "assistant", content = JsonSerializer.Serialize(ctxOutput) });
        }

        var input = new Dictionary<string, string>();
        for (var i = 0; i < texts.Count; i++)
        {
            input[(i + 1).ToString()] = texts[i];
        }

        messages.Add(new { role = "user", content = JsonSerializer.Serialize(input) });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = config.TranslateModel,
            ["messages"] = messages,
            ["temperature"] = 0.2
        };

        var content = await ChatAsync(client, url, payload, cancellationToken).ConfigureAwait(false);
        var map = TryParseNumberedJson(content, texts.Count);
        if (map != null)
        {
            return map;
        }

        messages[^1] = new
        {
            role = "user",
            content = "Translate each line. Reply with exactly one line per input in the form `N. translation`, no extra text.\n" +
                      string.Join("\n", texts.Select((t, i) => $"{i + 1}. {t}"))
        };
        payload["response_format"] = null;
        content = await ChatAsync(client, url, payload, cancellationToken).ConfigureAwait(false);
        var lined = ParseNumberedLines(content, texts.Count);
        if (lined != null)
        {
            return lined;
        }

        var results = new List<string>(texts.Count);
        foreach (var t in texts)
        {
            try
            {
                var singleMessages = new List<object>
                {
                    new { role = "system", content = system + " Reply with the translation only, no numbering." },
                    new { role = "user", content = t }
                };
                var singlePayload = new Dictionary<string, object?>
                {
                    ["model"] = config.TranslateModel,
                    ["messages"] = singleMessages,
                    ["temperature"] = 0.2
                };
                var one = await ChatAsync(client, url, singlePayload, cancellationToken).ConfigureAwait(false);
                results.Add(string.IsNullOrWhiteSpace(one) ? t : one.Trim());
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                results.Add(t); // keep source text rather than losing the line
            }
        }

        return results;
    }

    private static async Task<string> ChatAsync(HttpClient client, string url, Dictionary<string, object?> payload, CancellationToken ct)
    {
        using var req = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");
        var resp = await client.PostAsync(url, req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Translate HTTP {(int)resp.StatusCode}: {Truncate(body, 500)}");
        }

        using var doc = JsonDocument.Parse(body);
        var choice = doc.RootElement.GetProperty("choices")[0];
        var message = choice.GetProperty("message");
        return message.GetProperty("content").GetString() ?? string.Empty;
    }

    internal static List<string>? TryParseNumberedJson(string content, int count)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
            {
                trimmed = trimmed.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var result = new List<string>(count);
            for (var i = 1; i <= count; i++)
            {
                if (doc.RootElement.TryGetProperty(i.ToString(), out var v) && v.ValueKind == JsonValueKind.String)
                {
                    result.Add(v.GetString() ?? string.Empty);
                }
                else
                {
                    return null;
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static List<string>? ParseNumberedLines(string content, int count)
    {
        var dict = new Dictionary<int, string>();
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            var dot = line.IndexOfAny(new[] { '.', '、', ':', '：' });
            if (dot <= 0 || dot > 5)
            {
                continue;
            }

            if (int.TryParse(line.AsSpan(0, dot).Trim(), out var n) && n >= 1 && n <= count)
            {
                dict[n] = line.Substring(dot + 1).Trim();
            }
        }

        if (dict.Count != count)
        {
            return null;
        }

        return Enumerable.Range(1, count).Select(i => dict[i]).ToList();
    }

    public async Task<(bool Ok, string? Error)> TestAsrAsync(PluginConfiguration config, CancellationToken ct)
    {
        // Probe /models (cheap); ASR-only servers may 404 it, so only auth errors are fatal.
        try
        {
            var url = BuildEndpoint(config.EffectiveAsrBaseUrl, "/models");
            using var client = CreateClient(config.AsrApiKey, 30);
            var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                return (true, null);
            }

            var status = (int)resp.StatusCode;
            if (status is 401 or 403)
            {
                return (false, $"HTTP {status} (invalid API key?)");
            }

            return (true, $"HTTP {status} — /models unavailable, transcription may still work");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Ok, string? Error)> TestTranslateAsync(PluginConfiguration config, CancellationToken ct)
    {
        try
        {
            var url = BuildEndpoint(config.EffectiveTranslateBaseUrl, "/chat/completions");
            using var client = CreateClient(config.TranslateApiKey, 60);
            var payload = new
            {
                model = config.TranslateModel,
                messages = new[] { new { role = "user", content = "Reply with exactly: OK" } },
                max_tokens = 8
            };
            using var req = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(url, req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode ? (true, null) : (false, $"HTTP {(int)resp.StatusCode}: {Truncate(body, 200)}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}
