using System;
using cAlgo.API;

namespace cAlgo.Robots.Utils;

/// <summary>
/// Log severity levels, ordered from most verbose to most critical.
/// </summary>
public enum LogLevel
{
    /// <summary>Detailed diagnostic information.</summary>
    Debug = 0,

    /// <summary>General informational messages.</summary>
    Info = 1,

    /// <summary>Trade-specific events (entries, exits, adjustments).</summary>
    Trade = 2,

    /// <summary>Potentially harmful situations.</summary>
    Warning = 3,

    /// <summary>Error events that might still allow the bot to continue.</summary>
    Error = 4
}

/// <summary>
/// Simple logger that wraps cTrader's <see cref="Robot.Print"/> with severity levels and formatting.
/// Format: [2026-03-19 14:30:00] [LEVEL] message
/// </summary>
public class Logger
{
    private readonly Robot _robot;
    private readonly LogLevel _minLevel;

    private static readonly string[] LevelLabels = { "DEBUG", "INFO", "TRADE", "WARN", "ERROR" };

    /// <summary>
    /// Creates a new Logger instance.
    /// </summary>
    /// <param name="robot">The cTrader Robot instance (provides Print method).</param>
    /// <param name="minLevel">Minimum log level to output. Messages below this level are suppressed.</param>
    public Logger(Robot robot, LogLevel minLevel = LogLevel.Info)
    {
        _robot = robot;
        _minLevel = minLevel;
    }

    /// <summary>Logs a debug-level message.</summary>
    public void Debug(string message) => Log(LogLevel.Debug, message);

    /// <summary>Logs an info-level message.</summary>
    public void Info(string message) => Log(LogLevel.Info, message);

    /// <summary>Logs a trade-specific event.</summary>
    public void Trade(string message) => Log(LogLevel.Trade, message);

    /// <summary>Logs a warning-level message.</summary>
    public void Warning(string message) => Log(LogLevel.Warning, message);

    /// <summary>Logs an error-level message.</summary>
    public void Error(string message) => Log(LogLevel.Error, message);

    private void Log(LogLevel level, string message)
    {
        if (level < _minLevel)
            return;

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        var label = LevelLabels[(int)level];
        _robot.Print($"[{timestamp}] [{label}] {message}");
    }
}
