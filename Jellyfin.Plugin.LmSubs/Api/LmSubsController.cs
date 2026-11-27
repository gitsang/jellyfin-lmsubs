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
        // Tasks are pipeline units, not subtitle entries: one ASR per item and
        // one Translate per (item, language). Subtitle downloads merely trigger them.
        var tasks = new List<Dictionary<string, object?>>();
        var seenAsr = new HashSet<Guid>();
        var seenTranslate = new HashSet<(Guid, string)>();

        foreach (var job in _subtitles.AllJobs().OrderByDescending(j => j.StartedUtc))
        {
            if (job.Translate)
            {
                if (seenTranslate.Add((job.ItemId, SubtitleStore.LangSlug(job.TargetLanguage))))
                {
                    tasks.Add(JobDto(job));
                }
            }
            else if (seenAsr.Add(job.ItemId))
            {
                tasks.Add(JobDto(job));
            }
        }

        // Cache-only entries (e.g. after a server restart) still show as completed tasks.
        foreach (var itemId in _subtitles.Store.ListCachedItems())
        {
            foreach (var slug in _subtitles.Store.ListCachedLanguages(itemId))
            {
                var langSlug = slug.StartsWith("bi-", StringComparison.Ordinal) ? slug[3..] : slug;
                if (seenTranslate.Add((itemId, langSlug)))
                {
                    tasks.Add(CachedDto(itemId, langSlug));
                }
            }

            if (_subtitles.Store.HasSourceCache(itemId) && seenAsr.Add(itemId))
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
        // lang: "source" (ASR task) or a language name/slug (Translate task); omitted = whole item.
        if (lang == null)
        {
            _subtitles.Cancel(itemId);
            _subtitles.Forget(itemId);
            _subtitles.Store.DeleteCache(itemId);
            return Ok(new { deleted = true });
        }

        if (lang == "source")
        {
            _subtitles.Cancel(itemId);
            _subtitles.Forget(itemId, "source");
            _subtitles.Store.DeleteCache(itemId, "source");
            return Ok(new { deleted = true });
        }

        var langSlug = SubtitleStore.LangSlug(lang.StartsWith("bi-", StringComparison.Ordinal) ? lang[3..] : lang);
        var translateJob = _subtitles.AllJobs().FirstOrDefault(j =>
            j.ItemId == itemId && j.Translate && SubtitleStore.LangSlug(j.TargetLanguage) == langSlug);
        translateJob?.Cts.Cancel();

        // One translate task owns both cache representations of the language.
        foreach (var key in new[] { langSlug, "bi-" + langSlug })
        {
            _subtitles.Forget(itemId, key);
            _subtitles.Store.DeleteCache(itemId, key);
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
        ["kind"] = slug == "source" ? "asr" : "translate",
        ["targetLanguage"] = slug,
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

    private Dictionary<string, object?> JobDto(GenerationJob job)
    {
        // ASR progress always reads the live session, not the job's last-tick copy.
        var session = _subtitles.GetSession(job.ItemId);
        var asrProgress = session?.Progress ?? job.AsrProgress;
        return JobDtoCore(job, asrProgress);
    }

    private static Dictionary<string, object?> JobDtoCore(GenerationJob job, double asrProgress) => new()
    {
        ["itemId"] = job.ItemId,
        ["itemName"] = job.ItemName,
        ["status"] = job.Status,
        ["stage"] = job.Stage,
        ["progress"] = Math.Round(job.Progress, 3),
        ["asrProgress"] = Math.Round(asrProgress, 3),
        ["translateProgress"] = Math.Round(job.TranslateProgress, 3),
        ["error"] = job.Error,
        ["kind"] = job.Translate ? "translate" : "asr",
        ["targetLanguage"] = job.Translate ? job.TargetLanguage : "source",
        ["startedUtc"] = job.StartedUtc,
        ["finishedUtc"] = job.FinishedUtc
    };
}
