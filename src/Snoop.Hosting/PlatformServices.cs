using Snoop.Core;
using Snoop.Core.Diagnostics;
using Snoop.Platform.Processes;
using Snoop.Platform.Security;
using Snoop.Platform.SystemProxy;
using Snoop.Proxy.Processes;
using Snoop.Proxy.Tls;

namespace Snoop.Hosting;

/// <summary>
/// Picks the right implementation of each platform service. On Windows everything is real;
/// elsewhere (development on Linux/macOS) the proxy still runs, without system proxy,
/// process names or DPAPI.
/// </summary>
public static class PlatformServices
{
    public static CertificateAuthorityStore CreateCertificateStore(ISnoopLogger logger)
    {
        if (OperatingSystem.IsWindows())
        {
            return new CertificateAuthorityStore(SnoopPaths.CertificateDirectory, new DpapiSecretProtector());
        }

        logger.Warn("Not running on Windows: the root certificate key is stored without DPAPI protection.");
        return new CertificateAuthorityStore(SnoopPaths.CertificateDirectory, UnprotectedSecretProtector.Instance);
    }

    public static IClientProcessResolver CreateProcessResolver() =>
        OperatingSystem.IsWindows() ? new WindowsProcessResolver() : NullClientProcessResolver.Instance;

    /// <summary>Null when the platform has no system proxy support.</summary>
    public static SystemProxyController? CreateSystemProxyController(ISnoopLogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var executable = Environment.ProcessPath ?? "snoop.exe";
        return new SystemProxyController(
            new WinInetProxySettingsApi(),
            SnoopPaths.ProxyBackupFile,
            new RunOnceRestoreRegistration(executable),
            logger);
    }
}
