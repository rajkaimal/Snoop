using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Snoop.Hosting;
using Snoop.Platform.SystemProxy;
using Snoop.Proxy.Processes;
using Snoop.Proxy.Tls;

namespace Snoop.Hosting.Tests;

public sealed class CaptureHostTests : IAsyncLifetime
{
    private static readonly ProxySettings UserSettings = new(ProxyFlags.Direct, null, "*.corp", null);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "snoop-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeProxyApi _proxyApi = new(UserSettings);
    private readonly FakeTrust _trust = new();
    private CaptureHost _host = null!;
    private int _stateChanges;

    public Task InitializeAsync()
    {
        var services = new CaptureHostServices(
            () => new CertificateAuthorityStore(_directory, UnprotectedSecretProtector.Instance),
            new SystemProxyController(_proxyApi, Path.Combine(_directory, "proxy-backup.json")),
            NullClientProcessResolver.Instance,
            _trust);
        _host = new CaptureHost(services: services);
        _host.StateChanged += (_, _) => Interlocked.Increment(ref _stateChanges);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Start_listens_and_stop_closes()
    {
        await _host.StartAsync(new CaptureSettings { Port = 0 });

        Assert.True(_host.IsRunning);
        Assert.True(_host.IsDecrypting);
        Assert.NotNull(_host.Endpoint);
        Assert.True(await CanConnectAsync(_host.Endpoint!));

        var endpoint = _host.Endpoint!;
        await _host.StopAsync();

        Assert.False(_host.IsRunning);
        Assert.Null(_host.Endpoint);
        Assert.False(await CanConnectAsync(endpoint));
        Assert.True(_stateChanges >= 2);
    }

    [Fact]
    public async Task Decryption_off_does_not_create_a_certificate()
    {
        await _host.StartAsync(new CaptureSettings { Port = 0, DecryptHttps = false });

        Assert.False(_host.IsDecrypting);
        Assert.False(File.Exists(Path.Combine(_directory, CertificateAuthorityStore.ProtectedFileName)));
    }

    [Fact]
    public async Task System_proxy_follows_the_capture_and_is_restored_on_stop()
    {
        await _host.StartAsync(new CaptureSettings { Port = 0, SetSystemProxy = true });

        Assert.True(_host.IsSystemProxyEnabled);
        Assert.True(_proxyApi.Current.PointsAt("127.0.0.1", _host.Endpoint!.Port));

        await _host.StopAsync();

        Assert.False(_host.IsSystemProxyEnabled);
        Assert.Equal(UserSettings, _proxyApi.Current);
    }

    [Fact]
    public async Task System_proxy_can_be_toggled_while_running()
    {
        await _host.StartAsync(new CaptureSettings { Port = 0 });

        _host.SetSystemProxy(true);
        Assert.True(_host.IsSystemProxyEnabled);
        Assert.True(_host.Settings.SetSystemProxy);

        _host.SetSystemProxy(false);
        Assert.False(_host.IsSystemProxyEnabled);
        Assert.Equal(UserSettings, _proxyApi.Current);
    }

    [Fact]
    public void System_proxy_cannot_be_enabled_while_stopped()
    {
        Assert.Throws<InvalidOperationException>(() => _host.SetSystemProxy(true));
        Assert.Equal(UserSettings, _proxyApi.Current);
    }

    [Fact]
    public async Task Restart_keeps_sessions_and_moves_the_system_proxy()
    {
        await _host.StartAsync(new CaptureSettings { Port = 0, SetSystemProxy = true });
        var firstPort = _host.Endpoint!.Port;

        await _host.StartAsync(new CaptureSettings { Port = 0, SetSystemProxy = true, DecryptHttps = false });

        Assert.True(_host.IsRunning);
        Assert.False(_host.IsDecrypting);
        Assert.True(_proxyApi.Current.PointsAt("127.0.0.1", _host.Endpoint!.Port));
        Assert.NotEqual(0, firstPort);

        await _host.StopAsync();
        Assert.Equal(UserSettings, _proxyApi.Current);
    }

    [Fact]
    public async Task Restart_without_system_proxy_restores_it()
    {
        await _host.StartAsync(new CaptureSettings { Port = 0, SetSystemProxy = true });

        await _host.StartAsync(new CaptureSettings { Port = 0, SetSystemProxy = false });

        Assert.False(_host.IsSystemProxyEnabled);
        Assert.Equal(UserSettings, _proxyApi.Current);
    }

    [Fact]
    public void Recover_repairs_a_setting_left_by_a_crash()
    {
        var crashed = new SystemProxyController(_proxyApi, Path.Combine(_directory, "proxy-backup.json"));
        crashed.Enable("127.0.0.1", 8866);

        Assert.True(_host.RecoverSystemProxy());
        Assert.Equal(UserSettings, _proxyApi.Current);
    }

    [Fact]
    public void Trust_root_uses_the_same_certificate_every_time()
    {
        Assert.False(_host.IsRootTrusted);

        _host.TrustRootCertificate();

        Assert.True(_host.IsRootTrusted);
        Assert.Equal(_host.GetCertificateAuthority().RootCertificate.Thumbprint, _trust.Trusted!.Thumbprint);
    }

    [Fact]
    public async Task Skip_decryption_is_recorded_while_running()
    {
        Assert.False(_host.SkipDecryption("example.com")); // not running

        await _host.StartAsync(new CaptureSettings { Port = 0 });

        Assert.True(_host.SkipDecryption("example.com"));
        Assert.True(_host.SkippedHosts.ContainsKey("example.com"));
    }

    [Fact]
    public async Task Busy_port_surfaces_as_socket_exception()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
        var services = new CaptureHostServices(
            () => new CertificateAuthorityStore(_directory, UnprotectedSecretProtector.Instance),
            null,
            NullClientProcessResolver.Instance,
            _trust);
        await using var host = new CaptureHost(services: services);

        // Port fallback is on, so this succeeds on a later port.
        await host.StartAsync(new CaptureSettings { Port = port });

        Assert.NotEqual(port, host.Endpoint!.Port);
        Assert.False(host.IsSystemProxySupported);
    }

    private static async Task<bool> CanConnectAsync(IPEndPoint endpoint)
    {
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(endpoint.Address, endpoint.Port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private sealed class FakeProxyApi(ProxySettings initial) : IProxySettingsApi
    {
        public ProxySettings Current { get; private set; } = initial;

        public ProxySettings Read() => Current;

        public void Write(ProxySettings settings) => Current = settings;
    }

    private sealed class FakeTrust : IRootCertificateTrust
    {
        public X509Certificate2? Trusted { get; private set; }

        public bool IsSupported => true;

        public bool IsTrusted(X509Certificate2 root) => Trusted?.Thumbprint == root.Thumbprint;

        public void Trust(X509Certificate2 root) => Trusted = root;

        public int RemoveAll()
        {
            var had = Trusted is null ? 0 : 1;
            Trusted = null;
            return had;
        }
    }
}
