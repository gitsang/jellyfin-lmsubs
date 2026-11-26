using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LmSubs.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LmSubs.Services;

/// <summary>State of one full-video generation job.</summary>
public class GenerationJob
{
    public Guid ItemId { get; init; }

    public string ItemName { get; set; } = string.Empty;

    /// <summary>queued | extracting | transcribing | translating | done | error | cancelled.</summary>
    public string Status { get; set; } = "queued";

    public string Stage { get; set; } = string.Empty;

    public double Progress { get; set; }

    public string? Error { get; set; }

    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public DateTime? FinishedUtc { get; set; }

    public bool Translate { get; set; }

    public string TargetLanguage { get; set; } = string.Empty;

    public CancellationTokenSource Cts { get; } = new();
}

/// <summary>
/// Core pipeline: full-video ASR generation, LLM translation, subtitle caching,
/// and on-demand realtime window transcription for the web player script.
/// </summary>
public class SubtitleService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _appPaths;
    private readonly OpenAiClient _openAi;
    private readonly AudioExtractor _extractor;
    private readonly ILogger<SubtitleService> _logger;

    private readonly ConcurrentDictionary<Guid, GenerationJob> _jobs = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _windowLocks = new();

    public SubtitleService(
        ILibraryManager libraryManager,
        IApplicationPaths appPaths,
        OpenAiClient openAi,
        AudioExtractor extractor,
        ILogger<SubtitleService> logger)
    {
        _libraryManager = libraryManager;
        _appPaths = appPaths;
        _openAi = openAi;
        _extractor = extractor;
        _logger = logger;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private string CacheRoot
    {
        get
        {
            var dir = Path.Combine(_appPaths.DataPath, "plugins", "lmsubs", "cache");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private string ItemDir(Guid itemId)
    {
        var dir = Path.Combine(CacheRoot, itemId.ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string LangSlug(string lang)
    {
        var slug = Regex.Replace(lang.Trim().ToLowerInvariant(), "[^a-z0-9\\u4e00-\\u9fff]+", "-").Trim('-');
        return slug.Length == 0 ? "default" : slug;
    }

    // ------------------------------------------------------------------ lookup helpers

    public (string Path, int? AudioStreamIndex, double Duration) ResolveMedia(Guid itemId, int? audioStreamIndex)
    {
        var item = _libraryManager.GetItemById(itemId)
                   ?? throw new KeyNotFoundException($"找不到媒体项 {itemId}");
        var sources = item.GetMediaSources(true);
        var source = sources.FirstOrDefault() ?? throw new InvalidOperationException("媒体项没有可用的 MediaSource");
        var path = source.Path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            throw new FileNotFoundException($"媒体文件不存在或 Jellyfin 无法访问: {path}");
        }

        double duration = source.RunTimeTicks.HasValue ? TimeSpan.FromTicks(source.RunTimeTicks.Value).TotalSeconds : 0;
        return (path, audioStreamIndex, duration);
    }

    public string? GetCachedSrt(Guid itemId, bool translated, string targetLanguage)
    {
        var file = Path.Combine(ItemDir(itemId),
            translated ? $"translated.{LangSlug(targetLanguage)}.srt" : "source.srt");
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    public string? GetCachedVtt(Guid itemId, bool translated, string targetLanguage)
    {
        var srt = GetCachedSrt(itemId, translated, targetLanguage);
        return srt == null ? null : SubtitleText.ToVtt(SubtitleText.ParseSrt(srt));
    }

    public GenerationJob? GetJob(Guid itemId) => _jobs.TryGetValue(itemId, out var j) ? j : null;

    // ------------------------------------------------------------------ full generation

    public GenerationJob StartGeneration(Guid itemId, int? audioStreamIndex, bool translate, string? targetLanguage)
    {
        if (_jobs.TryGetValue(itemId, out var existing) &&
            existing.Status is "queued" or "extracting" or "transcribing" or "translating")
        {
            return existing;
        }

        var config = Config;
        var job = new GenerationJob
        {
            ItemId = itemId,
            Translate = translate,
            TargetLanguage = string.IsNullOrWhiteSpace(targetLanguage) ? config.TargetLanguage : targetLanguage!
        };

        var (path, streamIdx, duration) = ResolveMedia(itemId, audioStreamIndex);
        job.ItemName = _libraryManager.GetItemById(itemId)?.Name ?? itemId.ToString();
        _jobs[itemId] = job;

        _ = Task.Run(() => RunGenerationAsync(job, path, streamIdx, duration));
        return job;
    }

    public bool Cancel(Guid itemId)
    {
        if (_jobs.TryGetValue(itemId, out var job) &&
            job.Status is "queued" or "extracting" or "transcribing" or "translating")
        {
            job.Cts.Cancel();
            job.Status = "cancelled";
            job.FinishedUtc = DateTime.UtcNow;
            return true;
        }

        return false;
    }

    private async Task RunGenerationAsync(GenerationJob job, string mediaPath, int? audioStreamIndex, double duration)
    {
        await _runGate.WaitAsync(job.Cts.Token).ConfigureAwait(false);
        var config = Config;
        var tempDir = Path.Combine(Path.GetTempPath(), "lmsubs-" + job.ItemId.ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var ct = job.Cts.Token;
            if (duration <= 0)
            {
                duration = await _extractor.GetDurationSecondsAsync(mediaPath, config, ct).ConfigureAwait(false);
            }

            if (duration <= 0)
            {
                throw new InvalidOperationException("无法确定媒体时长（ffprobe 不可用？可在配置里指定 ffmpeg 路径）");
            }

            var chunk = Math.Clamp(config.ChunkSeconds, 30, 3600);
            var chunkCount = (int)Math.Ceiling(duration / chunk);
            job.Status = "extracting";
            job.Stage = $"切分音频（{chunkCount} 段 × {chunk}s）";

            // Extract all chunks first (fast, local), then transcribe with bounded parallelism.
            var chunkFiles = new string[chunkCount];
            for (var i = 0; i < chunkCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var start = (double)i * chunk;
                var len = Math.Min(chunk, duration - start);
                var file = Path.Combine(tempDir, $"chunk_{i:0000}.mp3");
                await _extractor.ExtractAsync(mediaPath, audioStreamIndex, start, len, file, config, ct).ConfigureAwait(false);
                chunkFiles[i] = file;
                job.Progress = 0.1 * (i + 1) / chunkCount;
            }

            job.Status = "transcribing";
            job.Stage = $"语音转写 0/{chunkCount}";
            var results = new List<AsrSegment>[chunkCount];
            var done = 0;
            var parallelism = Math.Clamp(config.MaxParallelChunks, 1, 8);
            await Parallel.ForEachAsync(Enumerable.Range(0, chunkCount),
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
                async (i, token) =>
                {
                    var start = (double)i * chunk;
                    var len = Math.Min(chunk, duration - start);
                    results[i] = await _openAi.TranscribeAsync(chunkFiles[i], start, len, config, token).ConfigureAwait(false);
                    var n = Interlocked.Increment(ref done);
                    job.Progress = 0.1 + 0.7 * n / chunkCount;
                    job.Stage = $"语音转写 {n}/{chunkCount}";
                }).ConfigureAwait(false);

            var segments = results.Where(r => r != null).SelectMany(r => r).ToList();
            var cues = SubtitleText.Normalize(segments);
            if (cues.Count == 0)
            {
                throw new InvalidOperationException("ASR 没有返回任何文本（检查模型/音频流）");
            }

            var sourceSrt = SubtitleText.ToSrt(cues);
            await File.WriteAllTextAsync(Path.Combine(ItemDir(job.ItemId), "source.srt"), sourceSrt, ct).ConfigureAwait(false);
            _logger.LogInformation("LMSubs: generated {Count} cues for {Item}", cues.Count, job.ItemName);

            if (job.Translate)
            {
                job.Status = "translating";
                job.Stage = "翻译中";
                var translated = await TranslateCuesAsync(cues, job.TargetLanguage,
                    p =>
                    {
                        job.Progress = 0.8 + 0.2 * p;
                        job.Stage = $"翻译中 {p:P0}";
                    }, ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(
                    Path.Combine(ItemDir(job.ItemId), $"translated.{LangSlug(job.TargetLanguage)}.srt"),
                    SubtitleText.ToSrt(translated), ct).ConfigureAwait(false);
            }

            job.Progress = 1;
            job.Status = "done";
            job.Stage = "完成";
            job.FinishedUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException)
        {
            job.Status = "cancelled";
            job.FinishedUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LMSubs: generation failed for {Item}", job.ItemName);
            job.Status = "error";
            job.Error = ex.Message;
            job.FinishedUtc = DateTime.UtcNow;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
            _runGate.Release();
        }
    }

    // ------------------------------------------------------------------ translation

    public async Task<List<SubtitleCue>> TranslateCuesAsync(
        List<SubtitleCue> cues, string targetLanguage, Action<double>? progress, CancellationToken ct)
    {
        var config = Config;
        var batchSize = Math.Clamp(config.TranslateBatchSize, 1, 100);
        var context = new List<(string Src, string Dst)>();
        var ctxMax = Math.Clamp(config.ContextPairs, 0, 30);
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

            progress?.Invoke((double)Math.Min(offset + batchSize, cues.Count) / cues.Count);
        }

        return output;
    }

    /// <summary>Translate an existing cached source subtitle file (no ASR).</summary>
    public async Task<string> TranslateCachedAsync(Guid itemId, string? targetLanguage, CancellationToken ct)
    {
        var config = Config;
        var lang = string.IsNullOrWhiteSpace(targetLanguage) ? config.TargetLanguage : targetLanguage!;
        var srt = GetCachedSrt(itemId, false, lang)
                  ?? throw new FileNotFoundException("还没有原文字幕，请先执行全量转写");
        var cues = SubtitleText.ParseSrt(srt);
        var translated = await TranslateCuesAsync(cues, lang, null, ct).ConfigureAwait(false);
        var result = SubtitleText.ToSrt(translated);
        await File.WriteAllTextAsync(Path.Combine(ItemDir(itemId), $"translated.{LangSlug(lang)}.srt"), result, ct).ConfigureAwait(false);
        return result;
    }

    // ------------------------------------------------------------------ realtime window

    /// <summary>
    /// Transcribe (and optionally translate) a short window around the playback position.
    /// Results are cached per window so seeking back and forth is cheap.
    /// </summary>
    public async Task<List<SubtitleCue>> TranscribeWindowAsync(
        Guid itemId, int? audioStreamIndex, double startSeconds, double windowSeconds,
        bool translate, string? targetLanguage, CancellationToken ct)
    {
        var config = Config;
        var lang = string.IsNullOrWhiteSpace(targetLanguage) ? config.TargetLanguage : targetLanguage!;
        windowSeconds = Math.Clamp(windowSeconds, 5, 120);
        startSeconds = Math.Max(0, startSeconds);

        var key = string.Create(CultureInfo.InvariantCulture,
            $"{itemId:N}:{audioStreamIndex?.ToString() ?? "a0"}:{startSeconds:0.##}:{windowSeconds:0.##}:{translate}:{LangSlug(lang)}");
        var cacheFile = Path.Combine(ItemDir(itemId), "windows", key.Replace(':', '_') + ".json");
        if (File.Exists(cacheFile))
        {
            try
            {
                var cached = System.Text.Json.JsonSerializer.Deserialize<List<SubtitleCue>>(
                    await File.ReadAllTextAsync(cacheFile, ct).ConfigureAwait(false));
                if (cached != null)
                {
                    return cached;
                }
            }
            catch { /* regenerate */ }
        }

        var gate = _windowLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another request may have filled the cache while we waited.
            if (File.Exists(cacheFile))
            {
                var cached = System.Text.Json.JsonSerializer.Deserialize<List<SubtitleCue>>(
                    await File.ReadAllTextAsync(cacheFile, ct).ConfigureAwait(false));
                if (cached != null)
                {
                    return cached;
                }
            }

            var (path, streamIdx, duration) = ResolveMedia(itemId, audioStreamIndex);
            if (duration > 0)
            {
                windowSeconds = Math.Min(windowSeconds, duration - startSeconds);
                if (windowSeconds <= 0.5)
                {
                    return new List<SubtitleCue>();
                }
            }

            var tempFile = Path.Combine(Path.GetTempPath(), $"lmsubs-win-{Guid.NewGuid():N}.mp3");
            try
            {
                await _extractor.ExtractAsync(path, streamIdx, startSeconds, windowSeconds, tempFile, config, ct).ConfigureAwait(false);
                var segments = await _openAi.TranscribeAsync(tempFile, startSeconds, windowSeconds, config, ct).ConfigureAwait(false);
                var cues = SubtitleText.Normalize(segments);

                if (translate && cues.Count > 0)
                {
                    cues = await TranslateCuesAsync(cues, lang, null, ct).ConfigureAwait(false);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                await File.WriteAllTextAsync(cacheFile, System.Text.Json.JsonSerializer.Serialize(cues), ct).ConfigureAwait(false);
                return cues;
            }
            finally
            {
                try { File.Delete(tempFile); } catch { /* best effort */ }
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
