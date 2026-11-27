using Jellyfin.Plugin.LmSubs.Services;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.LmSubs.Providers;

// OpenSubtitles-style flow: Search lists three candidates (source / translated /
// bilingual) sharing one ASR pass; downloading one runs its pipeline and returns
// the complete SRT, which Jellyfin's SubtitleManager saves and registers.
public class LmSubtitleProvider : ISubtitleProvider
{
    private readonly SubtitleService _service;

    public LmSubtitleProvider(SubtitleService service)
    {
        _service = service;
    }

    public string Name => "LMSubs";

    public IEnumerable<VideoContentType> SupportedMediaTypes =>
        new[] { VideoContentType.Movie, VideoContentType.Episode };

    public Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        // SubtitleSearchRequest carries no item id; resolve it from MediaPath and
        // encode it into the download id together with language and mode.
        var itemId = ResolveItemId(request);
        if (itemId == Guid.Empty)
        {
            return Task.FromResult(Enumerable.Empty<RemoteSubtitleInfo>());
        }

        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        var target = SubtitleService.NormalizeTargetLanguage(request.Language);
        var targetCode = SubtitleService.ThreeLetterLanguage(target);
        var targetSlug = SubtitleStore.LangSlug(target);
        var sourceCode = _service.SourceLanguageCode(itemId);

        var infos = new List<RemoteSubtitleInfo>
        {
            BuildInfo(itemId, "source", sourceCode, targetSlug, "LM Subtitles - Source",
                $"ASR: {config.AsrModel}", aiTranslated: false),
            BuildInfo(itemId, "translated", targetCode, targetSlug, "LM Subtitles - Translate",
                $"ASR: {config.AsrModel}\nTranslate: {config.TranslateModel}\nTarget: {target}", aiTranslated: true),
            BuildInfo(itemId, "bilingual", targetCode, targetSlug, "LM Subtitles - Bilingual",
                $"ASR: {config.AsrModel}\nTranslate: {config.TranslateModel}\nTarget: {target}", aiTranslated: true)
        };

