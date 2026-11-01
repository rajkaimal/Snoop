using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Snoop.Proxy.Tls;

namespace Snoop.Platform.Certificates;

/// <summary>
/// Adds or removes the Snoop root certificate in the current user's Trusted Root store.
/// Windows shows its own confirmation dialog when a root is added.
/// Firefox keeps its own store and is not covered.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RootCertificateTrust
{
    public static bool IsTrusted(X509Certificate2 root)
    {
        ArgumentNullException.ThrowIfNull(root);
        using var store = Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, root.Thumbprint, validOnly: false).Count > 0;
    }

    /// <summary>Adds the public part of <paramref name="root"/> to the store. Throws if the user declines.</summary>
    public static void Trust(X509Certificate2 root)
    {
        ArgumentNullException.ThrowIfNull(root);
        using var publicOnly = X509CertificateLoader.LoadCertificate(root.RawData);
        using var store = Open(OpenFlags.ReadWrite);
        store.Add(publicOnly);
    }

    /// <summary>Removes every Snoop root (current and older ones). Returns how many were removed.</summary>
    public static int RemoveAll()
    {
        using var store = Open(OpenFlags.ReadWrite);
        var matches = store.Certificates
            .Where(c => string.Equals(c.Subject, CertificateAuthority.RootSubjectName, StringComparison.Ordinal)
                        || c.Subject.StartsWith("CN=Snoop Root CA", StringComparison.Ordinal))
            .ToList();
        foreach (var certificate in matches)
        {
            store.Remove(certificate);
            certificate.Dispose();
        }

        return matches.Count;
    }

    private static X509Store Open(OpenFlags flags)
    {
        var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(flags);
        return store;
    }
}
