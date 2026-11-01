using System.Text;
using Snoop.Core.Http;
using Snoop.Core.Sessions;
using Snoop.Hosting.Composer;
using Snoop.Hosting.Rules;
using Snoop.Platform.SystemProxy;
using Snoop.Proxy.Processes;
using Snoop.Proxy.Rules;
using Snoop.Proxy.Tls;

namespace Snoop.Hosting.Tests;

/// <summary>Composer, rules file, snapshots and session numbering through the capture host.</summary>
public sealed class CaptureHostFeatureTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "snoop-tests", Guid.NewGuid().ToString("N"));
    private CaptureHost _host = null!;

    public Task InitializeAsync()
    {
        var services = new CaptureHostServices(
            () => new CertificateAuthorityStore(_directory, UnprotectedSecretProtector.Instance),
            new SystemProxyController(new NullProxyApi(), Path.Combine(_directory, "proxy-backup.json")),
            NullClientProcessResolver.Instance,
            new NoTrust());
        _host = new CaptureHost(services: services);
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

    private static ComposedRequest Request(string method, string url, string? body = null, params (string, string)[] headers) =>
        new(method, new Uri(url), new HttpHeaderList(headers.Select(h => new HttpHeader(h.Item1, h.Item2))), body is null ? null : Encoding.UTF8.GetBytes(body));

    [Fact]
    public async Task Composer_request_goes_through_the_proxy_and_returns_its_session()
    {
        await _host.StartAsync(new CaptureSettings { Port = 0 });
        _host.Rules.Replace([new TrafficRule { Name = "mock", Pattern = "composer.test", Actions = [new RuleAction(RuleActionKind.Respond, "202", "{\"ok\":true}")] }]);

        var result = await _host.SendComposedAsync(Request("post", "http://composer.test/api", "{\"a\":1}", ("Content-Type", "application/json"), ("X-Custom", "yes")));

        Assert.Null(result.Error);
        Assert.Equal(202, result.StatusCode);
        var session = result.Session!;
        Assert.True(session.Flags.HasFlag(SessionFlags.Composed));
        Assert.Equal("POST", session.Request.Method);
        Assert.Equal("yes", session.Request.Headers.Get("X-Custom"));
        Assert.Equal("application/json", session.Request.Headers.Get("Content-Type"));
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(session.Request.Body!.ToArray()));
        Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(session.Response!.Body!.ToArray()));
        Assert.Same(session, _host.Sessions.Find(session.Id));
    }

    [Fact]
    public async Task Composer_needs_a_running_capture()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _host.SendComposedAsync(Request("GET", "http://example.com/")));
    }

    [Fact]
    public async Task Composer_rejects_bad_input()
    {
        await _host.StartAsync(new CaptureSettings { Port = 0 });

        await Assert.ThrowsAsync<ArgumentException>(() => _host.SendComposedAsync(Request("GET", "ftp://example.com/")));
        await Assert.ThrowsAsync<ArgumentException>(() => _host.SendComposedAsync(Request("GE T", "http://example.com/")));
    }

    [Fact]
    public async Task Composer_reports_an_unreachable_server_with_its_session()
    {
        await _host.StartAsync(new CaptureSettings { Port = 0 });

        // Port 9 (discard) on loopback is closed on test machines.
        var result = await _host.SendComposedAsync(Request("GET", "http://127.0.0.1:9/nothing"));

        Assert.Equal(502, result.StatusCode);
        Assert.Equal(SessionState.Failed, result.Session!.State);
    }

    [Fact]
    public void Composer_message_moves_content_headers_and_adds_the_marker()
    {
        using var message = ComposerClient.BuildMessage(Request("GET", "http://example.com/", "x", ("Content-Type", "text/plain"), ("Content-Length", "999"), ("Accept", "*/*")), "id1");

        Assert.Equal("text/plain", message.Content!.Headers.ContentType!.ToString());
        Assert.Equal("*/*", message.Headers.Accept.ToString());
        Assert.Equal("id1", message.Headers.GetValues(Snoop.Proxy.SnoopHeaders.ComposerId).Single());
        Assert.Equal(1, message.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Session_numbers_never_repeat_across_restarts()
    {
        await _host.StartAsync(new CaptureSettings { Port = 0 });
        _host.Rules.Replace([new TrafficRule { Pattern = "numbers.test", Actions = [new RuleAction(RuleActionKind.Respond, "200", "x")] }]);
        var first = await _host.SendComposedAsync(Request("GET", "http://numbers.test/1"));

        await _host.StartAsync(new CaptureSettings { Port = 0 });
        var second = await _host.SendComposedAsync(Request("GET", "http://numbers.test/2"));

        Assert.NotNull(second.Session);
        Assert.True(second.Session!.Id > first.Session!.Id);
        Assert.Equal(2, _host.Sessions.Count);
    }

    [Fact]
    public async Task Snapshot_round_trip_through_the_host()
    {
        await _host.StartAsync(new CaptureSettings { Port = 0 });
        _host.Rules.Replace([new TrafficRule { Pattern = "snap.test", Actions = [new RuleAction(RuleActionKind.Respond, "200", "saved body")] }]);
        await _host.SendComposedAsync(Request("GET", "http://snap.test/a"));

        using var file = new MemoryStream();
        Assert.Equal(1, CaptureHost.SaveSnapshot(file, _host.Sessions.Snapshot()));
        file.Position = 0;
        var added = _host.LoadSnapshot(file);

        Assert.Equal(1, added);
        var sessions = _host.Sessions.Snapshot();
        Assert.Equal(2, sessions.Count);
        Assert.NotEqual(sessions[0].Id, sessions[1].Id);
        Assert.True(sessions[1].Flags.HasFlag(SessionFlags.Imported));
        Assert.Equal("saved body", Encoding.UTF8.GetString(sessions[1].Response!.Body!.ToArray()));
    }

    [Fact]
    public void Rules_file_round_trip()
    {
        var path = Path.Combine(_directory, "rules.json");
        var rules = new[]
        {
            new TrafficRule { Name = "one", MatchKind = RuleMatchKind.Wildcard, Pattern = "https://*.example.com/*", Method = "GET", Actions = [new RuleAction(RuleActionKind.SetRequestHeader, "X-A", "1"), new RuleAction(RuleActionKind.BreakOnResponse)] },
            new TrafficRule { Name = "two", Enabled = false, Pattern = "x", Actions = [new RuleAction(RuleActionKind.Delay, "", "100")] },
        };

        RulesFile.Save(rules, path);
        var loaded = RulesFile.Load(path);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(rules[0].Id, loaded[0].Id);
        Assert.Equal(RuleMatchKind.Wildcard, loaded[0].MatchKind);
        Assert.Equal("GET", loaded[0].Method);
        Assert.Equal(rules[0].Actions, loaded[0].Actions);
        Assert.False(loaded[1].Enabled);
        Assert.Contains("\"SetRequestHeader\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_rules_file_means_no_rules_and_a_damaged_one_is_reported()
    {
        Assert.Empty(RulesFile.Load(Path.Combine(_directory, "none.json")));

        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "bad.json");
        File.WriteAllText(path, "{ not json");

        Assert.Throws<InvalidDataException>(() => RulesFile.Load(path));
    }

    private sealed class NullProxyApi : IProxySettingsApi
    {
        private ProxySettings _current = ProxySettings.Direct;

        public ProxySettings Read() => _current;

        public void Write(ProxySettings settings) => _current = settings;
    }

    private sealed class NoTrust : IRootCertificateTrust
    {
        public bool IsSupported => false;

        public bool IsTrusted(System.Security.Cryptography.X509Certificates.X509Certificate2 root) => false;

        public void Trust(System.Security.Cryptography.X509Certificates.X509Certificate2 root)
        {
        }

        public int RemoveAll() => 0;
    }
}
