using System.Diagnostics;
using Jellyfin.Plugin.LmSubs.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;

namespace Jellyfin.Plugin.LmSubs.Services;

/// <summary>
/// Extracts audio windows from media files with ffmpeg (16 kHz mono mp3, ASR-friendly).
/// </summary>
public class AudioExtractor
{
    private readonly IServerConfigurationManager _configurationManager;

    public AudioExtractor(IServerConfigurationManager configurationManager)
    {
        _configurationManager = configurationManager;
    }

    public string ResolveFfmpegPath(PluginConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.FfmpegPath))
        {
            return config.FfmpegPath;
        }

        // Prefer Jellyfin's configured encoder path when available.
        try
        {
            var encoding = (EncodingOptions)_configurationManager.GetConfiguration("encoding");
            if (encoding != null && !string.IsNullOrWhiteSpace(encoding.EncoderAppPath))
            {
                return encoding.EncoderAppPath;
            }
        }
        catch
        {
            // fall through to PATH
        }

        return "ffmpeg";
    }

    /// <summary>Probe media duration in seconds via ffprobe; returns 0 when unavailable.</summary>
    public async Task<double> GetDurationSecondsAsync(string mediaPath, PluginConfiguration config, CancellationToken ct)
    {
        var ffmpeg = ResolveFfmpegPath(config);
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? string.Empty,
            Path.GetFileName(ffmpeg).Replace("ffmpeg", "ffprobe", StringComparison.OrdinalIgnoreCase));
        if (!File.Exists(ffprobe))
        {
            ffprobe = "ffprobe";
        }

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
            // ignore — duration is best-effort
        }

        return 0;
    }

    /// <summary>
    /// Extract [startSeconds, startSeconds+durationSeconds) of the given audio stream to an mp3 file.
    /// </summary>
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
            throw new InvalidOperationException($"ffmpeg 提取音频失败 (exit {code}): {err.Trim()}");
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

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动 {file}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }
}
