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
        // SubtitleSearchRequest carries no item id; the REST search endpoint resolves
        // the item from MediaPath. Encode it into the download id.
        var itemId = ResolveItemId(request);
        if (itemId == Guid.Empty)
        {
            return Task.FromResult(Enumerable.Empty<RemoteSubtitleInfo>());
        }

        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        var info = new RemoteSubtitleInfo
        {
            Id = itemId.ToString("N"),
            ProviderName = Name,
            Name = $"LM Subtitles (ASR + {config.TranslateModel})",
            Format = "srt",
            ThreeLetterISOLanguageName = SubtitleService.ThreeLetterLanguage(config.TargetLanguage),
            Author = "LMSubs",
            Comment = $"Generated on demand: {config.AsrModel} → {config.TranslateModel}, target: {config.TargetLanguage}",
            AiTranslated = true
        };

        return Task.FromResult<IEnumerable<RemoteSubtitleInfo>>(new[] { info });
    }

    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var itemId))
        {
            throw new FileNotFoundException("Unknown LMSubs subtitle id: " + id);
        }

        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        var job = _service.EnsureRunning(itemId, config.AutoTranslate);

        // The pipeline outlives this request: if the client/proxy times out, generation
        // continues and a later download attempt picks up the finished result.
        await job.Completion.Task.ConfigureAwait(false);

        if (job.Status != "done")
        {
            throw new InvalidOperationException(job.Error ?? $"Generation {job.Status}");
        }

        var srt = _service.Store.GetSrt(itemId, true, job.TargetLanguage)
                  ?? throw new InvalidOperationException("Generation finished but no subtitle file was produced");

        return new SubtitleResponse
        {
            Format = "srt",
            Language = SubtitleService.ThreeLetterLanguage(job.TargetLanguage),
            Stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(srt))
        };
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
