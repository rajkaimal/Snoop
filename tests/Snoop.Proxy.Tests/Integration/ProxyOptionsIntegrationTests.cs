using System.Net.Sockets;
using Snoop.Core.Sessions;
using Snoop.Proxy.Tests.TestSupport;

namespace Snoop.Proxy.Tests.Integration;

/// <summary>Behaviour that depends on proxy options: blind tunnels, do-not-decrypt, port fallback.</summary>
public sealed class ProxyOptionsIntegrationTests : IAsyncLifetime
{
    private TestServer _https = null!;

    public async Task InitializeAsync() => _https = await TestServer.StartAsync(TestCertificates.ServerCertificate());

    public async Task DisposeAsync() => await _https.DisposeAsync();

    [Fact]
    public async Task With_decryption_off_https_is_relayed_blind()
    {
        await using var harness = ProxyHarness.Start(configure: o => o with { DecryptHttps = false });

        // The client sees the real server certificate, so it must trust the server's CA.
        var client = harness.CreateClient(serverRoot: TestCertificates.ServerRoot);
        var text = await client.GetStringAsync(_https.Url("/hello"));
        client.Dispose(); // Closes the pooled connection, which ends the tunnel.

        Assert.Equal(TestServer.HelloText, text);
        await AssertBlindTunnelAsync(harness);
    }

    [Fact]
    public async Task Do_not_decrypt_hosts_are_relayed_blind()
    {
        await using var harness = ProxyHarness.Start(configure: o => o with { DoNotDecryptHosts = ["localhost"] });

        var client = harness.CreateClient(serverRoot: TestCertificates.ServerRoot);
        var text = await client.GetStringAsync(_https.Url("/hello"));
        client.Dispose(); // Closes the pooled connection, which ends the tunnel.

        Assert.Equal(TestServer.HelloText, text);
        await AssertBlindTunnelAsync(harness);
    }

    [Fact]
    public async Task Host_whose_app_keeps_rejecting_the_certificate_is_skipped_automatically()
    {
        await using var harness = ProxyHarness.Start(configure: o => o with { AutoSkipAfterRejections = 2 });

        // This "app" only trusts the real server's CA, like an app with its own trust store.
        async Task<string> TryAsync()
        {
            using var client = harness.CreateClient(serverRoot: TestCertificates.ServerRoot);
            return await client.GetStringAsync(_https.Url("/hello"));
        }

        await Assert.ThrowsAsync<HttpRequestException>(TryAsync);
        await harness.Recorder.WaitForFinishedAsync(s => s.Kind == SessionKind.Connect && s.Id == 1);
        await Assert.ThrowsAsync<HttpRequestException>(TryAsync);
        await harness.Recorder.WaitForFinishedAsync(s => s.Kind == SessionKind.Connect && s.Id == 2);

        // Now skipped: the tunnel is relayed blind and the app sees the real certificate.
        var text = await TryAsync();

        Assert.Equal(TestServer.HelloText, text);
        var rejected = harness.Recorder.Sessions.Where(s => s.Id <= 2).ToList();
        Assert.All(rejected, s => Assert.Contains("Snoop root certificate", s.Error ?? s.Note, StringComparison.Ordinal));
        Assert.All(rejected, s => Assert.DoesNotContain("inner exception", s.Error ?? s.Note, StringComparison.OrdinalIgnoreCase));
        Assert.True(harness.Proxy.SkippedHosts.ContainsKey("localhost"));
    }

    [Fact]
    public async Task Auto_skip_can_be_turned_off()
    {
        await using var harness = ProxyHarness.Start(configure: o => o with { AutoSkipAfterRejections = 0 });

        for (var i = 0; i < 3; i++)
        {
            using var client = harness.CreateClient(serverRoot: TestCertificates.ServerRoot);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(_https.Url("/hello")));
        }

        Assert.Empty(harness.Proxy.SkippedHosts);
    }

    [Fact]
    public async Task Busy_port_falls_back_to_the_next_one()
    {
        using var blocker = new TcpListener(System.Net.IPAddress.Loopback, 0);
        blocker.Start();
        var busyPort = ((System.Net.IPEndPoint)blocker.LocalEndpoint).Port;

        await using var harness = ProxyHarness.Start(configure: o => o with { Port = busyPort, PortFallbackAttempts = 20 });

        Assert.NotEqual(busyPort, harness.Proxy.Endpoint!.Port);
        Assert.InRange(harness.Proxy.Endpoint.Port, busyPort + 1, busyPort + 20);
    }

    private static async Task AssertBlindTunnelAsync(ProxyHarness harness)
    {
        var tunnel = await harness.Recorder.WaitForFinishedAsync(s => s.Kind == SessionKind.Connect);
        Assert.Equal(SessionState.Completed, tunnel.State);
        Assert.False(tunnel.Flags.HasFlag(SessionFlags.Decrypted));
        Assert.True(tunnel.BytesSent > 0);
        Assert.True(tunnel.BytesReceived > 0);
        Assert.DoesNotContain(harness.Recorder.Sessions, s => s.Kind == SessionKind.Http);
    }
}
