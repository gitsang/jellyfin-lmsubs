using System.Collections.Concurrent;
using Jellyfin.Plugin.LmSubs.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LmSubs.Services;

public class GenerationJob
{
    public Guid ItemId { get; init; }

    public string ItemName { get; set; } = string.Empty;

    public string Status { get; set; } = "queued";

    public string Stage { get; set; } = string.Empty;

    public double Progress { get; set; }

    public string? Error { get; set; }

    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public DateTime? FinishedUtc { get; set; }

    public string TargetLanguage { get; set; } = string.Empty;

    public bool Translate { get; set; }

    public CancellationTokenSource Cts { get; } = new();

    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

// Full-video ASR + translation pipeline. Runs sequentially from 0 to the end,
// persists cues after every chunk, and renders the final SRT only when complete —
// Jellyfin's SubtitleManager then saves/registers it like any downloaded subtitle.
public class SubtitleService
{
    private readonly ILibraryManager _libraryManager;
    private readonly SubtitleStore _store;
    private readonly OpenAiClient _openAi;
    private readonly AudioExtractor _extractor;
    private readonly ILogger<SubtitleService> _logger;

    private readonly ConcurrentDictionary<Guid, GenerationJob> _jobs = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _itemLocks = new();

    public SubtitleService(
        ILibraryManager libraryManager,
        SubtitleStore store,
        OpenAiClient openAi,
        AudioExtractor extractor,
        ILogger<SubtitleService> logger)
    {
        _libraryManager = libraryManager;
        _store = store;
        _openAi = openAi;
        _extractor = extractor;
        _logger = logger;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public SubtitleStore Store => _store;

    private Video GetVideo(Guid itemId) =>
        _libraryManager.GetItemById(itemId) as Video
        ?? throw new KeyNotFoundException($"Video item not found: {itemId}");

    public (string Path, double Duration) ResolveMedia(Guid itemId)
    {
        var video = GetVideo(itemId);
        var sources = video.GetMediaSources(true);
        var source = sources.FirstOrDefault() ?? throw new InvalidOperationException("Item has no usable MediaSource");
        var path = source.Path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            throw new FileNotFoundException($"Media file missing or not accessible to Jellyfin: {path}");
        }

        double duration = source.RunTimeTicks.HasValue ? TimeSpan.FromTicks(source.RunTimeTicks.Value).TotalSeconds : 0;
        return (path, duration);
    }

    public Guid FindItemIdByMediaPath(string? mediaPath)
    {
        if (string.IsNullOrEmpty(mediaPath))
        {
            return Guid.Empty;
        }

        var query = new InternalItemsQuery { Path = mediaPath, MediaTypes = new[] { Jellyfin.Data.Enums.MediaType.Video }, Recursive = true };
        var item = _libraryManager.GetItemList(query).FirstOrDefault();
        return item?.Id ?? Guid.Empty;
    }

    public static string ThreeLetterLanguage(string language)
    {
        var l = (language ?? string.Empty).ToLowerInvariant();
        if (l.Contains("chinese") || l.Contains("中文") || l.Contains("汉语") || l.Contains("漢"))
        {
            return "chi";
        }

        if (l.Contains("english") || l.Contains("英"))
        {
            return "eng";
        }

        if (l.Contains("japan") || l.Contains("日"))
        {
            return "jpn";
        }

        if (l.Contains("korean") || l.Contains("韩") || l.Contains("韓"))
        {
            return "kor";
        }

        if (l.Contains("french") || l.Contains("法"))
        {
            return "fre";
        }

        if (l.Contains("german") || l.Contains("德"))
        {
            return "ger";
        }

        if (l.Contains("spanish") || l.Contains("西班牙"))
        {
            return "spa";
        }

        if (l.Contains("russian") || l.Contains("俄"))
        {
            return "rus";
        }

        return "und";
    }

    public GenerationJob? GetJob(Guid itemId) => _jobs.TryGetValue(itemId, out var j) ? j : null;

    public bool Cancel(Guid itemId)
    {
        if (_jobs.TryGetValue(itemId, out var job) && job.Status is "queued" or "running")
        {
            job.Cts.Cancel();
            return true;
        }

        return false;
    }

    public GenerationJob EnsureRunning(Guid itemId, bool translate = true, string? targetLanguage = null, bool restart = false)
    {
        var config = Config;
        var lang = string.IsNullOrWhiteSpace(targetLanguage) ? config.TargetLanguage : targetLanguage!;

        // Serialize job control per item; requests may arrive concurrently.
        var controlLock = _itemLocks.GetOrAdd(itemId, _ => new SemaphoreSlim(1, 1));
        controlLock.Wait();
        try
        {
            if (_jobs.TryGetValue(itemId, out var existing))
            {
                if (existing.Status is "queued" or "running")
                {
                    if (!restart)
                    {
                        return existing;
                    }

                    existing.Cts.Cancel();
                }
                else if (!restart && existing.Status == "done")
                {
                    return existing;
                }
            }

            var (path, duration) = ResolveMedia(itemId);
            var job = new GenerationJob
            {
                ItemId = itemId,
                TargetLanguage = lang,
                Translate = translate,
                ItemName = GetVideo(itemId).Name ?? itemId.ToString()
            };

            _jobs[itemId] = job;
            _ = Task.Run(() => RunPipelineAsync(job, path, duration));
            return job;
        }
        finally
        {
            controlLock.Release();
        }
    }

