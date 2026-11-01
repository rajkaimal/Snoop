namespace Snoop.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>Minimal logging abstraction so the engine does not depend on a logging library.</summary>
public interface ISnoopLogger
{
    bool IsEnabled(LogLevel level);

    void Log(LogLevel level, string message, Exception? exception = null);
}

public static class SnoopLoggerExtensions
{
    public static void Debug(this ISnoopLogger logger, string message) => Write(logger, LogLevel.Debug, message, null);

    public static void Info(this ISnoopLogger logger, string message) => Write(logger, LogLevel.Info, message, null);

    public static void Warn(this ISnoopLogger logger, string message, Exception? exception = null) =>
        Write(logger, LogLevel.Warning, message, exception);

    public static void Error(this ISnoopLogger logger, string message, Exception? exception = null) =>
        Write(logger, LogLevel.Error, message, exception);

    private static void Write(ISnoopLogger logger, LogLevel level, string message, Exception? exception)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (logger.IsEnabled(level))
        {
            logger.Log(level, message, exception);
        }
    }
}

/// <summary>Discards everything.</summary>
public sealed class NullLogger : ISnoopLogger
{
    public static NullLogger Instance { get; } = new();

    private NullLogger()
    {
    }

    public bool IsEnabled(LogLevel level) => false;

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
    }
}
