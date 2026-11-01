using Snoop.Cli.Output;
using Snoop.Core.Diagnostics;
using Snoop.Hosting;
using Snoop.Platform.Certificates;
using Snoop.Proxy.Tls;

namespace Snoop.Cli.Commands;

/// <summary>One-shot commands: certificate trust, export, reset and proxy restore.</summary>
internal sealed class MaintenanceCommands(ISnoopLogger logger, ConsoleWriter writer)
{
    public int TrustRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return NotOnWindows();
        }

        using var root = PlatformServices.CreateCertificateStore(logger).LoadOrCreate();
        if (RootCertificateTrust.IsTrusted(root))
        {
            writer.WriteLine("The Snoop root certificate is already trusted.");
            return ExitCodes.Success;
        }

        writer.WriteLine("Windows will ask you to confirm installing the Snoop root certificate...");
        try
        {
            RootCertificateTrust.Trust(root);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            logger.Error("The certificate was not installed (the prompt may have been declined).", ex);
            return ExitCodes.Failed;
        }

        writer.WriteLine($"Trusted. Thumbprint {root.Thumbprint}.", ConsoleColor.Green);
        writer.WriteLine("Firefox uses its own certificate store and is not covered.");
        return ExitCodes.Success;
    }

    public int UntrustRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return NotOnWindows();
        }

        var removed = RootCertificateTrust.RemoveAll();
        writer.WriteLine(removed == 0 ? "No Snoop root certificate was trusted." : $"Removed {removed} Snoop root certificate(s).");
        return ExitCodes.Success;
    }

    public int ExportRoot(string path)
    {
        using var root = PlatformServices.CreateCertificateStore(logger).LoadOrCreate();
        using var authority = new CertificateAuthority(root);
        var full = Path.GetFullPath(path);
        if (full.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) || full.EndsWith(".crt", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(full, authority.ExportRootPublicCertificatePem());
        }
        else
        {
            File.WriteAllBytes(full, authority.ExportRootPublicCertificate());
        }

        writer.WriteLine($"Saved the root certificate (public part only) to {full}.");
        return ExitCodes.Success;
    }

    public int ResetRoot()
    {
        var store = PlatformServices.CreateCertificateStore(logger);
        if (OperatingSystem.IsWindows())
        {
            var removed = RootCertificateTrust.RemoveAll();
            if (removed > 0)
            {
                writer.WriteLine($"Removed {removed} trusted Snoop root certificate(s).");
            }
        }

        if (store.Exists)
        {
            store.Delete();
            writer.WriteLine("Deleted the root certificate. A new one is created next time Snoop starts.");
        }
        else
        {
            writer.WriteLine("There was no root certificate to delete.");
        }

        return ExitCodes.Success;
    }

    public int RestoreProxy()
    {
        var controller = PlatformServices.CreateSystemProxyController(logger);
        if (controller is null)
        {
            return NotOnWindows();
        }

        writer.WriteLine(controller.Disable() ? "System proxy setting restored." : "Nothing to restore: Snoop is not the system proxy.");
        return ExitCodes.Success;
    }

    private int NotOnWindows()
    {
        logger.Error("This command is only available on Windows.");
        return ExitCodes.Failed;
    }
}