    private async Task RunPipelineAsync(GenerationJob job, string mediaPath, double duration)
    {
        var itemLock = _itemLocks.GetOrAdd(job.ItemId, _ => new SemaphoreSlim(1, 1));
        try
        {
            await itemLock.WaitAsync(job.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Finish(job, "cancelled");
            return;
        }

        // A newer job may have superseded this one while we waited for the lock.
        if (job.Cts.IsCancellationRequested || !ReferenceEquals(GetJob(job.ItemId), job))
        {
            itemLock.Release();
            Finish(job, "cancelled");
            return;
        }

        var config = Config;
        var tempDir = Path.Combine(Path.GetTempPath(), "lmsubs-" + job.ItemId.ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var ct = job.Cts.Token;
            job.Status = "running";

            if (duration <= 0)
            {
                duration = await _extractor.GetDurationSecondsAsync(mediaPath, config, ct).ConfigureAwait(false);
            }

            if (duration <= 0)
            {
                throw new InvalidOperationException("Cannot determine media duration (ffprobe unavailable? set the ffmpeg path in plugin settings)");
            }

            var chunk = Math.Clamp(config.ChunkSeconds, 30, 3600);
            var chunkCount = (int)Math.Ceiling(duration / chunk);
            var sourceCues = new List<SubtitleCue>();
            var finalCues = new List<SubtitleCue>();
            var context = new List<(string Src, string Dst)>();
            var ctxMax = Math.Clamp(config.ContextPairs, 0, 30);

            for (var i = 0; i < chunkCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var cs = (double)i * chunk;
                var len = Math.Min(chunk, duration - cs);
                job.Stage = $"Transcribing {fmt(cs)}–{fmt(cs + len)} ({i + 1}/{chunkCount})";

                var file = Path.Combine(tempDir, $"chunk_{i:0000}.mp3");
                await _extractor.ExtractAsync(mediaPath, null, cs, len, file, config, ct).ConfigureAwait(false);
                List<AsrSegment> segments;
                try
                {
                    segments = await _openAi.TranscribeAsync(file, cs, len, config, ct).ConfigureAwait(false);
                }
                finally
                {
                    try { File.Delete(file); } catch { /* best effort */ }
                }

                var cues = SubtitleText.Normalize(segments);
                if (cues.Count > 0)
                {
                    sourceCues.AddRange(cues);
                    var chunkFinal = job.Translate
                        ? await TranslateCuesAsync(cues, job.TargetLanguage, context, ctxMax, ct).ConfigureAwait(false)
                        : cues;
                    finalCues.AddRange(chunkFinal);

                    // Persist progress so a crashed/interrupted run can be inspected or resumed via REST.
                    _store.SaveSourceCues(job.ItemId, sourceCues);
                    _store.SaveCues(job.ItemId, job.TargetLanguage, finalCues);
                }

                job.Progress = (double)(i + 1) / chunkCount;
            }

            _store.Render(job.ItemId, translated: true, job.TargetLanguage, finalCues);
            _logger.LogInformation("LMSubs: generated {Count} cues for {Item}", finalCues.Count, job.ItemName);
            Finish(job, "done");
        }
        catch (OperationCanceledException)
        {
            Finish(job, "cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LMSubs: pipeline failed for {Item}", job.ItemName);
            job.Error = ex.Message;
            Finish(job, "error");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
            itemLock.Release();
        }

        static string fmt(double s) => TimeSpan.FromSeconds(s).ToString(@"hh\:mm\:ss");
    }

    private static void Finish(GenerationJob job, string status)
    {
        job.Status = status;
        job.FinishedUtc = DateTime.UtcNow;
        if (status == "done")
        {
            job.Progress = 1;
            job.Stage = "Done";
        }

        job.Completion.TrySetResult();
    }

    private async Task<List<SubtitleCue>> TranslateCuesAsync(
        List<SubtitleCue> cues, string targetLanguage,
        List<(string Src, string Dst)> context, int ctxMax, CancellationToken ct)
    {
        var config = Config;
        var batchSize = Math.Clamp(config.TranslateBatchSize, 1, 100);
        var output = new List<SubtitleCue>(cues.Count);

        for (var offset = 0; offset < cues.Count; offset += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = cues.Skip(offset).Take(batchSize).ToList();
            var texts = batch.Select(c => c.Text).ToList();
            var translations = await _openAi.TranslateBatchAsync(texts, context, targetLanguage, config, ct).ConfigureAwait(false);

            for (var i = 0; i < batch.Count; i++)
            {
                var src = batch[i].Text;
                var dst = i < translations.Count ? translations[i] : src;
                var text = string.Equals(config.BilingualMode, "replace", StringComparison.OrdinalIgnoreCase)
                    ? dst
                    : src + "\n" + dst;
                output.Add(batch[i] with { Text = text });

                if (ctxMax > 0 && !string.IsNullOrWhiteSpace(dst) && dst != src)
                {
                    context.Add((src, dst));
                    if (context.Count > ctxMax)
                    {
                        context.RemoveAt(0);
                    }
                }
            }
        }

        return output;
    }

    public async Task<string> TranslateCachedAsync(Guid itemId, string? targetLanguage, CancellationToken ct)
    {
        var config = Config;
        var lang = string.IsNullOrWhiteSpace(targetLanguage) ? config.TargetLanguage : targetLanguage!;
        var cues = _store.LoadSourceCues(itemId);
        if (cues.Count == 0)
        {
            throw new FileNotFoundException("No source subtitles cached for this item");
        }

        var context = new List<(string Src, string Dst)>();
        var translated = await TranslateCuesAsync(cues, lang, context, Math.Clamp(config.ContextPairs, 0, 30), ct).ConfigureAwait(false);
        _store.SaveCues(itemId, lang, translated);
        _store.Render(itemId, translated: true, lang, translated);
        return SubtitleText.ToSrt(translated);
    }
}
