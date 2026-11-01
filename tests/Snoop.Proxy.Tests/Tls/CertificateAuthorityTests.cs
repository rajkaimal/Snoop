using System.Net;
using System.Security.Cryptography.X509Certificates;
using Snoop.Proxy.Tls;

namespace Snoop.Proxy.Tests.Tls;

public sealed class CertificateAuthorityTests : IDisposable
{
    private static readonly X509Certificate2 SharedRoot = CertificateAuthority.CreateRootCertificate();
    private readonly CertificateAuthority _authority = new(X509CertificateLoader.LoadPkcs12(SharedRoot.Export(X509ContentType.Pkcs12), null));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "snoop-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _authority.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Root_is_a_certificate_authority()
    {
        var root = _authority.RootCertificate;

        Assert.True(root.HasPrivateKey);
        Assert.Equal(CertificateAuthority.RootSubjectName, root.Subject);
        var constraints = root.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.True(constraints.CertificateAuthority);
        Assert.True(root.NotAfter > DateTime.UtcNow.AddYears(9));
    }

    [Fact]
    public void Leaf_chains_to_the_root_and_names_the_host()
    {
        var leaf = _authority.GetLeafCertificate("www.example.com");

        Assert.True(leaf.HasPrivateKey);
        Assert.Equal("www.example.com", leaf.GetNameInfo(X509NameType.DnsName, forIssuer: false));
        Assert.True(BuildsToRoot(leaf));
        Assert.True(leaf.MatchesHostname("www.example.com"));
        Assert.False(leaf.MatchesHostname("other.example.com"));
        Assert.True(leaf.NotBefore < DateTime.Now);
        Assert.True(leaf.NotAfter <= _authority.RootCertificate.NotAfter);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    public void Leaf_for_an_ip_address_uses_an_ip_san(string host)
    {
        var leaf = _authority.GetLeafCertificate(host);
        var san = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();

        var expected = IPAddress.Parse(host.Trim('[', ']'));
        Assert.Contains(expected, san.EnumerateIPAddresses());
        Assert.True(BuildsToRoot(leaf));
    }

    [Fact]
    public void Leaves_are_cached_per_host_ignoring_case()
    {
        var first = _authority.GetLeafCertificate("Example.COM");
        var second = _authority.GetLeafCertificate("example.com.");
        var other = _authority.GetLeafCertificate("example.org");

        Assert.Same(first, second);
        Assert.NotSame(first, other);
        Assert.Equal(2, _authority.CachedLeafCount);
    }

    [Fact]
    public void Leaf_is_a_server_certificate_not_a_ca()
    {
        var leaf = _authority.GetLeafCertificate("api.example.com");

        Assert.False(leaf.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        var usage = leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Contains(usage.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.1");
    }

    [Fact]
    public async Task Concurrent_requests_for_one_host_are_safe()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => _authority.GetLeafCertificate("busy.example.com"))));

        Assert.All(results, cert => Assert.Equal(results[0].Thumbprint, cert.Thumbprint));
    }

    [Fact]
    public void Root_without_private_key_is_rejected()
    {
        using var publicOnly = X509CertificateLoader.LoadCertificate(SharedRoot.RawData);

        Assert.Throws<ArgumentException>(() => new CertificateAuthority(publicOnly));
    }

    [Fact]
    public void Exported_public_certificate_has_no_private_key()
    {
        using var exported = X509CertificateLoader.LoadCertificate(_authority.ExportRootPublicCertificate());

        Assert.False(exported.HasPrivateKey);
        Assert.Equal(_authority.RootCertificate.Thumbprint, exported.Thumbprint);
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", _authority.ExportRootPublicCertificatePem(), StringComparison.Ordinal);
    }

    [Fact]
    public void Store_creates_once_then_loads_the_same_root()
    {
        var store = new CertificateAuthorityStore(_directory, UnprotectedSecretProtector.Instance);
        Assert.False(store.Exists);

        using var created = store.LoadOrCreate();
        using var loaded = store.LoadOrCreate();

        Assert.True(store.Exists);
        Assert.True(File.Exists(store.PublicFilePath));
        Assert.Equal(created.Thumbprint, loaded.Thumbprint);
        Assert.True(loaded.HasPrivateKey);
    }

    [Fact]
    public void Store_uses_the_protector()
    {
        var protector = new XorProtector();
        var store = new CertificateAuthorityStore(_directory, protector);

        using var created = store.LoadOrCreate();
        using var loaded = store.TryLoad();

        Assert.True(protector.ProtectCalls > 0);
        Assert.True(protector.UnprotectCalls > 0);
        Assert.Equal(created.Thumbprint, loaded!.Thumbprint);
    }

    [Fact]
    public void Store_reports_a_corrupt_file()
    {
        var store = new CertificateAuthorityStore(_directory, UnprotectedSecretProtector.Instance);
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(store.ProtectedFilePath, [1, 2, 3]);

        Assert.Throws<InvalidDataException>(() => store.TryLoad());
    }

    [Fact]
    public void Store_delete_removes_files()
    {
        var store = new CertificateAuthorityStore(_directory, UnprotectedSecretProtector.Instance);
        using var _ = store.LoadOrCreate();

        store.Delete();

        Assert.False(store.Exists);
        Assert.False(File.Exists(store.PublicFilePath));
    }

    private bool BuildsToRoot(X509Certificate2 leaf)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_authority.RootCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(leaf);
    }

    private sealed class XorProtector : ISecretProtector
    {
        public int ProtectCalls { get; private set; }

        public int UnprotectCalls { get; private set; }

        public byte[] Protect(byte[] data)
        {
            ProtectCalls++;
            return data.Select(b => (byte)(b ^ 0x5A)).ToArray();
        }

        public byte[] Unprotect(byte[] data)
        {
            UnprotectCalls++;
            return data.Select(b => (byte)(b ^ 0x5A)).ToArray();
        }
    }
}
