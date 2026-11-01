using System.Security.Cryptography.X509Certificates;
using Snoop.Core.Diagnostics;
using Snoop.Platform.Certificates;
using Snoop.Platform.SystemProxy;
using Snoop.Proxy.Processes;
using Snoop.Proxy.Tls;

namespace Snoop.Hosting;

/// <summary>Adds the Snoop root to (or removes it from) the trusted roots.</summary>
public interface IRootCertificateTrust
{
    bool IsSupported { get; }

    bool IsTrusted(X509Certificate2 root);

    void Trust(X509Certificate2 root);

    int RemoveAll();
}

/// <summary>The platform pieces a <see cref="CaptureHost"/> needs. Swappable for tests.</summary>
public sealed record CaptureHostServices(
    Func<CertificateAuthorityStore> CertificateStore,
    SystemProxyController? SystemProxy,
    IClientProcessResolver ProcessResolver,
    IRootCertificateTrust RootTrust)
{
    /// <summary>The real services for the current operating system.</summary>
    public static CaptureHostServices ForCurrentPlatform(ISnoopLogger logger) =>
        new(
            () => PlatformServices.CreateCertificateStore(logger),
            PlatformServices.CreateSystemProxyController(logger),
            PlatformServices.CreateProcessResolver(),
            OperatingSystem.IsWindows() ? new WindowsRootCertificateTrust() : new UnsupportedRootCertificateTrust());
}

/// <summary>The current user's Trusted Root store on Windows.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsRootCertificateTrust : IRootCertificateTrust
{
    public bool IsSupported => true;

    public bool IsTrusted(X509Certificate2 root) => RootCertificateTrust.IsTrusted(root);

    public void Trust(X509Certificate2 root) => RootCertificateTrust.Trust(root);

    public int RemoveAll() => RootCertificateTrust.RemoveAll();
}

/// <summary>Used where trusting a root is not automated (non-Windows development machines).</summary>
public sealed class UnsupportedRootCertificateTrust : IRootCertificateTrust
{
    public bool IsSupported => false;

    public bool IsTrusted(X509Certificate2 root) => false;

    public void Trust(X509Certificate2 root) =>
        throw new PlatformNotSupportedException("Trusting the root certificate is only automated on Windows.");

    public int RemoveAll() => 0;
}
