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

    // Keyed cache: source | <langslug> | bi-<langslug>.
    public string KeyedSrtPath(Guid itemId, string cacheKey) =>
        Path.Combine(ItemDir(itemId), cacheKey == "source" ? "source.srt" : $"translated.{cacheKey}.srt");

    public void RenderKeyed(Guid itemId, string cacheKey, IEnumerable<SubtitleCue> cues) =>
        File.WriteAllText(KeyedSrtPath(itemId, cacheKey), SubtitleText.ToSrt(cues));

    public string? GetSrtKeyed(Guid itemId, string cacheKey)
    {
        var file = KeyedSrtPath(itemId, cacheKey);
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    // Any complete translation result: paired bi- cache (current) or legacy
    // translated.<lang>.srt (pre-1.4.1 single-mode renders).
    public string? GetAnyTranslationSrt(Guid itemId, string langSlug)
    {
        return GetSrtKeyed(itemId, "bi-" + langSlug) ?? GetSrtKeyed(itemId, langSlug);
    }

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

    public List<Guid> ListCachedItems()
    {
        var result = new List<Guid>();
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            if (Guid.TryParse(Path.GetFileName(dir), out var id))
            {
                result.Add(id);
            }
        }

        return result;
    }

    // Languages with a rendered translated SRT on disk (slug form).
    public List<string> ListCachedLanguages(Guid itemId)
    {
        var dir = Path.Combine(_root, itemId.ToString("N"));
        if (!Directory.Exists(dir))
        {
            return new List<string>();
        }

        return Directory.EnumerateFiles(dir, "translated.*.srt")
            .Select(f => Path.GetFileName(f)["translated.".Length..^".srt".Length])
            .ToList();
    }

    public bool HasSourceCache(Guid itemId)
    {
        var dir = Path.Combine(_root, itemId.ToString("N"));
        return File.Exists(Path.Combine(dir, "source.srt")) || File.Exists(Path.Combine(dir, "cues.source.json"));
    }

    // Transcript exists even when the session died mid-way: cues.source.json is
    // persisted after every chunk, source.srt only at the end.
    public bool HasSourceResult(Guid itemId) => HasSourceCache(itemId) && LoadSourceCues(itemId).Count > 0;

    // Render source.srt from the cue cache on demand (partial transcripts included).
    public string? EnsureSourceSrt(Guid itemId)
    {
        var existing = GetSrtKeyed(itemId, "source");
        if (existing != null)
        {
            return existing;
        }

        var cues = LoadSourceCues(itemId);
        if (cues.Count == 0)
        {
            return null;
        }

        RenderKeyed(itemId, "source", cues);
        return GetSrtKeyed(itemId, "source");
    }

    // Delete the whole item cache, or one language's cues+srt (source included).
    public void DeleteCache(Guid itemId, string? langSlug = null)
    {
        var dir = Path.Combine(_root, itemId.ToString("N"));
        if (!Directory.Exists(dir))
        {
            return;
        }

        if (langSlug == null)
        {
            Directory.Delete(dir, true);
            return;
        }

        var files = langSlug == "source"
            ? new[] { "cues.source.json", "source.srt" }
            : new[] { $"cues.{langSlug}.json", $"translated.{langSlug}.srt" };
        foreach (var f in files)
        {
            var p = Path.Combine(dir, f);
            if (File.Exists(p))
            {
                File.Delete(p);
            }
        }

        if (!Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
        }
    }
}
