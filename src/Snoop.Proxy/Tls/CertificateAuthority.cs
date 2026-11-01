using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Snoop.Proxy.Util;

namespace Snoop.Proxy.Tls;

/// <summary>
/// The local root certificate authority that lets Snoop decrypt HTTPS. Mints a leaf
/// certificate for each host on demand, signed by the root, and caches it.
/// The root is unique to this machine and is never shipped.
/// </summary>
public sealed class CertificateAuthority : IDisposable
{
    public const string RootSubjectName = "CN=Snoop Root CA, O=Snoop (local only), OU=Created by Snoop on this machine";

    private static readonly TimeSpan RootLifetime = TimeSpan.FromDays(3650);
    private static readonly TimeSpan LeafLifetime = TimeSpan.FromDays(365);
    private static readonly TimeSpan BackDate = TimeSpan.FromDays(1);

    private readonly LruCache<string, X509Certificate2> _leafCache;
    private readonly TimeProvider _clock;
    private readonly AsymmetricAlgorithm _rootKey;
    private readonly X509SignatureGenerator _signer;
    private readonly Lock _signGate = new(); // Key objects are not guaranteed thread-safe.

    public CertificateAuthority(X509Certificate2 root, int leafCacheSize = 1000, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!root.HasPrivateKey)
        {
            throw new ArgumentException("The root certificate must include its private key.", nameof(root));
        }

        RootCertificate = root;
        _clock = clock ?? TimeProvider.System;
        if (root.GetRSAPrivateKey() is { } rsa)
        {
            _rootKey = rsa;
            _signer = X509SignatureGenerator.CreateForRSA(rsa, RSASignaturePadding.Pkcs1);
        }
        else if (root.GetECDsaPrivateKey() is { } ecdsa)
        {
            _rootKey = ecdsa;
            _signer = X509SignatureGenerator.CreateForECDsa(ecdsa);
        }
        else
        {
            throw new ArgumentException("The root certificate key must be RSA or ECDSA.", nameof(root));
        }

        _leafCache = new LruCache<string, X509Certificate2>(leafCacheSize, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The root certificate, including its private key.</summary>
    public X509Certificate2 RootCertificate { get; }

    /// <summary>Number of leaf certificates currently cached.</summary>
    public int CachedLeafCount => _leafCache.Count;

    /// <summary>Creates a brand-new root certificate authority (RSA 2048, valid 10 years).</summary>
    public static X509Certificate2 CreateRootCertificate(TimeProvider? clock = null)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(RootSubjectName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(now - BackDate, now + RootLifetime);
    }

    /// <summary>
    /// Returns a certificate for <paramref name="host"/> (a DNS name or IP address),
    /// minting and caching one if needed.
    /// </summary>
    public X509Certificate2 GetLeafCertificate(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var key = NormalizeHost(host);
        return _leafCache.GetOrAdd(key, CreateLeafCertificate);
    }

    /// <summary>Exports the root's public certificate (no private key) as DER bytes.</summary>
    public byte[] ExportRootPublicCertificate() => RootCertificate.Export(X509ContentType.Cert);

    /// <summary>Exports the root's public certificate in PEM text form.</summary>
    public string ExportRootPublicCertificatePem() => RootCertificate.ExportCertificatePem();

    public void Dispose()
    {
        _leafCache.Clear();
        _rootKey.Dispose();
        RootCertificate.Dispose();
    }

    internal static string NormalizeHost(string host)
    {
        var trimmed = host.Trim().TrimEnd('.');
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed.ToLowerInvariant();
    }

    private X509Certificate2 CreateLeafCertificate(string host)
    {
        var now = _clock.GetUtcNow();
        var notBefore = now - BackDate;
        var notAfter = now + LeafLifetime;
        if (notAfter > RootCertificate.NotAfter)
        {
            notAfter = RootCertificate.NotAfter;
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var commonName = host.Length <= 64 ? host : host[..64];
        var request = new CertificateRequest($"CN={EscapeDistinguishedNameValue(commonName)}", key, HashAlgorithmName.SHA256);

        var san = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(host, out var address))
        {
            san.AddIpAddress(address);
        }
        else
        {
            san.AddDnsName(host);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication")], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(RootCertificate, true, false));

        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        serial[0] &= 0x7F; // Keep the serial number positive.

        X509Certificate2 publicOnly;
        lock (_signGate)
        {
            publicOnly = request.Create(RootCertificate.SubjectName, _signer, notBefore, notAfter, serial);
        }

        using var signed = publicOnly;
        using var withKey = publicOnly.CopyWithPrivateKey(key);
        return MakeUsableForTlsServer(withKey);
    }

    /// <summary>
    /// SChannel (Windows TLS) cannot use the ephemeral keys produced by
    /// <see cref="CertificateRequest"/>, so on Windows the certificate is round-tripped
    /// through PKCS#12 to give it a real key container.
    /// </summary>
    private static X509Certificate2 MakeUsableForTlsServer(X509Certificate2 certificate)
    {
        var pfx = certificate.Export(X509ContentType.Pkcs12);
        var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet;
        return X509CertificateLoader.LoadPkcs12(pfx, password: null, flags);
    }

    private static string EscapeDistinguishedNameValue(string value) =>
        value.IndexOfAny([',', '+', '"', '\\', '<', '>', ';', '=']) >= 0
            ? "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : value;
}