        return Task.FromResult<IEnumerable<RemoteSubtitleInfo>>(infos);
    }

    private RemoteSubtitleInfo BuildInfo(Guid itemId, string mode, string langCode, string langSlug, string name, string comment, bool aiTranslated)
    {
        // translated and bilingual share one job (Mode=bilingual) and one bi- cache.
        var jobMode = mode == "source" ? "source" : "bilingual";
        var job = _service.AllJobs().FirstOrDefault(j => j.ItemId == itemId && j.Mode == jobMode &&
            (mode == "source" || SubtitleService.ThreeLetterLanguage(j.TargetLanguage) == langCode));
        var session = _service.GetSession(itemId);

        var asrDone = session is { Status: "done" } || _service.Store.GetSrtKeyed(itemId, "source") != null;
        var translateDone = job is { Status: "done" } || _service.Store.GetAnyTranslationSrt(itemId, langSlug) != null;

        if (job is { Status: "queued" or "running" })
        {
            comment = comment.Replace("ASR: ", $"ASR ({job.AsrProgress * 100:0}%): ", StringComparison.Ordinal);
            if (job.Translate)
            {
                comment = comment.Replace("Translate: ", $"Translate ({job.TranslateProgress * 100:0}%): ", StringComparison.Ordinal);
            }
        }
        else
        {
            if (session is { Status: "queued" or "running" } && mode == "source")
            {
                comment = comment.Replace("ASR: ", $"ASR ({session.Progress * 100:0}%): ", StringComparison.Ordinal);
            }
            else if (asrDone)
            {
                comment = comment.Replace("ASR: ", "ASR (Completed): ", StringComparison.Ordinal);
            }

            if (mode != "source" && translateDone)
            {
                comment = comment.Replace("Translate: ", "Translate (Completed): ", StringComparison.Ordinal);
            }
        }

        if (job is { Status: "error" })
        {
            comment += $"\nLast attempt failed: {job.Error}";
        }

        return new RemoteSubtitleInfo
        {
            Id = BuildId(itemId, langCode, mode),
            ProviderName = Name,
            Name = name,
            Format = "srt",
            ThreeLetterISOLanguageName = langCode,
            Author = "LMSubs",
            Comment = comment,
            AiTranslated = aiTranslated
        };
    }

    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        var (itemId, lang, mode) = ParseId(id);
        if (itemId == Guid.Empty)
        {
            throw new FileNotFoundException("Unknown LMSubs subtitle id: " + id);
        }

        // Cache hit: return immediately without starting or waiting on any job.
        // Translated and bilingual share the paired bi- cache; translated strips the source line.
        var langSlug = SubtitleStore.LangSlug(SubtitleService.NormalizeTargetLanguage(lang));
        var cached = mode == "source"
            ? _service.Store.GetSrtKeyed(itemId, "source")
            : _service.Store.GetAnyTranslationSrt(itemId, langSlug);
        if (cached != null)
        {
            // Legacy translated.<lang>.srt is already single-mode; only strip pairs
            // when the content came from the bi- cache. Detect by first cue shape.
            var fromPairs = _service.Store.GetSrtKeyed(itemId, "bi-" + langSlug) != null;
            return Respond(cached, itemId, lang, mode == "translated" && fromPairs ? "translated" : mode == "translated" ? "translated-raw" : mode);
        }

        // Ids carry ISO codes; job keys use the prompt-friendly language name.
        var job = _service.EnsureRunning(itemId, out var created, mode,
            mode == "source" ? null : SubtitleService.NormalizeTargetLanguage(lang));

        // Debounce: an in-flight download already waits on this job and will save the
        // subtitle when it completes. A second waiter would save a duplicate track.
        if (!created && job.Status is "queued" or "running")
        {
            throw new InvalidOperationException("Generation already in progress — search again to see progress; the subtitle is saved automatically when it finishes.");
        }

        // The pipeline outlives this request: if the client/proxy times out, generation
        // continues and a later download attempt picks up the finished result.
        await job.Completion.Task.ConfigureAwait(false);

        // A failed/cancelled job may still have a complete cached result from an earlier run.
        var srt = _service.Store.GetSrtKeyed(itemId, job.CacheKey);
        if (srt == null)
        {
            if (job.Status != "done")
            {
                throw new InvalidOperationException(job.Error ?? $"Generation {job.Status}");
            }

            throw new InvalidOperationException("Generation finished but no subtitle file was produced");
        }

        return Respond(srt, itemId, lang, mode);
    }

    private SubtitleResponse Respond(string srt, Guid itemId, string lang, string mode)
    {
        if (mode == "translated")
        {
            srt = SubtitleText.ToSrt(SubtitleText.ParseSrt(srt).Select(TranslationOnly));
        }
        // "translated-raw": legacy single-mode cache, serve as-is.

        return new SubtitleResponse
        {
            Format = "srt",
            Language = mode == "source" ? _service.SourceLanguageCode(itemId) : lang,
            Stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(srt))
        };
    }

    // Paired cues are "src\ndst"; keep only the translation line(s).
    internal static SubtitleCue TranslationOnly(SubtitleCue cue)
    {
        var idx = cue.Text.IndexOf('\n');
        return idx >= 0 ? cue with { Text = cue.Text[(idx + 1)..] } : cue;
    }

    internal static string ModeCacheKey(string mode, string langSlug) => mode switch
    {
        "source" => "source",
        "bilingual" => "bi-" + langSlug,
        _ => langSlug
    };

    // Download id: <itemId N>:<lang>:<s|t|b>. SubtitleManager splits the id on '_'
    // only, so ':' survives its provider-prefix handling.
    internal static string BuildId(Guid itemId, string lang, string mode) =>
        $"{itemId:N}:{lang}:{mode switch { "source" => "s", "bilingual" => "b", _ => "t" }}";

    internal static (Guid ItemId, string Lang, string Mode) ParseId(string id)
    {
        var parts = (id ?? string.Empty).Split(':');
        if (!Guid.TryParse(parts[0], out var itemId))
        {
            return (Guid.Empty, "und", "translated");
        }

        var lang = parts.Length > 1 && parts[1].Length == 3 ? parts[1] : "und";
        var mode = parts.Length > 2
            ? parts[2] switch { "s" => "source", "b" => "bilingual", _ => "translated" }
            : "translated";
        return (itemId, lang, mode);
    }

    private Guid ResolveItemId(SubtitleSearchRequest request)
    {
        if (request.ProviderIds != null &&
            request.ProviderIds.TryGetValue("LmSubsItemId", out var raw) &&
            Guid.TryParse(raw, out var fromIds))
        {
            return fromIds;
        }

        return _service.FindItemIdByMediaPath(request.MediaPath);
    }
}