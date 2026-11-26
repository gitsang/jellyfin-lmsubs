using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.LmSubs.Services;

// Per-item cache: cues.json is the source of truth (persisted after every chunk);
// .srt files are rendered views served by the REST API and the subtitle provider.
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

    public List<SubtitleCue> LoadSourceCues(Guid itemId) => LoadCues(itemId, "source");

    public void SaveSourceCues(Guid itemId, IEnumerable<SubtitleCue> cues) => SaveCues(itemId, "source", cues);

    public string CacheSrtPath(Guid itemId, bool translated, string lang) =>
        Path.Combine(ItemDir(itemId), !translated ? "source.srt" : $"translated.{LangSlug(lang)}.srt");

    public void Render(Guid itemId, bool translated, string lang, IEnumerable<SubtitleCue> cues)
    {
        File.WriteAllText(CacheSrtPath(itemId, translated, lang), SubtitleText.ToSrt(cues));
    }

    public string? GetSrt(Guid itemId, bool translated, string lang)
    {
        var file = CacheSrtPath(itemId, translated, lang);
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    public string? GetVtt(Guid itemId, bool translated, string lang)
    {
        var srt = GetSrt(itemId, translated, lang);
        return srt == null ? null : SubtitleText.ToVtt(SubtitleText.ParseSrt(srt));
    }
}
