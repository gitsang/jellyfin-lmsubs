using System.ComponentModel.DataAnnotations;
using Jellyfin.Plugin.LmSubs.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LmSubs.Api;

/// <summary>REST endpoints used by the injected web player script and the config page.</summary>
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

    public record GenerateRequest([Required] Guid ItemId, int? AudioStreamIndex, bool Translate = true, string? TargetLanguage = null);

    /// <summary>Queue full-video ASR generation (optionally followed by translation).</summary>
    [HttpPost("Generate")]
    public ActionResult Generate([FromBody] GenerateRequest request)
    {
        try
        {
            var job = _subtitles.StartGeneration(request.ItemId, request.AudioStreamIndex, request.Translate, request.TargetLanguage);
            return Ok(JobDto(job));
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Job status for polling.</summary>
    [HttpGet("Status/{itemId:guid}")]
    public ActionResult Status([FromRoute] Guid itemId)
    {
        var job = _subtitles.GetJob(itemId);
        var hasSource = _subtitles.GetCachedSrt(itemId, false, string.Empty) != null;
        if (job == null)
        {
            return Ok(new { status = hasSource ? "done" : "none", hasSource });
        }

        var dto = JobDto(job);
        dto["hasSource"] = hasSource;
        return Ok(dto);
    }

    [HttpPost("Cancel/{itemId:guid}")]
    public ActionResult Cancel([FromRoute] Guid itemId) => Ok(new { cancelled = _subtitles.Cancel(itemId) });

    public record TranslateRequest([Required] Guid ItemId, string? TargetLanguage = null);

    /// <summary>Translate the cached source subtitles (no ASR).</summary>
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

    /// <summary>Fetch generated subtitles as WebVTT for the HTML5 player.</summary>
    [HttpGet("Subtitles/{itemId:guid}")]
    [AllowAnonymous] // <track> requests cannot carry auth headers; item ids are unguessable GUIDs
    public ActionResult Subtitles(
        [FromRoute] Guid itemId,
        [FromQuery] bool translated = false,
        [FromQuery] string? lang = null,
        [FromQuery] string format = "vtt")
    {
        var lang2 = lang ?? Plugin.Instance?.Configuration.TargetLanguage ?? "Chinese (Simplified)";
        if (string.Equals(format, "srt", StringComparison.OrdinalIgnoreCase))
        {
            var srt = _subtitles.GetCachedSrt(itemId, translated, lang2);
            return srt == null ? NotFound() : Content(srt, "application/x-subrip; charset=utf-8");
        }

        var vtt = _subtitles.GetCachedVtt(itemId, translated, lang2);
        return vtt == null ? NotFound() : Content(vtt, "text/vtt; charset=utf-8");
    }

    public record RealtimeRequest(
        [Required] Guid ItemId,
        int? AudioStreamIndex,
        double Start,
        double Window = 30,
        bool Translate = true,
        string? TargetLanguage = null);

    /// <summary>
    /// On-demand transcription of a short window around the playback position.
    /// Used by the web script for "realtime" subtitles without a full pass.
    /// </summary>
    [HttpPost("Realtime")]
    public async Task<ActionResult> Realtime([FromBody] RealtimeRequest request, CancellationToken ct)
    {
        try
        {
            var cues = await _subtitles.TranscribeWindowAsync(
                request.ItemId, request.AudioStreamIndex, request.Start, request.Window,
                request.Translate, request.TargetLanguage, ct).ConfigureAwait(false);
            return Ok(new
            {
                start = request.Start,
                window = request.Window,
                cues = cues.Select(c => new { start = c.Start, end = c.End, text = c.Text })
            });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Connectivity test for both configured endpoints (used by the config page).</summary>
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
        ["error"] = job.Error,
        ["translate"] = job.Translate,
        ["targetLanguage"] = job.TargetLanguage,
        ["startedUtc"] = job.StartedUtc,
        ["finishedUtc"] = job.FinishedUtc
    };
}
