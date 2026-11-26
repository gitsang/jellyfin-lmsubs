using System.Collections.Concurrent;
using Jellyfin.Plugin.LmSubs.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LmSubs.Services;

// One ASR pass per item, shared by every target language. Translation jobs attach
// to the session and consume source cues chunk by chunk as they are produced.
public class AsrSession
{
    public Guid ItemId { get; init; }

    public string ItemName { get; set; } = string.Empty;

    public string Status { get; set; } = "queued";

    public string Stage { get; set; } = string.Empty;

    public double Progress { get; set; }

    public string? Error { get; set; }

    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public int ChunkCount { get; set; }

    public List<SubtitleCue> Cues { get; } = new();

    // Number of chunks whose cues are fully appended to Cues.
    public int ChunksDone { get; private set; }

    // Cumulative cue count after each chunk (index i = cues produced through chunk i).
    public List<int> ChunkEndCounts { get; } = new();

    public CancellationTokenSource Cts { get; } = new();

    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Raised after each chunk is appended and when the session finishes.
    public event Action? ChunkReady;

    public void AppendChunk(List<SubtitleCue> cues)
    {
        lock (Cues)
        {
            Cues.AddRange(cues);
            ChunksDone++;
            ChunkEndCounts.Add(Cues.Count);
            Progress = ChunkCount > 0 ? (double)ChunksDone / ChunkCount : 0;
        }

        ChunkReady?.Invoke();
    }

    public void NotifyFinished() => ChunkReady?.Invoke();

    public List<SubtitleCue> SnapshotCues()
    {
        lock (Cues)
        {
            return Cues.ToList();
        }
    }
}

// Per (item, target language) job riding on the shared ASR session.
public class GenerationJob
{
    public Guid ItemId { get; init; }

    public string ItemName { get; set; } = string.Empty;

    public string Status { get; set; } = "queued";

    public string Stage { get; set; } = string.Empty;

    public double Progress { get; set; }

    public double AsrProgress { get; set; }

    public double TranslateProgress { get; set; }

    public string? Error { get; set; }

    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public DateTime? FinishedUtc { get; set; }

    public string TargetLanguage { get; set; } = string.Empty;

    public bool Translate { get; set; }

    public CancellationTokenSource Cts { get; } = new();

    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

// ASR runs once per item; each target language gets its own translation job that
// consumes the shared transcript. The final SRT is rendered only when complete —
// Jellyfin's SubtitleManager then saves/registers it like any downloaded subtitle.
public class SubtitleService
{
    private readonly ILibraryManager _libraryManager;
    private readonly SubtitleStore _store;
    private readonly OpenAiClient _openAi;
    private readonly AudioExtractor _extractor;
    private readonly LmLog _logger;

    private readonly ConcurrentDictionary<Guid, AsrSession> _sessions = new();
    private readonly ConcurrentDictionary<(Guid ItemId, string Lang), GenerationJob> _jobs = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _controlLocks = new();
    private readonly SemaphoreSlim _asrGate = new(1, 1);

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
        _logger = new LmLog(logger);
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

