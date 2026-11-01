namespace Snoop.Platform.SystemProxy;

/// <summary>WinINET connection flags (PROXY_TYPE_*).</summary>
[Flags]
public enum ProxyFlags
{
    None = 0,
    Direct = 0x1,
    Proxy = 0x2,
    AutoProxyUrl = 0x4,
    AutoDetect = 0x8,
}

/// <summary>The Windows "LAN settings" proxy configuration for the current user.</summary>
/// <param name="Flags">Which methods are enabled.</param>
/// <param name="ProxyServer">Manual proxy, e.g. "http=127.0.0.1:8866;https=127.0.0.1:8866".</param>
/// <param name="BypassList">Semicolon-separated hosts that skip the proxy, e.g. "&lt;local&gt;;*.corp".</param>
/// <param name="AutoConfigUrl">PAC script URL.</param>
public sealed record ProxySettings(ProxyFlags Flags, string? ProxyServer, string? BypassList, string? AutoConfigUrl)
{
    public static ProxySettings Direct { get; } = new(ProxyFlags.Direct, null, null, null);

    /// <summary>The bypass entry that makes WinINET send localhost traffic to the proxy too.</summary>
    public const string IncludeLoopbackToken = "<-loopback>";

    /// <summary>The bypass entry that skips the proxy for names without a dot (which includes "localhost").</summary>
    public const string LocalNamesToken = "<local>";

    /// <summary>
    /// The settings that route this user's traffic through a proxy at <paramref name="host"/>:<paramref name="port"/>.
    /// Auto-detect and PAC are turned off while active (WinINET would otherwise prefer them).
    /// The user's own bypass entries are kept.
    /// </summary>
    public static ProxySettings ForProxy(string host, int port, bool captureLoopback, ProxySettings previous)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentNullException.ThrowIfNull(previous);

        var address = $"{host}:{port}";
        var entries = SplitBypass(previous.BypassList)
            .Where(e => !string.Equals(e, IncludeLoopbackToken, StringComparison.OrdinalIgnoreCase))
            .Where(e => !captureLoopback || !string.Equals(e, LocalNamesToken, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (captureLoopback)
        {
            entries.Add(IncludeLoopbackToken);
        }

        return new ProxySettings(
            ProxyFlags.Direct | ProxyFlags.Proxy,
            $"http={address};https={address}",
            entries.Count == 0 ? null : string.Join(';', entries),
            null);
    }

    /// <summary>True when these settings point at a proxy at <paramref name="host"/>:<paramref name="port"/>.</summary>
    public bool PointsAt(string host, int port) =>
        Flags.HasFlag(ProxyFlags.Proxy)
        && ProxyServer is not null
        && ProxyServer.Contains($"{host}:{port}", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> SplitBypass(string? list) =>
        (list ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
