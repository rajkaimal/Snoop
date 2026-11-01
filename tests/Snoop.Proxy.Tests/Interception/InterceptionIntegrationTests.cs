using System.Net;
using System.Text;
using Snoop.Core.Sessions;
using Snoop.Proxy.Interception;
using Snoop.Proxy.Rules;
using Snoop.Proxy.Tests.TestSupport;

namespace Snoop.Proxy.Tests.Interception;

/// <summary>Rules and breakpoints, end to end through a real proxy and a real server.</summary>
public sealed class InterceptionIntegrationTests : IAsyncLifetime
{
    private readonly TrafficInterceptor _interceptor = new();
    private TestServer _http = null!;
    private TestServer _https = null!;
    private ProxyHarness _harness = null!;

    public async Task InitializeAsync()
    {
        _http = await TestServer.StartAsync();
        _https = await TestServer.StartAsync(TestCertificates.ServerCertificate());
        _harness = ProxyHarness.Start(upstreamRoot: TestCertificates.ServerRoot, interceptor: _interceptor);
    }

    public async Task DisposeAsync()
    {
        _interceptor.Breakpoints.ResumeAll();
        await _harness.DisposeAsync();
        await _http.DisposeAsync();
        await _https.DisposeAsync();
    }

    private void UseRules(params TrafficRule[] rules) => _interceptor.Rules.Replace(rules);

    private static TrafficRule Rule(string name, string pattern, params RuleAction[] actions) => new() { Name = name, Pattern = pattern, Actions = actions };

    [Fact]
    public async Task Respond_rule_answers_without_contacting_the_server()
    {
        UseRules(Rule("mock", "nowhere.invalid/api", new RuleAction(RuleActionKind.Respond, "418", "{\"mocked\":true}"), new RuleAction(RuleActionKind.SetResponseHeader, "X-Mock", "yes")));
        using var client = _harness.CreateClient();

        using var response = await client.GetAsync("http://nowhere.invalid/api/user");

        Assert.Equal((HttpStatusCode)418, response.StatusCode);
        Assert.Equal("{\"mocked\":true}", await response.Content.ReadAsStringAsync());
        Assert.Equal("yes", response.Headers.GetValues("X-Mock").Single());
        var session = await _harness.Recorder.WaitForFinishedAsync("/api/user");
        Assert.True(session.Flags.HasFlag(SessionFlags.AutoResponded));
        Assert.True(session.Flags.HasFlag(SessionFlags.Modified));
        Assert.Equal("rule 'mock'", session.ModifiedBy);
        Assert.Equal(418, session.Response!.StatusCode);
        Assert.Equal("{\"mocked\":true}", Encoding.UTF8.GetString(session.Response.Body!.ToArray()));
    }

    [Fact]
    public async Task Respond_rule_works_inside_decrypted_https_and_keeps_the_connection()
    {
        UseRules(Rule("mock", "/mocked", new RuleAction(RuleActionKind.Respond, "", "fake")));
        using var client = _harness.CreateClient();

        Assert.Equal("fake", await client.GetStringAsync(_https.Url("/mocked")));
        Assert.Equal(TestServer.HelloText, await client.GetStringAsync(_https.Url("/hello")));
    }

    [Fact]
    public async Task Request_header_rule_reaches_the_server_and_the_original_is_kept()
    {
        UseRules(Rule("auth", "/headers", new RuleAction(RuleActionKind.SetRequestHeader, "X-Added", "by-rule"), new RuleAction(RuleActionKind.RemoveRequestHeader, "X-Secret")));
        using var client = _harness.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, _http.Url("/headers"));
        request.Headers.Add("X-Secret", "hidden");

        using var response = await client.SendAsync(request);
        var echoed = await response.Content.ReadAsStringAsync();

        Assert.Contains("X-Added: by-rule", echoed, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Secret", echoed, StringComparison.Ordinal);
        var session = await _harness.Recorder.WaitForFinishedAsync("/headers");
        Assert.Equal("by-rule", session.Request.Headers.Get("X-Added"));
        Assert.Equal("hidden", session.OriginalRequest!.Headers.Get("X-Secret"));
    }

