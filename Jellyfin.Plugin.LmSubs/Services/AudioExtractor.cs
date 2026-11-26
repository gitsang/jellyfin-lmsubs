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