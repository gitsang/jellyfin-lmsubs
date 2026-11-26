using Jellyfin.Plugin.LmSubs.Services;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.LmSubs.Providers;

// OpenSubtitles-style flow: Search lists one candidate per video; downloading it
// runs the full ASR + translation pipeline and returns the complete SRT, which
// Jellyfin's SubtitleManager saves and registers like any downloaded subtitle.
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
        // encode it into the download id together with the searched target language.
        var itemId = ResolveItemId(request);
        if (itemId == Guid.Empty)
        {
            return Task.FromResult(Enumerable.Empty<RemoteSubtitleInfo>());
        }

        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        var translate = config.AutoTranslate;
        var target = SubtitleService.NormalizeTargetLanguage(request.Language);
        var threeLetter = SubtitleService.ThreeLetterLanguage(target);

        var comment = $"ASR: {config.AsrModel}";
        if (translate)
        {
            comment += $"\nTranslate: {config.TranslateModel}\nTarget: {target}";
        }

        var job = _service.GetJob(itemId, translate ? target : "source");
        if (job is { Status: "queued" or "running" })
        {
            comment = $"ASR: {config.AsrModel} ({job.AsrProgress * 100:0}%)" +
                      (job.Translate
                          ? $"\nTranslate: {config.TranslateModel} ({job.TranslateProgress * 100:0}%)\nTarget: {job.TargetLanguage}"
                          : string.Empty);
        }
        else if (job is { Status: "done" })
        {
            comment += "\nReady (cached)";
        }
        else if (job is { Status: "error" })
        {
            comment += $"\nLast attempt failed: {job.Error}";
        }

        var info = new RemoteSubtitleInfo
        {
            Id = BuildId(itemId, threeLetter, translate),
            ProviderName = Name,
            Name = "LM Subtitles",
            Format = "srt",
            ThreeLetterISOLanguageName = threeLetter,
            Author = "LMSubs",
            Comment = comment,
            AiTranslated = translate
        };

        return Task.FromResult<IEnumerable<RemoteSubtitleInfo>>(new[] { info });
    }

    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        var (itemId, lang, translate) = ParseId(id);
        if (itemId == Guid.Empty)
        {
            throw new FileNotFoundException("Unknown LMSubs subtitle id: " + id);
        }

        var job = _service.EnsureRunning(itemId, translate, lang);

        // The pipeline outlives this request: if the client/proxy times out, generation
        // continues and a later download attempt picks up the finished result.
        await job.Completion.Task.ConfigureAwait(false);

        // A failed/cancelled job may still have a complete cached result from an earlier run.
        var srt = _service.Store.GetSrt(itemId, job.Translate, job.TargetLanguage);
        if (srt == null)
        {
            if (job.Status != "done")
            {
                throw new InvalidOperationException(job.Error ?? $"Generation {job.Status}");
            }

            throw new InvalidOperationException("Generation finished but no subtitle file was produced");
        }

        return new SubtitleResponse
        {
            Format = "srt",
            Language = SubtitleService.ThreeLetterLanguage(job.TargetLanguage),
            Stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(srt))
        };
    }

    // Download id: <itemId N>:<three-letter lang>:<t|s>. SubtitleManager splits the
    // id on '_' only, so ':' survives its provider-prefix handling.
    internal static string BuildId(Guid itemId, string lang, bool translate) =>
        $"{itemId:N}:{lang}:{(translate ? "t" : "s")}";

    internal static (Guid ItemId, string Lang, bool Translate) ParseId(string id)
    {
        var parts = (id ?? string.Empty).Split(':');
        if (!Guid.TryParse(parts[0], out var itemId))
        {
            return (Guid.Empty, "und", true);
        }

        var lang = parts.Length > 1 && parts[1].Length == 3 ? parts[1] : "und";
        var translate = parts.Length <= 2 || parts[2] != "s";
        return (itemId, lang, translate);
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