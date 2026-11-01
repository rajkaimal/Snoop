using Snoop.Core.Diagnostics;

namespace Snoop.Cli.Output;

/// <summary>Writes log messages to the console (warnings and errors to stderr).</summary>
internal sealed class ConsoleLogger(LogLevel minimum, ConsoleWriter writer) : ISnoopLogger
{
    public bool IsEnabled(LogLevel level) => level >= minimum;

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        var (tag, color) = level switch
        {
            LogLevel.Debug => ("debug", ConsoleColor.DarkGray),
            LogLevel.Info => ("info ", ConsoleColor.Gray),
            LogLevel.Warning => ("warn ", ConsoleColor.Yellow),
            _ => ("error", ConsoleColor.Red),
        };

        var text = exception is null || level == LogLevel.Debug ? message : $"{message} {exception.Message}";
        if (exception is not null && minimum == LogLevel.Debug)
        {
            text += Environment.NewLine + exception;
        }

        writer.WriteLine($"[{tag}] {text}", color, toError: level >= LogLevel.Warning);
    }
}

/// <summary>Serialises coloured console output from many threads.</summary>
internal sealed class ConsoleWriter
{
    private readonly Lock _gate = new();

    public void WriteLine(string text, ConsoleColor? color = null, bool toError = false)
    {
        lock (_gate)
        {
            var writer = toError ? Console.Error : Console.Out;
            if (color is { } c && !Console.IsOutputRedirected)
            {
                var previous = Console.ForegroundColor;
                Console.ForegroundColor = c;
                writer.WriteLine(text);
                Console.ForegroundColor = previous;
            }
            else
            {
                writer.WriteLine(text);
            }
        }
    }
}