    // Search dialog language (ISO code like "chi"/"zh" or a display name) -> prompt-friendly name.
    public static string NormalizeTargetLanguage(string? language)
    {
        var l = (language ?? string.Empty).Trim();
        if (l.Length == 0)
        {
            return "Chinese (Simplified)";
        }

        return l.ToLowerInvariant() switch
        {
            "chi" or "zho" or "zh" or "zh-cn" or "zh-hans" => "Chinese (Simplified)",
            "zh-tw" or "zh-hk" or "zh-hant" => "Chinese (Traditional)",
            "eng" or "en" => "English",
            "jpn" or "ja" => "Japanese",
            "kor" or "ko" => "Korean",
            "fre" or "fra" or "fr" => "French",
            "ger" or "deu" or "de" => "German",
            "spa" or "es" => "Spanish",
            "rus" or "ru" => "Russian",
            "ita" or "it" => "Italian",
            "por" or "pt" => "Portuguese",
            "ara" or "ar" => "Arabic",
            "tha" or "th" => "Thai",
            "vie" or "vi" => "Vietnamese",
            "und" => "Chinese (Simplified)",
            _ => l
        };
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

    public GenerationJob? GetJob(Guid itemId, string? targetLanguage = null)
    {
        if (targetLanguage != null)
        {
            return _jobs.TryGetValue((itemId, SubtitleStore.LangSlug(targetLanguage)), out var j) ? j : null;
        }

        return _jobs.Where(kv => kv.Key.ItemId == itemId)
            .OrderByDescending(kv => kv.Value.StartedUtc)
            .Select(kv => kv.Value)
            .FirstOrDefault();
    }

    public bool Cancel(Guid itemId)
    {
        var cancelled = false;
        foreach (var kv in _jobs.Where(kv => kv.Key.ItemId == itemId && kv.Value.Status is "queued" or "running"))
        {
            kv.Value.Cts.Cancel();
            cancelled = true;
        }

        if (_sessions.TryGetValue(itemId, out var session) && session.Status is "queued" or "running")
        {
            session.Cts.Cancel();
            cancelled = true;
        }

        return cancelled;
    }

    public GenerationJob EnsureRunning(Guid itemId, bool translate = true, string? targetLanguage = null, bool restart = false) =>
        EnsureRunning(itemId, out _, translate, targetLanguage, restart);

    public GenerationJob EnsureRunning(Guid itemId, out bool created, bool translate = true, string? targetLanguage = null, bool restart = false)
    {
        var lang = NormalizeTargetLanguage(targetLanguage);
        var key = (itemId, translate ? SubtitleStore.LangSlug(lang) : "source");

        var controlLock = _controlLocks.GetOrAdd(itemId, _ => new SemaphoreSlim(1, 1));
        controlLock.Wait();
        try
        {
            created = false;
            if (_jobs.TryGetValue(key, out var existing))
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

            var session = EnsureSession(itemId, restart);
            var job = new GenerationJob
            {
                ItemId = itemId,
                TargetLanguage = lang,
                Translate = translate,
                ItemName = session.ItemName
            };

            _jobs[key] = job;
            created = true;
            _ = Task.Run(() => RunJobAsync(job, session));
            return job;
        }
        finally
        {
            controlLock.Release();
        }
    }

    // Caller holds the item control lock.
    private AsrSession EnsureSession(Guid itemId, bool restart)
    {
        if (_sessions.TryGetValue(itemId, out var existing))
        {
            if (!restart && existing.Status is "queued" or "running" or "done")
            {
                return existing;
            }

            existing.Cts.Cancel();
        }

        var session = new AsrSession
        {
            ItemId = itemId,
            ItemName = GetVideo(itemId).Name ?? itemId.ToString()
        };
        _sessions[itemId] = session;
        _ = Task.Run(() => RunAsrAsync(session));
        return session;
    }

    private async Task RunAsrAsync(AsrSession session)
    {
        // One ffmpeg/ASR pass at a time server-wide; translation jobs run freely.
        try
        {
            await _asrGate.WaitAsync(session.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            FinishSession(session, "cancelled");
            return;
        }

        var config = Config;
        var tempDir = Path.Combine(Path.GetTempPath(), "lmsubs-" + session.ItemId.ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var ct = session.Cts.Token;
            session.Status = "running";

            var (path, duration) = ResolveMedia(session.ItemId);
            if (duration <= 0)
            {
                duration = await _extractor.GetDurationSecondsAsync(path, config, ct).ConfigureAwait(false);
            }

            if (duration <= 0)
            {
                throw new InvalidOperationException("Cannot determine media duration (ffprobe unavailable? set the ffmpeg path in plugin settings)");
            }

            var chunk = Math.Clamp(config.ChunkSeconds, 30, 3600);
            session.ChunkCount = (int)Math.Ceiling(duration / chunk);
            _logger.Debug("LMSubs: ASR start {Item}: {Duration:0}s, {Chunks} chunks of {Chunk}s", session.ItemName, duration, session.ChunkCount, chunk);

            for (var i = 0; i < session.ChunkCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var cs = (double)i * chunk;
                var len = Math.Min(chunk, duration - cs);
                session.Stage = $"Transcribing {fmt(cs)}–{fmt(cs + len)} ({i + 1}/{session.ChunkCount})";

                var file = Path.Combine(tempDir, $"chunk_{i:0000}.mp3");
                await _extractor.ExtractAsync(path, null, cs, len, file, config, ct).ConfigureAwait(false);
                List<AsrSegment> segments;
                try
                {
                    segments = await _openAi.TranscribeAsync(file, cs, len, config, ct).ConfigureAwait(false);
                }
                finally
                {
                    try { File.Delete(file); } catch { /* best effort */ }
                }

                var chunkCues = SubtitleText.Normalize(segments);
                session.AppendChunk(chunkCues);
                _store.SaveSourceCues(session.ItemId, session.SnapshotCues());
                _logger.Debug("LMSubs: ASR chunk {Index}/{Total} of {Item}: {Cues} cues", i + 1, session.ChunkCount, session.ItemName, chunkCues.Count);
            }

            _store.Render(session.ItemId, translated: false, "source", session.SnapshotCues());
            _logger.Info("LMSubs: transcribed {Item} ({Chunks} chunks)", session.ItemName, session.ChunkCount);
            FinishSession(session, "done");
        }
        catch (OperationCanceledException)
        {
            FinishSession(session, "cancelled");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "LMSubs: ASR failed for {Item}", session.ItemName);
            session.Error = ex.Message;
            FinishSession(session, "error");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
            _asrGate.Release();
        }

        static string fmt(double s) => TimeSpan.FromSeconds(s).ToString(@"hh\:mm\:ss");
    }

    private static void FinishSession(AsrSession session, string status)
    {
        session.Status = status;
        if (status == "done")
        {
            session.Progress = 1;
            session.Stage = "Done";
        }

        session.Completion.TrySetResult();
        session.NotifyFinished();
    }

    private async Task RunJobAsync(GenerationJob job, AsrSession session)
    {
        var config = Config;
        var context = new List<(string Src, string Dst)>();
        var ctxMax = Math.Clamp(config.ContextPairs, 0, 30);
        var finalCues = new List<SubtitleCue>();
        var translatedCount = 0;
        var consumedChunks = 0;

        try
        {
            var ct = job.Cts.Token;
            job.Status = "running";

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                List<SubtitleCue> snapshot;
                int chunksDone;
                int chunkCount;
                int fullyTranslatedChunks;
                lock (session.Cues)
                {
                    snapshot = session.Cues.ToList();
                    chunksDone = session.ChunksDone;
                    chunkCount = Math.Max(1, session.ChunkCount);
                    fullyTranslatedChunks = session.ChunkEndCounts.Count(end => end <= translatedCount);
                }

                var finished = session.Status is "done" or "error" or "cancelled";
                var baseP = (double)fullyTranslatedChunks / chunkCount;

                // Nothing new to do: wait for the next chunk instead of busy-looping.
                var hasWork = job.Translate
                    ? snapshot.Count > translatedCount
                    : consumedChunks < chunksDone;
                if (!hasWork && !finished)
                {
                    await WaitForChunkAsync(session, chunksDone, ct).ConfigureAwait(false);
                    continue;
                }

                if (job.Translate && snapshot.Count > translatedCount)
                {
                    var pending = snapshot.Skip(translatedCount).ToList();
                    job.Stage = $"Translating lines {translatedCount + 1}–{snapshot.Count}";
                    _logger.Debug("LMSubs: translate {Item} ({Lang}): lines {From}-{To}", job.ItemName, job.TargetLanguage, translatedCount + 1, snapshot.Count);

                    // Interpolate between the last fully translated chunk and ASR's position.
                    var span = (double)chunksDone / chunkCount - baseP;
                    var done = await TranslateCuesAsync(pending, job.TargetLanguage, context, ctxMax, ct,
                        fraction =>
                        {
                            job.AsrProgress = session.Progress;
                            job.TranslateProgress = Math.Min(1, baseP + Math.Max(0, span) * fraction);
                        }).ConfigureAwait(false);
                    finalCues.AddRange(done);
                    translatedCount = snapshot.Count;
                    _store.SaveCues(job.ItemId, job.TargetLanguage, finalCues);
                }

                consumedChunks = chunksDone;

                job.AsrProgress = session.Progress;
                if (job.Translate)
                {
                    lock (session.Cues)
                    {
                        fullyTranslatedChunks = session.ChunkEndCounts.Count(end => end <= translatedCount);
                    }

                    job.TranslateProgress = Math.Max(job.TranslateProgress, (double)fullyTranslatedChunks / chunkCount);
                    job.Progress = job.TranslateProgress;
                }
                else
                {
                    job.TranslateProgress = session.Progress;
                    job.Progress = session.Progress;
                }

                if (finished)
                {
                    break;
                }

                await WaitForChunkAsync(session, chunksDone, ct).ConfigureAwait(false);
            }

            if (session.Status != "done")
            {
                job.Error = session.Error ?? $"ASR {session.Status}";
                Finish(job, session.Status == "cancelled" ? "cancelled" : "error");
                return;
            }

            if (!job.Translate)
            {
                finalCues = session.SnapshotCues();
            }

            _store.Render(job.ItemId, job.Translate, job.TargetLanguage, finalCues);
            _logger.Info("LMSubs: generated {Count} cues for {Item} ({Lang})",
                finalCues.Count, job.ItemName, job.Translate ? job.TargetLanguage : "source");
            Finish(job, "done");
        }
        catch (OperationCanceledException)
        {
            Finish(job, "cancelled");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "LMSubs: translation failed for {Item} ({Lang})", job.ItemName, job.TargetLanguage);
            job.Error = ex.Message;
            Finish(job, "error");
        }
    }

