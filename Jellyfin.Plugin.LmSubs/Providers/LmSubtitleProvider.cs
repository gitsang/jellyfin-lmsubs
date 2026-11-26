using Jellyfin.Plugin.LmSubs.Services;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.LmSubs.Providers;

/// <summary>
/// Subtitle provider entry for LMSubs. The tracks are installed directly as external
/// MediaStreams (see LmTrackInstaller); this provider exists so LMSubs is a registered
/// subtitle source and can resolve "lmsubs://itemId/live|cached" ids if a client ever
/// routes through GetRemoteSubtitles.
/// </summary>
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
        => Task.FromResult(Enumerable.Empty<RemoteSubtitleInfo>());

    public Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        // id format: lmsubs://<itemIdN>/live|cached
        if (string.IsNullOrEmpty(id) || !id.StartsWith("lmsubs://", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("未知的 LMSubs 字幕 id: " + id);
        }

        var parts = id.Substring("lmsubs://".Length).Split('/');
        if (!Guid.TryParse(parts[0], out var itemId))
        {
            throw new FileNotFoundException("未知的 LMSubs 字幕 id: " + id);
        }

        var live = parts.Length > 1 && parts[1].Equals("live", StringComparison.OrdinalIgnoreCase);
        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

        // Resolving the subtitle IS the trigger.
        _service.EnsureRunning(itemId, null, force: live);

        var vtt = _service.Store.GetVtt(itemId, true, config.TargetLanguage, live) ?? "WEBVTT\n\n";
        return Task.FromResult(new SubtitleResponse
        {
            Format = "vtt",
            Language = SubtitleService.ThreeLetterLanguage(config.TargetLanguage),
            Stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(vtt))
        });
    }
}
