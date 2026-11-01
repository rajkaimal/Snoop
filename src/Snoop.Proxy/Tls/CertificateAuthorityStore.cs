using System.Security.Cryptography.X509Certificates;

namespace Snoop.Proxy.Tls;

/// <summary>Protects secret bytes at rest (DPAPI on Windows).</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] data);

    byte[] Unprotect(byte[] data);
}

/// <summary>Stores data as-is. For tests and non-Windows development only.</summary>
public sealed class UnprotectedSecretProtector : ISecretProtector
{
    public static UnprotectedSecretProtector Instance { get; } = new();

    public byte[] Protect(byte[] data) => (byte[])data.Clone();

    public byte[] Unprotect(byte[] data) => (byte[])data.Clone();
}

/// <summary>
/// Loads and saves the root certificate authority. The PKCS#12 file (certificate and
/// private key) is encrypted with an <see cref="ISecretProtector"/>; a public copy of the
/// certificate (<c>snoop-root.cer</c>) sits beside it for importing into other devices.
/// </summary>
public sealed class CertificateAuthorityStore(string directory, ISecretProtector protector)
{
    public const string ProtectedFileName = "snoop-root.pfx.protected";
    public const string PublicFileName = "snoop-root.cer";

    public string Directory { get; } = directory ?? throw new ArgumentNullException(nameof(directory));

    public string ProtectedFilePath => Path.Combine(Directory, ProtectedFileName);

    public string PublicFilePath => Path.Combine(Directory, PublicFileName);

    public bool Exists => File.Exists(ProtectedFilePath);

    /// <summary>Loads the saved root, or creates and saves a new one on first run.</summary>
    public X509Certificate2 LoadOrCreate(TimeProvider? clock = null)
    {
        var existing = TryLoad();
        if (existing is not null)
        {
            return existing;
        }

        var root = CertificateAuthority.CreateRootCertificate(clock);
        Save(root);
        return root;
    }

    /// <summary>Loads the saved root, or returns null when none is saved.</summary>
    /// <exception cref="InvalidDataException">The file exists but cannot be decrypted or read.</exception>
    public X509Certificate2? TryLoad()
    {
        if (!Exists)
        {
            return null;
        }

        try
        {
            var pfx = protector.Unprotect(File.ReadAllBytes(ProtectedFilePath));
            var flags = X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable;
            var certificate = X509CertificateLoader.LoadPkcs12(pfx, password: null, flags);
            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw new InvalidDataException("The saved root certificate has no private key.");
            }

            return certificate;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            throw new InvalidDataException(
                $"The saved root certificate at '{ProtectedFilePath}' could not be read. Delete it to create a new one.", ex);
        }
    }

    public void Save(X509Certificate2 root)
    {
        ArgumentNullException.ThrowIfNull(root);
        System.IO.Directory.CreateDirectory(Directory);
        var pfx = root.Export(X509ContentType.Pkcs12);
        AtomicFile.WriteAllBytes(ProtectedFilePath, protector.Protect(pfx));
        AtomicFile.WriteAllBytes(PublicFilePath, root.Export(X509ContentType.Cert));
    }

    /// <summary>Deletes the saved root. A new one is created on next start.</summary>
    public void Delete()
    {
        File.Delete(ProtectedFilePath);
        File.Delete(PublicFilePath);
    }
}

/// <summary>Writes files through a temp file and rename, so a crash never leaves half a file.</summary>
internal static class AtomicFile
{
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}
