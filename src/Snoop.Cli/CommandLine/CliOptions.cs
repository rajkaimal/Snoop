using System.Globalization;
using Snoop.Core.Diagnostics;
using Snoop.Proxy;

namespace Snoop.Cli.CommandLine;

public enum CliCommand
{
    Run,
    TrustRoot,
    UntrustRoot,
    ExportRoot,
    ResetRoot,
    RestoreProxy,
    Help,
}

/// <summary>Parsed command line.</summary>
public sealed record CliOptions
{
    public CliCommand Command { get; init; } = CliCommand.Run;

    public int Port { get; init; } = ProxyOptions.DefaultPort;

    public bool SetSystemProxy { get; init; }

    public bool Decrypt { get; init; } = true;

    public bool CaptureLoopback { get; init; } = true;

    public bool AllowRemote { get; init; }

    public bool ShowTunnels { get; init; }

    public IReadOnlyList<string> DoNotDecrypt { get; init; } = [];

    /// <summary>Apply Snoop's built-in list of hosts that should never be decrypted.</summary>
    public bool UseDefaultSkips { get; init; } = true;

    /// <summary>Stop decrypting a host automatically after its app rejects Snoop's certificate twice.</summary>
    public bool AutoSkip { get; init; } = true;

    /// <summary>Remove zstd from Accept-Encoding so bodies can be shown.</summary>
    public bool AvoidZstd { get; init; } = true;

    public LogLevel LogLevel { get; init; } = LogLevel.Info;

    /// <summary>For export-root: file to write.</summary>
    public string? OutputPath { get; init; }

    public const string Usage = """
        Snoop - HTTP/HTTPS monitoring proxy (headless)

        Usage:
          snoop [run] [options]       Start the proxy and print each request
          snoop trust-root            Add the Snoop root certificate to your trusted roots
          snoop untrust-root          Remove the Snoop root certificate from your trusted roots
          snoop export-root <file>    Save the root certificate (.cer, or .pem for PEM text)
          snoop reset-root            Delete the root certificate; a new one is made next start
          snoop restore-proxy         Put back the system proxy setting saved by Snoop
          snoop help                  Show this help

        Options for run:
          -p, --port <n>              Port to listen on (default 8866)
          -s, --system-proxy          Make Snoop the Windows system proxy while running
              --no-decrypt            Relay HTTPS without decrypting it
              --no-loopback           Do not capture traffic to localhost via the system proxy
              --skip <host>           Never decrypt this host; repeatable; *.example.com allowed
              --no-default-skips      Also decrypt hosts on the built-in skip list (Claude, Google Drive, Windows Update)
              --no-auto-skip          Keep decrypting hosts whose apps reject the Snoop certificate
              --keep-zstd             Leave zstd in Accept-Encoding (Snoop cannot show zstd bodies)
              --allow-remote          Listen on all interfaces (for phones and other PCs)
              --show-tunnels          Also print CONNECT tunnel rows
          -v, --verbose               Print debug messages
          -q, --quiet                 Print warnings and errors only
        """;

    /// <exception cref="CliUsageException">Unknown command or option, or a bad value.</exception>
    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new CliOptions();
        var skip = new List<string>();
        var index = 0;

        if (args.Count > 0 && !args[0].StartsWith('-'))
        {
            options = options with { Command = ParseCommand(args[0]) };
            index = 1;
        }

        for (; index < args.Count; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "-p" or "--port":
                    options = options with { Port = ParsePort(NextValue(args, ref index, arg)) };
                    break;
                case "-s" or "--system-proxy":
                    options = options with { SetSystemProxy = true };
                    break;
                case "--no-decrypt":
                    options = options with { Decrypt = false };
                    break;
                case "--no-loopback":
                    options = options with { CaptureLoopback = false };
                    break;
                case "--skip":
                    skip.Add(NextValue(args, ref index, arg));
                    break;
                case "--no-default-skips":
                    options = options with { UseDefaultSkips = false };
                    break;
                case "--no-auto-skip":
                    options = options with { AutoSkip = false };
                    break;
                case "--keep-zstd":
                    options = options with { AvoidZstd = false };
                    break;
                case "--allow-remote":
                    options = options with { AllowRemote = true };
                    break;
                case "--show-tunnels":
                    options = options with { ShowTunnels = true };
                    break;
                case "-v" or "--verbose":
                    options = options with { LogLevel = LogLevel.Debug };
                    break;
                case "-q" or "--quiet":
                    options = options with { LogLevel = LogLevel.Warning };
                    break;
                case "-h" or "--help" or "/?":
                    options = options with { Command = CliCommand.Help };
                    break;
                default:
                    if (options.Command == CliCommand.ExportRoot && options.OutputPath is null && !arg.StartsWith('-'))
                    {
                        options = options with { OutputPath = arg };
                        break;
                    }

                    throw new CliUsageException($"Unknown option '{arg}'.");
            }
        }

        if (options.Command == CliCommand.ExportRoot && string.IsNullOrWhiteSpace(options.OutputPath))
        {
            throw new CliUsageException("export-root needs a file name, e.g. snoop export-root snoop-root.cer");
        }

        return options with { DoNotDecrypt = skip };
    }

    private static CliCommand ParseCommand(string text) =>
        text.ToLowerInvariant() switch
        {
            "run" => CliCommand.Run,
            "trust-root" => CliCommand.TrustRoot,
            "untrust-root" => CliCommand.UntrustRoot,
            "export-root" => CliCommand.ExportRoot,
            "reset-root" => CliCommand.ResetRoot,
            "restore-proxy" => CliCommand.RestoreProxy,
            "help" => CliCommand.Help,
            _ => throw new CliUsageException($"Unknown command '{text}'."),
        };

    private static string NextValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count)
        {
            throw new CliUsageException($"Option '{option}' needs a value.");
        }

        index++;
        return args[index];
    }

    private static int ParsePort(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 0 and <= 65535
            ? port
            : throw new CliUsageException($"'{text}' is not a valid port.");
}

/// <summary>The command line could not be understood.</summary>
public sealed class CliUsageException : Exception
{
    public CliUsageException()
    {
    }

    public CliUsageException(string message)
        : base(message)
    {
    }

    public CliUsageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
