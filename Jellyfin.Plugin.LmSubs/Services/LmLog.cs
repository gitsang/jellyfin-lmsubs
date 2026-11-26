using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LmSubs.Services;

// Plugin-level log filter. Empty config = follow the server's Serilog level.
// When the plugin level allows Debug but the server logger filters it out,
// emit at Information with a [Debug] tag so it still reaches the log file.
public class LmLog
{
    private readonly ILogger _logger;

    public LmLog(ILogger logger)
    {
        _logger = logger;
    }

    private static LogLevel? ConfiguredLevel =>
        Enum.TryParse<LogLevel>(Plugin.Instance?.Configuration.LogLevel, ignoreCase: true, out var l) ? l : null;

    private bool Allows(LogLevel level) => ConfiguredLevel is not { } configured || level >= configured;

    public void Debug(string message, params object?[] args)
    {
        if (!Allows(LogLevel.Debug))
        {
            return;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(message, args);
        }
        else
        {
            _logger.LogInformation("[Debug] " + message, args);
        }
    }

    public void Info(string message, params object?[] args)
    {
        if (Allows(LogLevel.Information))
        {
            _logger.LogInformation(message, args);
        }
    }

    public void Warn(string message, params object?[] args)
    {
        if (Allows(LogLevel.Warning))
        {
            _logger.LogWarning(message, args);
        }
    }

    public void Error(Exception? ex, string message, params object?[] args)
    {
        if (Allows(LogLevel.Error))
        {
            _logger.LogError(ex, message, args);
        }
    }
}