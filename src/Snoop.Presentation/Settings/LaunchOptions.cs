namespace Snoop.Presentation.Settings;

/// <summary>
/// The desktop app's command line:
/// <list type="bullet">
/// <item><c>--test</c>: for UI tests. Never capture or touch the system proxy on start, and keep all
/// data (settings, rules, saved requests, certificates) in a fresh temporary folder unless
/// <c>SNOOP_DATA_DIR</c> already points somewhere.</item>
/// <item><c>--open &lt;file&gt;</c> (or just a file path): open a snapshot or HAR file on start.</item>
/// <item><c>restore-proxy</c>: used by the logon safety net; put the proxy back and quit.</item>
/// </list>
/// </summary>
public sealed record LaunchOptions
{
    public const string RestoreProxyArgument = "restore-proxy";

    public bool TestMode { get; init; }

    public bool RestoreProxy { get; init; }

    public string? OpenPath { get; init; }

    /// <summary>Reads the arguments after the program name. Unknown switches are ignored.</summary>
    public static LaunchOptions Parse(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new LaunchOptions();
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var arg = list[i];
            if (string.Equals(arg, RestoreProxyArgument, StringComparison.OrdinalIgnoreCase))
            {
                options = options with { RestoreProxy = true };
            }
            else if (string.Equals(arg, "--test", StringComparison.OrdinalIgnoreCase))
            {
                options = options with { TestMode = true };
            }
            else if (string.Equals(arg, "--open", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < list.Count)
                {
                    options = options with { OpenPath = list[++i] };
                }
            }
            else if (!arg.StartsWith('-') && options.OpenPath is null && arg.Length > 0)
            {
                options = options with { OpenPath = arg }; // e.g. a .snoop file opened from Explorer
            }
        }

        return options;
    }

    /// <summary>
    /// Settings to run with. In test mode capture never starts, the system proxy is left alone and HTTPS
    /// decryption is off (so no root certificate is needed and the "trust the certificate" banner stays away).
    /// </summary>
    public AppSettings Adjust(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return TestMode ? settings with { CaptureOnStartup = false, UseSystemProxy = false, DecryptHttps = false } : settings;
    }
}
