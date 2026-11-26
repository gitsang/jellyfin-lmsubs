using Jellyfin.Plugin.LmSubs.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LmSubs.Providers;

// Track files live in the plugin cache, never in the media directory: a library
// re-scan wipes DB streams whose files vanished, and media dirs are often read-only.
public static class LmTrackInstaller
{
    public const string Marker = ".lmsubs";

    public static bool IsLmPath(string? path) =>
        !string.IsNullOrEmpty(path) &&
        (path.Contains(Marker + ".", StringComparison.OrdinalIgnoreCase) ||
         path.Contains(Marker + "-live.", StringComparison.OrdinalIgnoreCase));

    public static bool IsLivePath(string? path) =>
        !string.IsNullOrEmpty(path) && path.Contains(Marker + "-live.", StringComparison.OrdinalIgnoreCase);

    public static bool TryIdentify(IItemRepository itemRepo, Guid itemId, int? streamIndex, out bool live)
    {
        live = false;
        if (!streamIndex.HasValue || streamIndex.Value < 0)
        {
            return false;
        }

        var stream = itemRepo.GetMediaStreams(new MediaStreamQuery { ItemId = itemId })
            .FirstOrDefault(s => s.Index == streamIndex.Value && s.Type == MediaStreamType.Subtitle);
        if (stream == null)
        {
            return false;
        }

        if (IsLmPath(stream.Path))
        {
            live = IsLivePath(stream.Path);
            return true;
        }

        if (string.Equals(stream.Title, "LM Subtitles", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(stream.Title, "LM Subtitles (Live)", StringComparison.Ordinal))
        {
            live = true;
            return true;
        }

        return false;
    }

    public static bool EnsureTracks(
        Video video,
        IItemRepository itemRepo,
        SubtitleStore store,
        string targetLanguage,
        ILogger logger)
    {
        if (string.IsNullOrEmpty(video.Path))
        {
            return false;
        }

        var streams = itemRepo.GetMediaStreams(new MediaStreamQuery { ItemId = video.Id });
        var langCode = SubtitleService.ThreeLetterLanguage(targetLanguage);
        var changed = false;
        var maxIndex = streams.Count == 0 ? -1 : streams.Max(s => s.Index);

        foreach (var live in new[] { false, true })
        {
            var title = live ? "LM Subtitles (Live)" : "LM Subtitles";
            var path = store.CacheSrtPath(video.Id, true, live, targetLanguage);

            if (!File.Exists(path))
            {
                File.WriteAllText(path, string.Empty);
            }

            var existing = streams.FirstOrDefault(s =>
                s.Type == MediaStreamType.Subtitle &&
                (string.Equals(s.Title, title, StringComparison.Ordinal) ||
                 (IsLmPath(s.Path) && IsLivePath(s.Path) == live)));

            if (existing != null)
            {
                if (existing.Path != path || existing.Title != title || existing.Language != langCode)
                {
                    existing.Path = path;
                    existing.Title = title;
                    existing.Language = langCode;
                    existing.Codec = "srt";
                    existing.IsExternal = true;
                    existing.DeliveryMethod = SubtitleDeliveryMethod.External;
                    changed = true;
                }

                continue;
            }

            maxIndex++;
            streams.Add(new MediaStream
            {
                Type = MediaStreamType.Subtitle,
                Index = maxIndex,
                Codec = "srt",
                Language = langCode,
                Title = title,
                IsExternal = true,
                IsDefault = false,
                IsForced = false,
                IsHearingImpaired = false,
                SupportsExternalStream = true,
                DeliveryMethod = SubtitleDeliveryMethod.External,
                IsExternalUrl = false,
                Path = path
            });
            changed = true;
        }

        if (changed)
        {
            itemRepo.SaveMediaStreams(video.Id, streams, CancellationToken.None);
            logger.LogInformation("LMSubs: installed LM subtitle tracks on {Item}", video.Name);
        }

        return changed;
    }
}
