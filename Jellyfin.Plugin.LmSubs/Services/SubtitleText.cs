using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.LmSubs.Services;

public record SubtitleCue(double Start, double End, string Text);

public static class SubtitleText
{
    public static string FormatTimestamp(double seconds, bool vtt)
    {
        if (seconds < 0)
        {
            seconds = 0;
        }

        var ts = TimeSpan.FromSeconds(seconds);
        var s = string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}{3}{4:000}",
            (int)ts.TotalHours, ts.Minutes, ts.Seconds, vtt ? '.' : ',', ts.Milliseconds);
        return s;
    }

    public static string ToSrt(IEnumerable<SubtitleCue> cues)
    {
        var sb = new StringBuilder();
        var i = 1;
        foreach (var c in cues)
        {
            sb.Append(i++.ToString(CultureInfo.InvariantCulture)).Append('\n')
              .Append(FormatTimestamp(c.Start, false)).Append(" --> ").Append(FormatTimestamp(c.End, false)).Append('\n')
              .Append(c.Text.Replace("\r\n", "\n")).Append("\n\n");
        }

        return sb.ToString();
    }

    public static string ToVtt(IEnumerable<SubtitleCue> cues)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var c in cues)
        {
            sb.Append(FormatTimestamp(c.Start, true)).Append(" --> ").Append(FormatTimestamp(c.End, true)).Append('\n')
              .Append(c.Text.Replace("\r\n", "\n")).Append("\n\n");
        }

        return sb.ToString();
    }

    public static List<SubtitleCue> ParseSrt(string content)
    {
        var cues = new List<SubtitleCue>();
        var blocks = content.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            var timeIdx = Array.FindIndex(lines, l => l.Contains("-->", StringComparison.Ordinal));
            if (timeIdx < 0)
            {
                continue;
            }

            var parts = lines[timeIdx].Split("-->", StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                !TryParseTimestamp(parts[0], out var start) ||
                !TryParseTimestamp(parts[1].Split(' ')[0], out var end))
            {
                continue;
            }

            var text = string.Join("\n", lines.Skip(timeIdx + 1)).Trim();
            if (text.Length > 0)
            {
                cues.Add(new SubtitleCue(start, end, text));
            }
        }

        return cues;
    }

    private static bool TryParseTimestamp(string s, out double seconds)
    {
        seconds = 0;
        s = s.Trim().Replace(',', '.');
        var parts = s.Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        double mult = 1;
        foreach (var p in parts.Reverse())
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                return false;
            }

            seconds += v * mult;
            mult *= 60;
        }

        return true;
    }

    private const string SentenceEnders = "。！？!?…";
    private const string ClauseBreaks = "，、；：,;:";

    public static List<SubtitleCue> Normalize(
        IEnumerable<AsrSegment> segments,
        double maxCharsPerCue = 80,
        double maxCueSeconds = 8,
        IReadOnlyList<(double Start, double End)>? speechRanges = null)
    {
        var cues = new List<SubtitleCue>();
        foreach (var seg in segments.OrderBy(s => s.StartSeconds))
        {
            var text = seg.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var span = Math.Max(seg.EndSeconds - seg.StartSeconds, 0.5);

            // Sentence-first: never cut mid-sentence when punctuation tells us where
            // sentences end; time is apportioned by character count within the segment.
            var pieces = SplitSentences(text)
                .SelectMany(s => SplitText(s, (int)maxCharsPerCue))
                .ToList();

            var totalChars = pieces.Sum(p => p.Length);

            // Speech ranges (relative to the segment start) let us skip silences:
            // distribute characters over the speech timeline, then map back.
            var ranges = speechRanges != null
                ? speechRanges
                    .Select(r => (Start: Math.Max(0, r.Start - seg.StartSeconds), End: Math.Min(span, r.End - seg.StartSeconds)))
                    .Where(r => r.End > r.Start)
                    .OrderBy(r => r.Start)
                    .ToList()
                : null;
            var speechTotal = ranges?.Sum(r => r.End - r.Start) ?? 0;

            if (ranges != null && ranges.Count > 0 && speechTotal > 0.5)
            {
                var charsDone = 0;
                foreach (var piece in pieces)
                {
                    var s0 = (double)charsDone / totalChars * speechTotal;
                    charsDone += piece.Length;
                    var s1 = (double)charsDone / totalChars * speechTotal;
                    var start = seg.StartSeconds + SpeechToReal(ranges, s0);
                    var end = seg.StartSeconds + SpeechToReal(ranges, s1);

                    // Snap near silence boundaries: sentence edges usually ARE pauses.
                    start = Snap(start - seg.StartSeconds, ranges, 1.5) + seg.StartSeconds;
                    end = Snap(end - seg.StartSeconds, ranges, 1.5) + seg.StartSeconds;

                    cues.Add(new SubtitleCue(start, Math.Max(end, start + 0.3), piece));
                }
            }
            else
            {
                var t = seg.StartSeconds;
                foreach (var piece in pieces)
                {
                    var pieceSpan = Math.Max(span * piece.Length / Math.Max(totalChars, 1), 0.5);
                    var end = Math.Min(t + pieceSpan, seg.EndSeconds);
                    cues.Add(new SubtitleCue(t, Math.Max(end, t + 0.3), piece));
                    t = end;
                }
            }
        }

        // Keep cues monotonic after snapping.
        for (var i = 1; i < cues.Count; i++)
        {
            if (cues[i].Start < cues[i - 1].End)
            {
                var end = Math.Max(cues[i].End, cues[i - 1].End + 0.3);
                cues[i] = cues[i] with { Start = cues[i - 1].End, End = end };
            }
        }

        return cues;
    }

    // Map a position on the concatenated speech timeline back to segment-relative time.
    internal static double SpeechToReal(IReadOnlyList<(double Start, double End)> ranges, double speechPos)
    {
        var acc = 0.0;
        foreach (var r in ranges)
        {
            var len = r.End - r.Start;
            if (speechPos <= acc + len)
            {
                return r.Start + (speechPos - acc);
            }

            acc += len;
        }

        return ranges[^1].End;
    }

    // Pull a timestamp to a nearby speech-range edge when within tolerance.
    internal static double Snap(double t, IReadOnlyList<(double Start, double End)> ranges, double tolerance)
    {
        double best = t, bestDist = tolerance;
        foreach (var r in ranges)
        {
            foreach (var edge in new[] { r.Start, r.End })
            {
                var d = Math.Abs(edge - t);
                if (d < bestDist)
                {
                    best = edge;
                    bestDist = d;
                }
            }
        }

        return best;
    }

    // Split after sentence-ending punctuation (。！？!?…), keeping the punctuation
    // attached. Latin '.' ends a sentence only when followed by space+capital/end,
    // so abbreviations and decimals survive.
    internal static List<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var isEnd = SentenceEnders.Contains(ch);
            if (!isEnd && ch == '.')
            {
                var next = i + 1;
                while (next < text.Length && text[next] == ' ')
                {
                    next++;
                }

                isEnd = next >= text.Length || char.IsUpper(text[next]);

                // Common abbreviations keep their dot even before a capital.
                if (isEnd)
                {
                    foreach (var abbr in new[] { "Dr", "Mr", "Mrs", "Ms", "Prof", "St", "vs", "etc", "e.g", "i.e" })
                    {
                        var at = i - abbr.Length;
                        if (at >= 0 && text.AsSpan(at, abbr.Length).Equals(abbr, StringComparison.Ordinal) &&
                            (at == 0 || !char.IsLetter(text[at - 1])))
                        {
                            isEnd = false;
                            break;
                        }
                    }
                }
            }

            if (!isEnd)
            {
                continue;
            }

            // Absorb consecutive enders and closing quotes/brackets.
            var end = i + 1;
            while (end < text.Length && (SentenceEnders.Contains(text[end]) || text[end] is '”' or '’' or '」' or '』' or ')' or '"' or '\''))
            {
                end++;
            }

            var sentence = text.Substring(start, end - start).Trim();
            if (sentence.Length > 0)
            {
                sentences.Add(sentence);
            }

            start = end;
            i = end - 1;
        }

        var tail = text.Substring(start).Trim();
        if (tail.Length > 0)
        {
            sentences.Add(tail);
        }

        return sentences;
    }

    // Over-length sentences: prefer clause punctuation (，、；：,;:), then word
    // boundaries for spaced text, then a hard character cut as the last resort.
    private static List<string> SplitText(string text, int maxLen)
    {
        if (text.Length <= maxLen)
        {
            return new List<string> { text };
        }

        // Try the last clause break within the limit (not too early: >= 40% of maxLen).
        var cut = -1;
        for (var i = Math.Min(maxLen, text.Length) - 1; i >= maxLen * 2 / 5; i--)
        {
            if (ClauseBreaks.Contains(text[i]))
            {
                cut = i + 1;
                break;
            }
        }

        if (cut > 0 && cut < text.Length)
        {
            var head = text.Substring(0, cut).Trim();
            var rest = text.Substring(cut).Trim();
            var result = new List<string> { head };
            result.AddRange(SplitText(rest, maxLen));
            return result;
        }

        var pieces = new List<string>();
        var words = text.Contains(' ') ? text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList() : null;
        if (words != null)
        {
            var current = new StringBuilder();
            foreach (var w in words)
            {
                if (current.Length > 0 && current.Length + 1 + w.Length > maxLen)
                {
                    pieces.Add(current.ToString());
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.Append(' ');
                }

                current.Append(w);
            }

            if (current.Length > 0)
            {
                pieces.Add(current.ToString());
            }
        }
        else
        {
            for (var i = 0; i < text.Length; i += maxLen)
            {
                pieces.Add(text.Substring(i, Math.Min(maxLen, text.Length - i)));
            }
        }

        return pieces;
    }
}
