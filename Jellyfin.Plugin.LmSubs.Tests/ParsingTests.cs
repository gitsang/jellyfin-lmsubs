using Jellyfin.Plugin.LmSubs.Providers;
using Jellyfin.Plugin.LmSubs.Services;
using Xunit;

namespace Jellyfin.Plugin.LmSubs.Tests;

public class ParsingTests
{
    [Fact]
    public void Endpoint_NormalBaseUrl()
    {
        Assert.Equal("https://api.openai.com/v1/audio/transcriptions",
            OpenAiClient.BuildEndpoint("https://api.openai.com/v1", "/audio/transcriptions"));
        Assert.Equal("https://api.openai.com/v1/chat/completions",
            OpenAiClient.BuildEndpoint("https://api.openai.com/v1/", "/chat/completions"));
    }

    [Fact]
    public void Endpoint_FullEndpointPassthrough()
    {
        Assert.Equal("https://x.example/v1/chat/completions",
            OpenAiClient.BuildEndpoint("https://x.example/v1/chat/completions", "/chat/completions"));
    }

    [Fact]
    public void Transcription_VerboseJsonSegments()
    {
        var body = """
        {"task":"transcribe","language":"en","duration":10.0,
         "text":"Hello world. Second line.",
         "segments":[{"start":0.0,"end":4.5,"text":" Hello world."},
                     {"start":4.5,"end":9.8,"text":" Second line."}]}
        """;
        var segs = OpenAiClient.ParseTranscription(body, 600, 10);
        Assert.Equal(2, segs.Count);
        Assert.Equal(600, segs[0].StartSeconds);
        Assert.Equal(604.5, segs[0].EndSeconds);
        Assert.Equal("Hello world.", segs[0].Text);
        Assert.Equal(609.8, segs[1].EndSeconds);
    }

    [Fact]
    public void Transcription_PlainJsonFallback()
    {
        var segs = OpenAiClient.ParseTranscription("{\"text\":\"hello world\"}", 30, 30);
        Assert.Single(segs);
        Assert.Equal(30, segs[0].StartSeconds);
        Assert.Equal(60, segs[0].EndSeconds);
        Assert.Equal("hello world", segs[0].Text);
    }

    [Fact]
    public void Transcription_PlainTextFallback()
    {
        var segs = OpenAiClient.ParseTranscription("just text", 0, 12);
        Assert.Single(segs);
        Assert.Equal("just text", segs[0].Text);
    }

    [Fact]
    public void NumberedJson_ParsesAligned()
    {
        var r = OpenAiClient.TryParseNumberedJson("{\"1\":\"hello\",\"2\":\"world\"}", 2);
        Assert.NotNull(r);
        Assert.Equal(new[] { "hello", "world" }, r);
    }

    [Fact]
    public void NumberedJson_StripsCodeFence()
    {
        var r = OpenAiClient.TryParseNumberedJson("```json\n{\"1\":\"a\"}\n```", 1);
        Assert.NotNull(r);
        Assert.Equal("a", r![0]);
    }

    [Fact]
    public void NumberedJson_MissingKeyFails()
    {
        Assert.Null(OpenAiClient.TryParseNumberedJson("{\"1\":\"a\"}", 2));
    }

    [Fact]
    public void NumberedLines_Parses()
    {
        var r = OpenAiClient.ParseNumberedLines("1. hello\n2. world\n3: ok", 3);
        Assert.NotNull(r);
        Assert.Equal(new[] { "hello", "world", "ok" }, r);
    }

    [Fact]
    public void Srt_RoundTrip()
    {
        var cues = new List<SubtitleCue>
        {
            new(0, 2.5, "Hello\nBonjour"),
            new(65.25, 70, "Second line")
        };
        var srt = SubtitleText.ToSrt(cues);
        Assert.Contains("00:00:00,000 --> 00:00:02,500", srt);
        Assert.Contains("00:01:05,250 --> 00:01:10,000", srt);
        var parsed = SubtitleText.ParseSrt(srt);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("Hello\nBonjour", parsed[0].Text);
        Assert.Equal(65.25, parsed[1].Start, 3);
    }

    [Fact]
    public void Vtt_Format()
    {
        var vtt = SubtitleText.ToVtt(new[] { new SubtitleCue(1.5, 3, "hi") });
        Assert.StartsWith("WEBVTT", vtt);
        Assert.Contains("00:00:01.500 --> 00:00:03.000", vtt);
    }

    [Fact]
    public void Normalize_SplitsLongNoSpaceSegment()
    {
        var longText = string.Join(string.Empty, Enumerable.Repeat("word ", 60)).Trim();
        var segs = new[] { new AsrSegment(0, 40, longText) };
        var cues = SubtitleText.Normalize(segs);
        Assert.True(cues.Count >= 3);
        Assert.All(cues, c => Assert.True(c.Text.Length <= 80));
        Assert.True(cues[^1].End <= 40.001);
        for (var i = 1; i < cues.Count; i++)
        {
            Assert.True(cues[i].Start >= cues[i - 1].End - 0.001);
        }
    }

