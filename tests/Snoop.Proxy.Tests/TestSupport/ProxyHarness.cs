using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Snoop.Proxy.Tls;

namespace Snoop.Proxy.Tests.TestSupport;

/// <summary>
/// A running Snoop proxy wired to a recorder, plus an <see cref="HttpClient"/> that sends
/// everything through it and trusts Snoop's root certificate.
/// </summary>
internal sealed class ProxyHarness : IAsyncDisposable
{
    private ProxyHarness(ProxyServer proxy, SessionRecorder recorder, CertificateAuthority authority)
    {
        Proxy = proxy;
        Recorder = recorder;
        Authority = authority;
        ProxyUri = new Uri($"http://{proxy.Endpoint}");
    }

    public ProxyServer Proxy { get; }

    public SessionRecorder Recorder { get; }

    public CertificateAuthority Authority { get; }

    public Uri ProxyUri { get; }

    /// <summary>Starts a proxy. Upstream HTTPS is trusted when it chains to <paramref name="upstreamRoot"/>.</summary>
    public static ProxyHarness Start(X509Certificate2? upstreamRoot = null, Func<ProxyOptions, ProxyOptions>? configure = null, Snoop.Proxy.Interception.ITrafficInterceptor? interceptor = null)
    {
        var authority = new CertificateAuthority(TestCertificates.NewRoot());
        var options = new ProxyOptions
        {
            Port = 0,
            UpstreamCertificateValidation = upstreamRoot is null ? null : TrustOnly(upstreamRoot),
            ClientIdleTimeout = TimeSpan.FromSeconds(30),
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        };
        options = configure?.Invoke(options) ?? options;

        var recorder = new SessionRecorder();
        var proxy = new ProxyServer(options, recorder, authority, interceptor: interceptor);
        proxy.Start();
        return new ProxyHarness(proxy, recorder, authority);
    }

    /// <summary>A client that uses the proxy for every URL (including localhost) and trusts <paramref name="serverRoot"/>.</summary>
    public HttpClient CreateClient(X509Certificate2? serverRoot = null, Version? version = null, System.Security.Authentication.SslProtocols tls = default)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new AlwaysProxy(ProxyUri),
            UseProxy = true,
            AllowAutoRedirect = false,
            SslOptions =
            {
                RemoteCertificateValidationCallback = TrustOnly(serverRoot ?? Authority.RootCertificate),
                EnabledSslProtocols = tls,
            },
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
            DefaultRequestVersion = version ?? HttpVersion.Version11,
        };
    }

    public async ValueTask DisposeAsync()
    {
        await Proxy.DisposeAsync();
        Authority.Dispose();
    }

    /// <summary>Accepts only certificates that chain to <paramref name="root"/> and match the host name.</summary>
    public static RemoteCertificateValidationCallback TrustOnly(X509Certificate2 root) =>
        (_, certificate, _, errors) =>
        {
            if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            {
                return false;
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            var leaf = certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            return chain.Build(leaf);
        };

    /// <summary>
    /// .NET's WebProxy never proxies loopback addresses; tests need it to.
    /// </summary>
    private sealed class AlwaysProxy(Uri proxy) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => proxy;

        public bool IsBypassed(Uri host) => false;
    }
}

/// <summary>Certificates for the fake "internet" servers, issued by a separate test CA.</summary>
internal static class TestCertificates
{
    private static readonly Lazy<CertificateAuthority> ServerAuthority = new(() => new CertificateAuthority(NewRoot()));

    public static X509Certificate2 ServerRoot => ServerAuthority.Value.RootCertificate;

    public static X509Certificate2 ServerCertificate(string host = "localhost") => ServerAuthority.Value.GetLeafCertificate(host);

    public static X509Certificate2 NewRoot() => CertificateAuthority.CreateRootCertificate();
}
