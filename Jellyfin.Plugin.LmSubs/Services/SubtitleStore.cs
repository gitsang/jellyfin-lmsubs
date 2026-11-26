using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.LmSubs.Services;

/// <summary>
/// Per-item subtitle cache. cues.json is the source of truth (partial results are
/// persisted after every chunk); .srt / .lm.srt files are rendered views that double
/// as the real external subtitle files Jellyfin serves to clients.
/// </summary>
public class SubtitleStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly string _root;

    public SubtitleStore(IApplicationPaths appPaths)
    {
        _root = Path.Combine(appPaths.DataPath, "plugins", "lmsubs", "cache");
        Directory.CreateDirectory(_root);
    }

    public string ItemDir(Guid itemId)
    {
        var dir = Path.Combine(_root, itemId.ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string LangSlug(string lang)
    {
        var slug = System.Text.RegularExpressions.Regex
            .Replace((lang ?? string.Empty).Trim().ToLowerInvariant(), "[^a-z0-9\\u4e00-\\u9fff]+", "-").Trim('-');
        return slug.Length == 0 ? "default" : slug;
    }

    // ---------------- cues (source of truth) ----------------

    private string CuesPath(Guid itemId, string lang) =>
        Path.Combine(ItemDir(itemId), $"cues.{LangSlug(lang)}.json");

    public List<SubtitleCue> LoadCues(Guid itemId, string lang)
    {
        var path = CuesPath(itemId, lang);
        if (!File.Exists(path))
        {
            return new List<SubtitleCue>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<SubtitleCue>>(File.ReadAllText(path), JsonOpts) ?? new List<SubtitleCue>();
        }
        catch
        {
            return new List<SubtitleCue>();
        }
    }

    public void SaveCues(Guid itemId, string lang, IEnumerable<SubtitleCue> cues)
    {
        var list = cues.OrderBy(c => c.Start).ThenBy(c => c.End).ToList();
        File.WriteAllText(CuesPath(itemId, lang), JsonSerializer.Serialize(list));
    }

    /// <summary>Merge cues (dedupe near-identical start+text), keep sorted, persist.</summary>
    public List<SubtitleCue> MergeCues(Guid itemId, string lang, IEnumerable<SubtitleCue> newCues)
    {
        var all = LoadCues(itemId, lang);
        foreach (var c in newCues)
        {
            if (string.IsNullOrWhiteSpace(c.Text))
            {
                continue;
            }

            var dup = all.Any(e => Math.Abs(e.Start - c.Start) < 0.25 &&
                                   string.Equals(e.Text, c.Text, StringComparison.Ordinal));
            if (!dup)
            {
                all.Add(c);
            }
        }

        all = all.OrderBy(c => c.Start).ThenBy(c => c.End).ToList();
        SaveCues(itemId, lang, all);
        return all;
    }

    public List<SubtitleCue> LoadSourceCues(Guid itemId) => LoadCues(itemId, "source");

    public void SaveSourceCues(Guid itemId, IEnumerable<SubtitleCue> cues) => SaveCues(itemId, "source", cues);

    // ---------------- rendered SRT files ----------------

    /// <summary>Rendered file next to the media (picked up by library scans).</summary>
    public static string SidecarPath(string mediaPath, bool live, string lang)
    {
        var dir = Path.GetDirectoryName(mediaPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(mediaPath);
        return Path.Combine(dir, $"{name}.{LangSlug(lang)}.lmsubs{(live ? "-live" : string.Empty)}.srt");
    }

    /// <summary>Rendered file in the plugin cache (REST API / fallback delivery).</summary>
    public string CacheSrtPath(Guid itemId, bool translated, bool live, string lang) =>
        Path.Combine(ItemDir(itemId),
            !translated ? "source.srt" : live ? $"live.{LangSlug(lang)}.srt" : $"translated.{LangSlug(lang)}.srt");

    /// <summary>Write SRT views (cache always; sidecar when the media dir is writable).</summary>
    public void Render(Guid itemId, string? mediaPath, bool translated, bool live, string lang, IEnumerable<SubtitleCue> cues)
    {
        var list = cues as List<SubtitleCue> ?? cues.ToList();
        var srt = SubtitleText.ToSrt(list);
        File.WriteAllText(CacheSrtPath(itemId, translated, live, lang), srt);
        if (!string.IsNullOrEmpty(mediaPath))
        {
            TryWriteSidecar(SidecarPath(mediaPath, live, lang), srt);
        }
    }

    public void EnsureSidecars(Guid itemId, string mediaPath, string lang)
    {
        foreach (var live in new[] { false, true })
        {
            var path = SidecarPath(mediaPath, live, lang);
            if (!File.Exists(path))
            {
                var cache = CacheSrtPath(itemId, true, live, lang);
                var srt = File.Exists(cache) ? File.ReadAllText(cache) : string.Empty;
                TryWriteSidecar(path, srt);
            }
        }
    }

    private static void TryWriteSidecar(string path, string content)
    {
        try
        {
            File.WriteAllText(path, content);
        }
        catch
        {
            // read-only media dir — cache copy still works via the REST API
        }
    }

    public string? GetSrt(Guid itemId, bool translated, string lang, bool live = false)
    {
        var file = CacheSrtPath(itemId, translated, live, lang);
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    public string? GetVtt(Guid itemId, bool translated, string lang, bool live = false)
    {
        var srt = GetSrt(itemId, translated, lang, live);
        return srt == null ? null : SubtitleText.ToVtt(SubtitleText.ParseSrt(srt));
    }
}
