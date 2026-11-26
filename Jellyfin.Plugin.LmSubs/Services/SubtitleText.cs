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

    public static List<SubtitleCue> Normalize(IEnumerable<AsrSegment> segments, double maxCharsPerCue = 80, double maxCueSeconds = 8)
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
            if (text.Length <= maxCharsPerCue && span <= maxCueSeconds)
            {
                cues.Add(new SubtitleCue(seg.StartSeconds, seg.EndSeconds, text));
                continue;
            }

            // Split proportionally by characters (works for CJK and space-separated text).
            var pieces = SplitText(text, (int)maxCharsPerCue);
            var totalChars = pieces.Sum(p => p.Length);
            var t = seg.StartSeconds;
            foreach (var piece in pieces)
            {
                var pieceSpan = Math.Max(span * piece.Length / Math.Max(totalChars, 1), 0.5);
                var end = Math.Min(t + pieceSpan, seg.EndSeconds);
                cues.Add(new SubtitleCue(t, Math.Max(end, t + 0.3), piece));
                t = end;
            }
        }

        return cues;
    }

    private static List<string> SplitText(string text, int maxLen)
    {
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