    [Fact]
    public void Normalize_SplitsEnglishOnWords()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 40));
        var cues = SubtitleText.Normalize(new[] { new AsrSegment(10, 30, text) });
        Assert.True(cues.Count > 1);
        Assert.All(cues, c => Assert.DoesNotContain("wor ", c.Text + " "));
    }

    [Fact]
    public void TargetLanguage_IsoCodesMapped()
    {
        Assert.Equal("Chinese (Simplified)", SubtitleService.NormalizeTargetLanguage("chi"));
        Assert.Equal("Chinese (Simplified)", SubtitleService.NormalizeTargetLanguage("zh"));
        Assert.Equal("Chinese (Traditional)", SubtitleService.NormalizeTargetLanguage("zh-tw"));
        Assert.Equal("English", SubtitleService.NormalizeTargetLanguage("eng"));
        Assert.Equal("Japanese", SubtitleService.NormalizeTargetLanguage("ja"));
        Assert.Equal("Klingon", SubtitleService.NormalizeTargetLanguage("Klingon"));
        Assert.Equal("Chinese (Simplified)", SubtitleService.NormalizeTargetLanguage(null));
    }

    [Fact]
    public void SubtitleId_RoundTrips()
    {
        var id = Guid.NewGuid();
        var built = LmSubtitleProvider.BuildId(id, "chi", true);
        var (parsed, lang, translate) = LmSubtitleProvider.ParseId(built);
        Assert.Equal(id, parsed);
        Assert.Equal("chi", lang);
        Assert.True(translate);

        var (_, lang2, translate2) = LmSubtitleProvider.ParseId(LmSubtitleProvider.BuildId(id, "eng", false));
        Assert.Equal("eng", lang2);
        Assert.False(translate2);

        // Legacy bare-guid id still parses.
        var (parsed3, _, _) = LmSubtitleProvider.ParseId(id.ToString("N"));
        Assert.Equal(id, parsed3);
        Assert.Equal(Guid.Empty, LmSubtitleProvider.ParseId("garbage").ItemId);
    }

    [Fact]
    public void AsrSession_SharedProgressAndChunkBoundaries()
    {
        var session = new AsrSession { ItemId = Guid.NewGuid(), ChunkCount = 3 };
        var notified = 0;
        session.ChunkReady += () => notified++;

        session.AppendChunk(new List<SubtitleCue> { new(0, 1, "a"), new(1, 2, "b") });
        session.AppendChunk(new List<SubtitleCue> { new(2, 3, "c") });

        Assert.Equal(2, session.ChunksDone);
        Assert.Equal(2.0 / 3, session.Progress, 3);
        Assert.Equal(new[] { 2, 3 }, session.ChunkEndCounts);
        Assert.Equal(2, notified);
        Assert.Equal(3, session.SnapshotCues().Count);

        // A consumer that translated 2 cues has fully covered chunk 1 only.
        Assert.Equal(1, session.ChunkEndCounts.Count(end => end <= 2));
        Assert.Equal(2, session.ChunkEndCounts.Count(end => end <= 3));
    }

    [Fact]
    public void EstimateTokens_CjkAndLatin()
    {
        Assert.Equal(12, SubtitleService.EstimateTokens("你好世界测试字幕一二三四"));
        Assert.Equal(3, SubtitleService.EstimateTokens("hello world!")); // 12 chars -> 3
        Assert.Equal(0, SubtitleService.EstimateTokens(""));
        Assert.Equal(0, SubtitleService.EstimateTokens(null));
        // Mixed: 2 CJK + 6 latin -> 2 + 2
        Assert.Equal(4, SubtitleService.EstimateTokens("你好 abcde")); // 2 CJK + 6 other -> 2 + (6+3)/4
    }

    [Fact]
    public void SplitSentences_CjkEnders()
    {
        var parts = SubtitleText.SplitSentences("你好吗？我很好。今天天气不错！走吧…");
        Assert.Equal(new[] { "你好吗？", "我很好。", "今天天气不错！", "走吧…" }, parts);
    }

    [Fact]
    public void SplitSentences_LatinDotRules()
    {
        var parts = SubtitleText.SplitSentences("Hello there. How are you? I'm fine! See Dr. Smith at 3.5 pm.");
        Assert.Equal(new[] { "Hello there.", "How are you?", "I'm fine!", "See Dr. Smith at 3.5 pm." }, parts);
    }

    [Fact]
    public void Normalize_KeepsSentencesWhole()
    {
        // 3 sentences, total > 80 chars: each cue must be exactly one sentence.
        var s1 = new string('一', 30) + "。";
        var s2 = new string('二', 30) + "？";
        var s3 = new string('三', 30) + "！";
        var cues = SubtitleText.Normalize(new[] { new AsrSegment(0, 30, s1 + s2 + s3) });
        Assert.Equal(3, cues.Count);
        Assert.Equal(s1, cues[0].Text);
        Assert.Equal(s2, cues[1].Text);
        Assert.Equal(s3, cues[2].Text);
        // Time proportional to chars: equal sentences -> ~10s each.
        Assert.Equal(10, cues[0].End, 1);
        Assert.Equal(20, cues[1].End, 1);
        Assert.Equal(30, cues[2].End, 1);
    }

    [Fact]
    public void Normalize_LongSentenceSplitsAtClause()
    {
        var clauseA = new string('甲', 45);
        var clauseB = new string('乙', 45);
        var cues = SubtitleText.Normalize(new[] { new AsrSegment(0, 20, clauseA + "，" + clauseB + "。") });
        Assert.Equal(2, cues.Count);
        Assert.EndsWith("，", cues[0].Text);
        Assert.EndsWith("。", cues[1].Text);
        Assert.DoesNotContain("乙", cues[0].Text);
    }

    [Fact]
    public void Normalize_ShortSentenceLongSpanStaysOneCue()
    {
        // Timestamp-less provider: one short sentence spread over a 600s chunk.
        var cues = SubtitleText.Normalize(new[] { new AsrSegment(0, 600, "你好。") });
        Assert.Single(cues);
        Assert.Equal("你好。", cues[0].Text);
    }
}