    [Fact]
    public async Task Rewrite_rule_sends_the_request_somewhere_else()
    {
        UseRules(Rule("map", "nowhere.invalid", new RuleAction(RuleActionKind.RewriteUrl, "http://nowhere.invalid", _http.BaseAddress.GetLeftPart(UriPartial.Authority))));
        using var client = _harness.CreateClient();

        Assert.Equal(TestServer.HelloText, await client.GetStringAsync("http://nowhere.invalid/hello"));

        var session = await _harness.Recorder.WaitForFinishedAsync("/hello");
        Assert.Equal(_http.Port, session.Request.Url.Port);
        Assert.Equal("nowhere.invalid", session.OriginalRequest!.Url.Host);
    }

    [Fact]
    public async Task Response_header_rule_changes_a_streamed_response()
    {
        UseRules(Rule("cors", "/stream", new RuleAction(RuleActionKind.SetResponseHeader, "Access-Control-Allow-Origin", "*")));
        using var client = _harness.CreateClient();

        using var response = await client.GetAsync(_http.Url("/stream"));

        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("part0;part1;part2;part3;part4;", await response.Content.ReadAsStringAsync());
        var session = await _harness.Recorder.WaitForFinishedAsync("/stream");
        Assert.Equal("*", session.Response!.Headers.Get("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Post_body_still_reaches_the_server_when_a_rule_applies()
    {
        UseRules(Rule("tag", "/echo", new RuleAction(RuleActionKind.SetRequestHeader, "X-Tag", "1")));
        using var client = _harness.CreateClient();
        using var content = new StringContent("payload", Encoding.UTF8, "text/plain");

        using var response = await client.PostAsync(_http.Url("/echo"), content);

        Assert.Equal("payload", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Block_rule_closes_the_connection()
    {
        UseRules(Rule("block", "/hello", new RuleAction(RuleActionKind.Block)));
        using var client = _harness.CreateClient();

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(_http.Url("/hello")));

        var session = await _harness.Recorder.WaitForFinishedAsync("/hello");
        Assert.Equal(SessionState.Aborted, session.State);
        Assert.Equal("Blocked by rule 'block'.", session.Error);
    }

    [Fact]
    public async Task Request_breakpoint_lets_the_user_edit_the_request()
    {
        _interceptor.Breakpoints.BreakOnRequests = true;
        _interceptor.Breakpoints.Filter = "/echo";
        var paused = WaitForPause();
        using var client = _harness.CreateClient();
        using var content = new StringContent("original", Encoding.UTF8, "text/plain");

        var sending = client.PostAsync(_http.Url("/echo"), content);
        var exchange = await paused;
        Assert.Equal(BreakpointStage.Request, exchange.Stage);
        Assert.Equal(SessionState.Paused, exchange.Session.State);
        Assert.Equal("original", Encoding.UTF8.GetString(exchange.Request.Body!));
        exchange.Request.Body = Encoding.UTF8.GetBytes("edited at the breakpoint");
        exchange.Resume();

        using var response = await sending;
        Assert.Equal("edited at the breakpoint", await response.Content.ReadAsStringAsync());
        var session = await _harness.Recorder.WaitForFinishedAsync("/echo");
        Assert.True(session.Flags.HasFlag(SessionFlags.Breakpoint));
        Assert.Equal("edited at a breakpoint", session.ModifiedBy);
        Assert.Equal("original", Encoding.UTF8.GetString(session.OriginalRequest!.Body!.ToArray()));
        Assert.Empty(_interceptor.Breakpoints.Pending);
    }

    [Fact]
    public async Task Breakpoint_filter_lets_other_requests_through()
    {
        _interceptor.Breakpoints.BreakOnRequests = true;
        _interceptor.Breakpoints.Filter = "/something-else";
        using var client = _harness.CreateClient();

        Assert.Equal(TestServer.HelloText, await client.GetStringAsync(_http.Url("/hello")));
        Assert.Empty(_interceptor.Breakpoints.Pending);
    }

    [Fact]
    public async Task Response_breakpoint_lets_the_user_edit_the_response()
    {
        _interceptor.Breakpoints.BreakOnResponses = true;
        var paused = WaitForPause();
        using var client = _harness.CreateClient();

        var getting = client.GetAsync(_https.Url("/hello"));
        var exchange = await paused;
        Assert.Equal(BreakpointStage.Response, exchange.Stage);
        Assert.Equal(TestServer.HelloText, Encoding.UTF8.GetString(exchange.Response!.Body));
        Assert.Equal(200, exchange.Session.Response!.StatusCode); // the server's response is visible while paused
        exchange.Response.StatusCode = 201;
        exchange.Response.Body = Encoding.UTF8.GetBytes("changed!");
        exchange.Resume();

        using var response = await getting;
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("changed!", await response.Content.ReadAsStringAsync());
        Assert.Equal(8, response.Content.Headers.ContentLength);
        var session = await _harness.Recorder.WaitForFinishedAsync("/hello");
        Assert.Equal(201, session.Response!.StatusCode);
        Assert.Equal("changed!", Encoding.UTF8.GetString(session.Response.Body!.ToArray()));
    }

    [Fact]
    public async Task Breakpoint_rule_action_pauses_only_matching_requests()
    {
        UseRules(Rule("pause", "/hello", new RuleAction(RuleActionKind.BreakOnRequest)));
        var paused = WaitForPause();
        using var client = _harness.CreateClient();

        var getting = client.GetStringAsync(_http.Url("/hello"));
        var exchange = await paused;
        exchange.Resume();

        Assert.Equal(TestServer.HelloText, await getting);
    }

    [Fact]
    public async Task Aborting_at_a_request_breakpoint_gives_the_app_an_error()
    {
        _interceptor.Breakpoints.BreakOnRequests = true;
        var paused = WaitForPause();
        using var client = _harness.CreateClient();

        var getting = client.GetAsync(_http.Url("/hello"));
        (await paused).Abort();

        using var response = await getting;
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var session = await _harness.Recorder.WaitForFinishedAsync("/hello");
        Assert.Equal(SessionState.Aborted, session.State);
    }

    [Fact]
    public async Task Stopping_the_proxy_releases_paused_requests()
    {
        _interceptor.Breakpoints.BreakOnRequests = true;
        var paused = WaitForPause();
        using var client = _harness.CreateClient();

        var getting = client.GetAsync(_http.Url("/hello"));
        await paused;
        await _harness.Proxy.StopAsync();

        await Assert.ThrowsAsync<HttpRequestException>(() => getting);
        Assert.Empty(_interceptor.Breakpoints.Pending);
    }

    [Fact]
    public async Task Composer_marker_is_removed_and_recorded()
    {
        using var client = _harness.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, _http.Url("/headers"));
        request.Headers.Add(SnoopHeaders.ComposerId, "abc123");

        using var response = await client.SendAsync(request);

        Assert.DoesNotContain(SnoopHeaders.ComposerId, await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        var session = await _harness.Recorder.WaitForFinishedAsync("/headers");
        Assert.Equal("abc123", session.ComposerId);
        Assert.True(session.Flags.HasFlag(SessionFlags.Composed));
        Assert.False(session.Request.Headers.Contains(SnoopHeaders.ComposerId));
    }

    [Fact]
    public async Task Zstd_is_taken_out_of_accept_encoding_when_asked()
    {
        await using var harness = ProxyHarness.Start(configure: o => o with { RemoveZstdFromAcceptEncoding = true });
        using var client = harness.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, _http.Url("/headers"));
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br, zstd");

        using var response = await client.SendAsync(request);
        var echoed = await response.Content.ReadAsStringAsync();

        Assert.Contains("Accept-Encoding: gzip, br", echoed, StringComparison.Ordinal);
        Assert.DoesNotContain("zstd", echoed, StringComparison.Ordinal);
    }

    private Task<PausedExchange> WaitForPause()
    {
        var paused = new TaskCompletionSource<PausedExchange>(TaskCreationOptions.RunContinuationsAsynchronously);
        _interceptor.Breakpoints.Paused += (_, exchange) => paused.TrySetResult(exchange);
        return paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
