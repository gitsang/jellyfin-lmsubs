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
        var segs = OpenAiClient.ParseTranscription("{\"text\":\"你好世界\"}", 30, 30);
        Assert.Single(segs);
        Assert.Equal(30, segs[0].StartSeconds);
        Assert.Equal(60, segs[0].EndSeconds);
        Assert.Equal("你好世界", segs[0].Text);
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
        var r = OpenAiClient.TryParseNumberedJson("{\"1\":\"你好\",\"2\":\"世界\"}", 2);
        Assert.NotNull(r);
        Assert.Equal(new[] { "你好", "世界" }, r);
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
        var r = OpenAiClient.ParseNumberedLines("1. 你好\n2、世界\n3: ok", 3);
        Assert.NotNull(r);
        Assert.Equal(new[] { "你好", "世界", "ok" }, r);
    }

    [Fact]
    public void Srt_RoundTrip()
    {
        var cues = new List<SubtitleCue>
        {
            new(0, 2.5, "Hello\n你好"),
            new(65.25, 70, "Second line")
        };
        var srt = SubtitleText.ToSrt(cues);
        Assert.Contains("00:00:00,000 --> 00:00:02,500", srt);
        Assert.Contains("00:01:05,250 --> 00:01:10,000", srt);
        var parsed = SubtitleText.ParseSrt(srt);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("Hello\n你好", parsed[0].Text);
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
    public void Normalize_SplitsLongCjkSegment()
    {
        var longText = new string('字', 200);
        var segs = new[] { new AsrSegment(0, 40, longText) };
        var cues = SubtitleText.Normalize(segs);
        Assert.True(cues.Count >= 3);
        Assert.All(cues, c => Assert.True(c.Text.Length <= 80));
        Assert.True(cues[^1].End <= 40.001);
        // ordered and non-overlapping
        for (var i = 1; i < cues.Count; i++)
        {
            Assert.True(cues[i].Start >= cues[i - 1].End - 0.001);
        }
    }

    [Fact]
    public void MergeRange_AppendDedupes()
    {
        var existing = new List<SubtitleCue> { new(0, 2, "a"), new(2, 4, "b") };
        var merged = SubtitleService.MergeRange(existing, new[] { new SubtitleCue(2, 4, "b"), new SubtitleCue(4, 6, "c") }, 4, 6, replace: false);
        Assert.Equal(3, merged.Count);
        Assert.Equal("c", merged[2].Text);
    }

    [Fact]
    public void MergeRange_ReplaceOverwritesRange()
    {
        var existing = new List<SubtitleCue> { new(0, 2, "old-head"), new(10, 12, "old-mid"), new(12, 14, "old-mid2"), new(30, 32, "old-tail") };
        var merged = SubtitleService.MergeRange(existing, new[] { new SubtitleCue(10, 13, "new-mid") }, 10, 20, replace: true);
        Assert.Equal(3, merged.Count);
        Assert.Equal("old-head", merged[0].Text);
        Assert.Equal("new-mid", merged[1].Text);
        Assert.Equal("old-tail", merged[2].Text);
    }

    [Fact]
    public void MergeRange_ReplaceWithEmptyClearsRange()
    {
        var existing = new List<SubtitleCue> { new(0, 2, "head"), new(10, 12, "mid") };
        var merged = SubtitleService.MergeRange(existing, Array.Empty<SubtitleCue>(), 10, 20, replace: true);
        Assert.Single(merged);
        Assert.Equal("head", merged[0].Text);
    }

    [Fact]
    public void Normalize_SplitsEnglishOnWords()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 40));
        var cues = SubtitleText.Normalize(new[] { new AsrSegment(10, 30, text) });
        Assert.True(cues.Count > 1);
        Assert.All(cues, c => Assert.DoesNotContain("wor ", c.Text + " "));
    }
}