    private static async Task WaitForChunkAsync(AsrSession session, int chunksDone, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChunk() => tcs.TrySetResult();
        session.ChunkReady += OnChunk;
        try
        {
            // Re-check after subscribing to avoid a lost wakeup.
            if (session.Status is "queued" or "running" && session.ChunksDone == chunksDone)
            {
                using var reg = ct.Register(() => tcs.TrySetResult());
                await Task.WhenAny(tcs.Task, session.Completion.Task).ConfigureAwait(false);
            }
        }
        finally
        {
            session.ChunkReady -= OnChunk;
        }

        ct.ThrowIfCancellationRequested();
    }

    private static void Finish(GenerationJob job, string status)
    {
        job.Status = status;
        job.FinishedUtc = DateTime.UtcNow;
        if (status == "done")
        {
            job.Progress = 1;
            job.TranslateProgress = 1;
            job.AsrProgress = 1;
            job.Stage = "Done";
        }

        job.Completion.TrySetResult();
    }

    // Rough token estimate: CJK ~1 token/char, everything else ~1 token/4 chars.
    internal static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var cjk = 0;
        foreach (var ch in text)
        {
            if (ch >= 0x2E80 && ch <= 0xFAFF)
            {
                cjk++;
            }
        }

        return cjk + (text.Length - cjk + 3) / 4;
    }

    private async Task<List<SubtitleCue>> TranslateCuesAsync(
        List<SubtitleCue> cues, string targetLanguage,
        List<(string Src, string Dst)> context, int ctxMax, CancellationToken ct,
        Action<double>? onBatch = null)
    {
        var config = Config;
        var batchSize = Math.Clamp(config.TranslateBatchSize, 1, 100);
        var budget = Math.Max(0, config.ContextSize);
        var output = new List<SubtitleCue>(cues.Count);
        var offset = 0;

        while (offset < cues.Count)
        {
            ct.ThrowIfCancellationRequested();

            // Pack the batch under both the line cap and the estimated token budget.
            var fixedTokens = 400 + context.Sum(p => EstimateTokens(p.Src) + EstimateTokens(p.Dst));
            var count = 0;
            var batchTokens = 0;
            while (count < batchSize && offset + count < cues.Count)
            {
                var t = EstimateTokens(cues[offset + count].Text) + 8;
                if (budget > 0 && count > 0 && fixedTokens + batchTokens + t > budget)
                {
                    break;
                }

                batchTokens += t;
                count++;
            }

            var batch = cues.Skip(offset).Take(count).ToList();
            var texts = batch.Select(c => c.Text).ToList();
            var translations = await TranslateWithSplitAsync(texts, context, targetLanguage, config, ct).ConfigureAwait(false);
            offset += count;
            _logger.Debug("LMSubs: translate batch {From}-{To}/{Total} ({Lang}) ok, ~{Tokens} tokens", offset - count + 1, offset, cues.Count, targetLanguage, fixedTokens + batchTokens);
            onBatch?.Invoke((double)offset / cues.Count);

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

            // Keep the context itself inside the budget for the next request.
            if (budget > 0)
            {
                while (context.Count > 1 &&
                       400 + context.Sum(p => EstimateTokens(p.Src) + EstimateTokens(p.Dst)) > budget / 2)
                {
                    context.RemoveAt(0);
                }
            }
        }

        return output;
    }

    // On provider context-overflow errors, halve the batch and retry each half.
    private async Task<List<string>> TranslateWithSplitAsync(
        List<string> texts,
        List<(string Src, string Dst)> context,
        string targetLanguage,
        PluginConfiguration config,
        CancellationToken ct)
    {
        try
        {
            return await _openAi.TranslateBatchAsync(texts, context, targetLanguage, config, ct).ConfigureAwait(false);
        }
        catch (TokenLimitException ex) when (texts.Count > 1)
        {
            _logger.Warn("LMSubs: batch of {Count} lines exceeded the model context, splitting: {Error}", texts.Count, ex.Message);
            var mid = texts.Count / 2;
            var left = await TranslateWithSplitAsync(texts.Take(mid).ToList(), context, targetLanguage, config, ct).ConfigureAwait(false);
            var right = await TranslateWithSplitAsync(texts.Skip(mid).ToList(), context, targetLanguage, config, ct).ConfigureAwait(false);
            return left.Concat(right).ToList();
        }
        catch (TokenLimitException) when (texts.Count == 1)
        {
            // A single line still overflows (huge cue or context): drop context and retry once.
            _logger.Warn("LMSubs: single line exceeded the model context, retrying without context");
            return await _openAi.TranslateBatchAsync(texts, Array.Empty<(string Src, string Dst)>(), targetLanguage, config, ct).ConfigureAwait(false);
        }
    }

    public async Task<string> TranslateCachedAsync(Guid itemId, string? targetLanguage, CancellationToken ct)
    {
        var config = Config;
        var lang = NormalizeTargetLanguage(targetLanguage);
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