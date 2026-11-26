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
        bool Translate = true,
        string? TargetLanguage = null,
        bool Restart = false);

    [HttpPost("Generate")]
    public ActionResult Generate([FromBody] GenerateRequest request)
    {
        try
        {
            var job = _subtitles.EnsureRunning(request.ItemId, request.Translate, request.TargetLanguage, request.Restart);
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
        var translated = !string.Equals(mode, "source", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(format, "srt", StringComparison.OrdinalIgnoreCase))
        {
            var srt = _subtitles.Store.GetSrt(itemId, translated, targetLang);
            return srt == null ? NotFound() : Content(srt, "application/x-subrip; charset=utf-8");
        }

        var vtt = _subtitles.Store.GetVtt(itemId, translated, targetLang);
        return vtt == null ? NotFound() : Content(vtt, "text/vtt; charset=utf-8");
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
        ["targetLanguage"] = job.TargetLanguage,
        ["startedUtc"] = job.StartedUtc,
        ["finishedUtc"] = job.FinishedUtc
    };
}
