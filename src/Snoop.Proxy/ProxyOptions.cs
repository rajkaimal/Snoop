using System.Net;
using System.Net.Security;
using Snoop.Core.Bodies;

namespace Snoop.Proxy;

/// <summary>Settings for one run of the proxy engine.</summary>
public sealed record ProxyOptions
{
    public const int DefaultPort = 8866;

    /// <summary>Address to listen on. Loopback unless remote devices are allowed.</summary>
    public IPAddress ListenAddress { get; init; } = IPAddress.Loopback;

    /// <summary>Preferred port. 0 picks any free port.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>If the preferred port is taken, try this many following ports.</summary>
    public int PortFallbackAttempts { get; init; } = 10;

    /// <summary>Decrypt HTTPS inside CONNECT tunnels. When false, tunnels are relayed blind.</summary>
    public bool DecryptHttps { get; init; } = true;

    /// <summary>Hosts never decrypted, e.g. "*.windowsupdate.com". See <see cref="Util.HostMatcher"/>.</summary>
    public IReadOnlyList<string> DoNotDecryptHosts { get; init; } = [];

    /// <summary>Also skip the hosts in <see cref="BuiltInDoNotDecryptHosts"/>.</summary>
    public bool UseBuiltInDoNotDecryptList { get; init; } = true;

    /// <summary>
    /// Stop decrypting a host after its client rejects Snoop's certificate this many times in a row
    /// (see <see cref="Tls.CertificateRejectionTracker"/>). 0 turns this off.
    /// </summary>
    public int AutoSkipAfterRejections { get; init; } = 2;

    /// <summary>
    /// Hosts whose apps are known not to accept a locally trusted root (certificate pinning or a
    /// private trust store). Decrypting them only breaks the app.
    /// </summary>
    public static IReadOnlyList<string> BuiltInDoNotDecryptHosts { get; } =
    [
        // Claude desktop app
        "claude.ai",
        "*.claude.ai",
        "*.anthropic.com",

        // Google Drive for desktop
        "drivefrontend-pa.googleapis.com",
        "play.googleapis.com",

        // Windows Update and Store downloads
        "*.windowsupdate.com",
        "*.update.microsoft.com",
        "*.delivery.mp.microsoft.com",
    ];

    /// <summary>
    /// Validates server certificates for upstream HTTPS. Null uses normal system validation.
    /// Tests use this to trust a local test server.
    /// </summary>
    public RemoteCertificateValidationCallback? UpstreamCertificateValidation { get; init; }

    /// <summary>Send upstream traffic through another proxy (e.g. a corporate proxy). Null connects directly.</summary>
    public Uri? UpstreamProxy { get; init; }

    /// <summary>
    /// Remove "zstd" from requests' Accept-Encoding, so servers answer with gzip or brotli, which Snoop can
    /// show. (.NET has no zstd decoder.)
    /// </summary>
    public bool RemoveZstdFromAcceptEncoding { get; init; }

    public BodyCaptureOptions BodyCapture { get; init; } = BodyCaptureOptions.Default;

    /// <summary>Largest accepted request or response head.</summary>
    public int MaxHeadBytes { get; init; } = 64 * 1024;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>A client connection with no new request for this long is closed.</summary>
    public TimeSpan ClientIdleTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public TimeSpan TlsHandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long <see cref="ProxyServer.StopAsync"/> waits for open connections to close.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(ListenAddress);
        ArgumentOutOfRangeException.ThrowIfNegative(Port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Port, IPEndPoint.MaxPort);
        ArgumentOutOfRangeException.ThrowIfNegative(PortFallbackAttempts);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxHeadBytes, 1024);
        ArgumentNullException.ThrowIfNull(BodyCapture);
        ArgumentNullException.ThrowIfNull(DoNotDecryptHosts);
        ArgumentOutOfRangeException.ThrowIfNegative(AutoSkipAfterRejections);
    }
}
