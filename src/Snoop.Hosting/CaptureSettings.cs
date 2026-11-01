using Snoop.Proxy;

namespace Snoop.Hosting;

/// <summary>How a capture should run. Shared by the command line and the app.</summary>
public sealed record CaptureSettings
{
    public int Port { get; init; } = ProxyOptions.DefaultPort;

    /// <summary>Decrypt HTTPS using the Snoop root certificate.</summary>
    public bool DecryptHttps { get; init; } = true;

    /// <summary>Make Snoop the Windows system proxy while capturing.</summary>
    public bool SetSystemProxy { get; init; }

    /// <summary>When Snoop is the system proxy, also route localhost traffic through it.</summary>
    public bool CaptureLoopback { get; init; } = true;

    /// <summary>Listen on all interfaces so phones and other PCs can use the proxy.</summary>
    public bool AllowRemote { get; init; }

    /// <summary>Extra hosts never decrypted.</summary>
    public IReadOnlyList<string> DoNotDecryptHosts { get; init; } = [];

    /// <summary>Apply the built-in list of hosts that should never be decrypted.</summary>
    public bool UseBuiltInSkips { get; init; } = true;

    /// <summary>Stop decrypting a host after its app rejects the Snoop certificate twice in a row.</summary>
    public bool AutoSkip { get; init; } = true;

    /// <summary>Ask servers not to use zstd (which Snoop cannot decode) by removing it from Accept-Encoding.</summary>
    public bool AvoidZstd { get; init; } = true;
}
