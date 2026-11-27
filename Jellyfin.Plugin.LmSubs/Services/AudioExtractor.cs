using System.Diagnostics;
using Jellyfin.Plugin.LmSubs.Configuration;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.LmSubs.Services;

public class AudioExtractor
{
    private readonly IMediaEncoder _mediaEncoder;

    public AudioExtractor(IMediaEncoder mediaEncoder)
    {
        _mediaEncoder = mediaEncoder;
    }

    // Jellyfin's resolved encoder (config, bundled jellyfin-ffmpeg, or PATH) — bare
    // "ffmpeg" is not on PATH inside the official container.
    public string ResolveFfmpegPath(PluginConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.FfmpegPath))
        {
            return config.FfmpegPath;
        }

        var encoder = _mediaEncoder.EncoderPath;
        return string.IsNullOrWhiteSpace(encoder) ? "ffmpeg" : encoder;
    }

    private string ResolveFfprobePath(PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.FfmpegPath))
        {
            var probe = _mediaEncoder.ProbePath;
            if (!string.IsNullOrWhiteSpace(probe))
            {
                return probe;
            }
        }

        var ffmpeg = ResolveFfmpegPath(config);
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? string.Empty,
            Path.GetFileName(ffmpeg).Replace("ffmpeg", "ffprobe", StringComparison.OrdinalIgnoreCase));
        return File.Exists(ffprobe) ? ffprobe : "ffprobe";
    }

    public async Task<double> GetDurationSecondsAsync(string mediaPath, PluginConfiguration config, CancellationToken ct)
    {
        var ffprobe = ResolveFfprobePath(config);

        try
        {
            var (code, stdout, _) = await RunAsync(ffprobe,
                $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{mediaPath}\"", ct).ConfigureAwait(false);
            if (code == 0)
            {
                var line = stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
                if (double.TryParse(line, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0)
                {
                    return d;
                }
            }
        }
        catch
        {
        }

        return 0;
    }

    public async Task ExtractAsync(
        string mediaPath,
        int? audioStreamIndex,
        double startSeconds,
        double durationSeconds,
        string outputPath,
        PluginConfiguration config,
        CancellationToken ct)
    {
        var ffmpeg = ResolveFfmpegPath(config);
        var map = audioStreamIndex.HasValue ? $"-map 0:{audioStreamIndex.Value} " : "-map 0:a:0 ";
        var start = startSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var dur = durationSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var args = $"-hide_banner -loglevel error -y -ss {start} -i \"{mediaPath}\" -t {dur} {map}" +
                   $"-vn -sn -dn -ac 1 -ar 16000 -b:a {config.AudioBitrateKbps}k \"{outputPath}\"";

        var (code, _, err) = await RunAsync(ffmpeg, args, ct).ConfigureAwait(false);
        if (code != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new InvalidOperationException($"ffmpeg audio extraction failed (exit {code}): {err.Trim()}");
        }
    }

    // Speech activity via ffmpeg silencedetect on the already-extracted chunk audio.
    // Returns segment-relative speech ranges; empty list = detection unavailable.
    public async Task<List<(double Start, double End)>> DetectSpeechRangesAsync(
        string audioPath, double durationSeconds, PluginConfiguration config, CancellationToken ct)
    {
        try
        {
            var ffmpeg = ResolveFfmpegPath(config);
            var noise = Math.Clamp(config.SilenceNoiseDb, -60, -10);
            var minSilence = Math.Clamp(config.SilenceMinSeconds, 0.1, 5.0)
                .ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            var args = $"-hide_banner -nostats -i \"{audioPath}\" -af silencedetect=noise={noise}dB:d={minSilence} -f null -";
            var (code, _, err) = await RunAsync(ffmpeg, args, ct).ConfigureAwait(false);
            if (code != 0)
            {
                return new List<(double, double)>();
            }

            return ParseSpeechRanges(err, durationSeconds);
        }
        catch
        {
            return new List<(double, double)>();
        }
    }

    internal static List<(double Start, double End)> ParseSpeechRanges(string ffmpegStderr, double duration)
    {
        var silences = new List<(double Start, double End)>();
        double? openStart = null;
        foreach (var line in ffmpegStderr.Split('\n'))
        {
            var s = line.IndexOf("silence_start:", StringComparison.Ordinal);
            if (s >= 0 && double.TryParse(
                    line.AsSpan(s + 14).Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var st))
            {
                openStart = st;
                continue;
            }

            var e = line.IndexOf("silence_end:", StringComparison.Ordinal);
            if (e >= 0 && openStart.HasValue)
            {
                var rest = line.AsSpan(e + 12).Trim();
                var sp = rest.IndexOf(' ');
                var num = sp > 0 ? rest[..sp] : rest;
                if (double.TryParse(num, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var en))
                {
                    silences.Add((openStart.Value, en));
                    openStart = null;
                }
            }
        }

        // Trailing silence runs to the end of the audio.
        if (openStart.HasValue && openStart.Value < duration)
        {
            silences.Add((openStart.Value, duration));
        }

        var speech = new List<(double Start, double End)>();
        var t = 0.0;
        foreach (var (s, e) in silences.OrderBy(x => x.Start))
        {
            if (s > t)
            {
                speech.Add((t, Math.Min(s, duration)));
            }

            t = Math.Max(t, e);
        }

        if (t < duration)
        {
            speech.Add((t, duration));
        }

        return speech.Where(r => r.End - r.Start > 0.05).ToList();
    }

    private static async Task<(int Code, string StdOut, string StdErr)> RunAsync(string file, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {file}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }
}