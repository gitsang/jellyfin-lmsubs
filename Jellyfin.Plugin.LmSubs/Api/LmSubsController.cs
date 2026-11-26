using System.ComponentModel.DataAnnotations;
using Jellyfin.Plugin.LmSubs.Providers;
using Jellyfin.Plugin.LmSubs.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LmSubs.Api;

/// <summary>
/// REST helpers. Normal playback needs none of these — selecting the LM subtitle
/// track in any client drives everything via playback events. These endpoints are
/// for scripts, debugging and external players.
/// </summary>
[ApiController]
[Authorize]
[Route("LmSubs")]
[Produces("application/json")]
public class LmSubsController : ControllerBase
{
    private readonly SubtitleService _subtitles;
    private readonly OpenAiClient _openAi;
    private readonly IItemRepository _itemRepo;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LmSubsController> _logger;

    public LmSubsController(
        SubtitleService subtitles,
        OpenAiClient openAi,
        IItemRepository itemRepo,
        ILibraryManager libraryManager,
        ILogger<LmSubsController> logger)
    {
        _subtitles = subtitles;
        _openAi = openAi;
        _itemRepo = itemRepo;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public record GenerateRequest(
        [Required] Guid ItemId,
        double? Position = null,
        bool Live = false,
        bool Translate = true,
        string? TargetLanguage = null);

    /// <summary>Start / resume the pipeline (Live=true re-transcribes from Position and overwrites).</summary>
    [HttpPost("Generate")]
    public ActionResult Generate([FromBody] GenerateRequest request)
    {
        try
        {
            var job = _subtitles.EnsureRunning(request.ItemId, request.Position, request.Live, request.TargetLanguage, request.Translate);
            return Ok(job == null ? new { status = "none" } : JobDto(job));
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
        var hasSource = _subtitles.Store.GetSrt(itemId, false, string.Empty) != null;
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

    /// <summary>Install (or repair) the two LM subtitle tracks on an item.</summary>
    [HttpPost("Install/{itemId:guid}")]
    public ActionResult Install([FromRoute] Guid itemId)
    {
        if (_libraryManager.GetItemById(itemId) is not Video video)
        {
            return NotFound(new { error = "不是视频项" });
        }

        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        var changed = LmTrackInstaller.EnsureTracks(video, _itemRepo, _subtitles.Store, config.TargetLanguage, _logger);
        return Ok(new { changed });
    }

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

    /// <summary>Fetch generated subtitles as WebVTT / SRT (external players, debugging).</summary>
    [HttpGet("Subtitles/{itemId:guid}")]
    [AllowAnonymous] // <track> requests cannot carry auth headers; item ids are unguessable GUIDs
    public ActionResult Subtitles(
        [FromRoute] Guid itemId,
        [FromQuery] string mode = "cached",
        [FromQuery] string? lang = null,
        [FromQuery] string format = "vtt")
    {
        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        var targetLang = lang ?? config.TargetLanguage;
        var live = string.Equals(mode, "live", StringComparison.OrdinalIgnoreCase);
        var translated = !string.Equals(mode, "source", StringComparison.OrdinalIgnoreCase);

        // Serving subtitles also nudges the pipeline (position unknown here).
        if (translated)
        {
            try { _subtitles.EnsureRunning(itemId, null, force: false); } catch { /* item may be gone */ }
        }

        if (string.Equals(format, "srt", StringComparison.OrdinalIgnoreCase))
        {
            var srt = _subtitles.Store.GetSrt(itemId, translated, targetLang, live);
            return srt == null ? NotFound() : Content(srt, "application/x-subrip; charset=utf-8");
        }

        var vtt = _subtitles.Store.GetVtt(itemId, translated, targetLang, live);
        return vtt == null ? NotFound() : Content(vtt, "text/vtt; charset=utf-8");
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
        ["targetLanguage"] = job.TargetLanguage,
        ["startedUtc"] = job.StartedUtc,
        ["finishedUtc"] = job.FinishedUtc
    };
}
