using System.ComponentModel.DataAnnotations;
using Jellyfin.Plugin.LmSubs.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LmSubs.Api;

[ApiController]
[Authorize]
[Route("LmSubs")]
[Produces("application/json")]
public class LmSubsController : ControllerBase
{
    private readonly SubtitleService _subtitles;
    private readonly OpenAiClient _openAi;

    public LmSubsController(SubtitleService subtitles, OpenAiClient openAi)
    {
        _subtitles = subtitles;
        _openAi = openAi;
    }

    public record GenerateRequest(
        [Required] Guid ItemId,
        string Mode = "translated",
        string? TargetLanguage = null,
        bool Restart = false);

    [HttpPost("Generate")]
    public ActionResult Generate([FromBody] GenerateRequest request)
    {
        try
        {
            var mode = request.Mode is "source" or "bilingual" ? request.Mode : "translated";
            var job = _subtitles.EnsureRunning(request.ItemId, mode, request.TargetLanguage, request.Restart);
            return Ok(JobDto(job));
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("Status/{itemId:guid}")]
    public ActionResult Status([FromRoute] Guid itemId)
    {
        var job = _subtitles.GetJob(itemId);
        if (job == null)
        {
            var hasResult = _subtitles.Store.GetSrt(itemId, true, string.Empty) != null;
            return Ok(new { status = hasResult ? "done" : "none" });
        }

        return Ok(JobDto(job));
    }

    [HttpPost("Cancel/{itemId:guid}")]
    public ActionResult Cancel([FromRoute] Guid itemId) => Ok(new { cancelled = _subtitles.Cancel(itemId) });

    [HttpGet("Tasks")]
    public ActionResult Tasks()
    {
        // One ASR row + one Translate row per (item, language). Translated and
        // bilingual are renders of the same translation, never separate rows.
        var tasks = new List<Dictionary<string, object?>>();
        var seen = new HashSet<(Guid, string)>();

        foreach (var job in _subtitles.AllJobs().OrderByDescending(j => j.StartedUtc))
        {
            if (seen.Add((job.ItemId, job.CacheKey)))
            {
                tasks.Add(JobDto(job));
                if (job.Translate)
                {
                    seen.Add((job.ItemId, SubtitleStore.LangSlug(job.TargetLanguage)));
                }
            }
        }

        // Cache-only entries (e.g. after a server restart) still show as completed tasks.
        foreach (var itemId in _subtitles.Store.ListCachedItems())
        {
            foreach (var slug in _subtitles.Store.ListCachedLanguages(itemId))
            {
                // Merge bi-<lang> and legacy <lang> into a single translate row.
                var langSlug = slug.StartsWith("bi-", StringComparison.Ordinal) ? slug[3..] : slug;
                if (seen.Add((itemId, langSlug)))
                {
                    tasks.Add(CachedDto(itemId, "bi-" + langSlug));
                }

                seen.Add((itemId, "bi-" + langSlug));
            }

            if (_subtitles.Store.HasSourceCache(itemId) && seen.Add((itemId, "source")))
            {
                tasks.Add(CachedDto(itemId, "source"));
            }
        }

        return Ok(tasks);
    }

    // Delete cache (and stop the task if running). Downloaded subtitle files are untouched.
    [HttpDelete("Tasks/{itemId:guid}")]
    public ActionResult DeleteTask([FromRoute] Guid itemId, [FromQuery] string? lang = null)
    {
        // lang is a cache key: source | <langslug> | bi-<langslug>.
        var slug = lang == null ? null : (lang == "source" || lang.StartsWith("bi-", StringComparison.Ordinal) ? lang : SubtitleStore.LangSlug(lang));
        if (slug == null)
        {
            _subtitles.Cancel(itemId);
        }
        else
        {
            var job = _subtitles.AllJobs().FirstOrDefault(j => j.ItemId == itemId && j.CacheKey == slug);
            job?.Cts.Cancel();
        }

        _subtitles.Forget(itemId, slug);
        _subtitles.Store.DeleteCache(itemId, slug);

        // Translated/bilingual share the bi- cache: deleting either removes both
        // representations (and any legacy single-mode render).
        if (slug != null && slug != "source")
        {
            var counterpart = slug.StartsWith("bi-", StringComparison.Ordinal) ? slug[3..] : "bi-" + slug;
            _subtitles.Forget(itemId, counterpart);
            _subtitles.Store.DeleteCache(itemId, counterpart);
        }

        return Ok(new { deleted = true });
    }

    private Dictionary<string, object?> CachedDto(Guid itemId, string slug) => new()
    {
        ["itemId"] = itemId,
        ["itemName"] = _subtitles.ItemName(itemId) ?? itemId.ToString("N"),
        ["status"] = "done",
        ["stage"] = "Cached",
        ["progress"] = 1.0,
        ["asrProgress"] = 1.0,
        ["translateProgress"] = 1.0,
        ["error"] = null,
        ["targetLanguage"] = slug == "source" ? "source" : slug.StartsWith("bi-", StringComparison.Ordinal) ? slug[3..] : slug,
        ["mode"] = slug == "source" ? "source" : slug.StartsWith("bi-", StringComparison.Ordinal) ? "bilingual" : "translated",
        ["cacheKey"] = slug,
        ["startedUtc"] = null,
        ["finishedUtc"] = null
    };

    public record TranslateRequest([Required] Guid ItemId, string? TargetLanguage = null);

    [HttpPost("Translate")]
    public async Task<ActionResult> Translate([FromBody] TranslateRequest request, CancellationToken ct)
    {
        try
        {
            var srt = await _subtitles.TranslateCachedAsync(request.ItemId, request.TargetLanguage, ct).ConfigureAwait(false);
            return Ok(new { srt });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("Subtitles/{itemId:guid}")]
    [AllowAnonymous] // subtitle fetches cannot carry auth headers; item ids are unguessable GUIDs
    public ActionResult Subtitles(
        [FromRoute] Guid itemId,
        [FromQuery] string mode = "translated",
        [FromQuery] string? lang = null,
        [FromQuery] string format = "vtt")
    {
        var targetLang = SubtitleService.NormalizeTargetLanguage(lang);
        var m = mode.ToLowerInvariant();
        var srt = m == "source"
            ? _subtitles.Store.GetSrtKeyed(itemId, "source")
            : _subtitles.Store.GetAnyTranslationSrt(itemId, SubtitleStore.LangSlug(targetLang));
        if (srt == null)
        {
            return NotFound();
        }

        // Paired bi- cues serve translated requests as translation-only.
        if (m == "translated" && _subtitles.Store.GetSrtKeyed(itemId, "bi-" + SubtitleStore.LangSlug(targetLang)) != null)
        {
            srt = SubtitleText.ToSrt(SubtitleText.ParseSrt(srt).Select(Providers.LmSubtitleProvider.TranslationOnly));
        }

        return string.Equals(format, "srt", StringComparison.OrdinalIgnoreCase)
            ? Content(srt, "application/x-subrip; charset=utf-8")
            : Content(SubtitleText.ToVtt(SubtitleText.ParseSrt(srt)), "text/vtt; charset=utf-8");
    }

    [HttpPost("Test")]
    public async Task<ActionResult> Test(CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        var asr = await _openAi.TestAsrAsync(config, ct).ConfigureAwait(false);
        var translate = await _openAi.TestTranslateAsync(config, ct).ConfigureAwait(false);
        return Ok(new
        {
            asrOk = asr.Ok,
            asrError = asr.Error,
            translateOk = translate.Ok,
            translateError = translate.Error
        });
    }

    private static Dictionary<string, object?> JobDto(GenerationJob job) => new()
    {
        ["itemId"] = job.ItemId,
        ["itemName"] = job.ItemName,
        ["status"] = job.Status,
        ["stage"] = job.Stage,
        ["progress"] = Math.Round(job.Progress, 3),
        ["asrProgress"] = Math.Round(job.AsrProgress, 3),
        ["translateProgress"] = Math.Round(job.TranslateProgress, 3),
        ["error"] = job.Error,
        ["targetLanguage"] = job.Translate ? job.TargetLanguage : "source",
        ["mode"] = job.Mode,
        ["cacheKey"] = job.CacheKey,
        ["startedUtc"] = job.StartedUtc,
        ["finishedUtc"] = job.FinishedUtc
    };
}
