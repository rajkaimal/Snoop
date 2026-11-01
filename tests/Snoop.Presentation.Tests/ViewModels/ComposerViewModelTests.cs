using System.Text;
using Snoop.Core.Http;
using Snoop.Core.Sessions;
using Snoop.Hosting;
using Snoop.Presentation.Tests.TestSupport;
using Snoop.Presentation.ViewModels;
using Snoop.Proxy.Rules;

namespace Snoop.Presentation.Tests.ViewModels;

public sealed class ComposerViewModelTests : IAsyncLifetime
{
    private TestHost _test = null!;
    private ComposerViewModel _vm = null!;

    public Task InitializeAsync()
    {
        _test = new TestHost();
        _vm = new ComposerViewModel(_test.Host);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _test.DisposeAsync();

    [Fact]
    public void Build_adds_https_and_guesses_the_content_type()
    {
        _vm.Method = " post ";
        _vm.Url = "example.com/api";
        _vm.HeadersText = "Accept: application/json\r\rX-Trace: 1";
        _vm.BodyText = "{\"a\":1}\r";

        var request = _vm.BuildRequest(out var error);

        Assert.Null(error);
        Assert.Equal("POST", request!.Method);
        Assert.Equal(new Uri("https://example.com/api"), request.Url);
        Assert.Equal("1", request.Headers.Get("X-Trace"));
        Assert.Equal("application/json", request.Headers.Get("Content-Type"));
        Assert.Equal("{\"a\":1}\n", Encoding.UTF8.GetString(request.Body!));
    }

    [Theory]
    [InlineData("GET", "ftp://example.com/", "", "http:// or https://")]
    [InlineData("", "https://example.com/", "", "method")]
    [InlineData("GET", "https://example.com/", "broken header", "Header line 1")]
    public void Build_explains_problems(string method, string url, string headers, string expected)
    {
        _vm.Method = method;
        _vm.Url = url;
        _vm.HeadersText = headers;

        Assert.Null(_vm.BuildRequest(out var error));
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_from_session_copies_the_request_without_connection_headers()
    {
        var session = TestHost.NewSession("https://example.com/items?id=1");
        session.Request.Headers.Add("Accept", "*/*");
        session.Request.Headers.Add("Proxy-Connection", "keep-alive");

        _vm.LoadFrom(session);

        Assert.Equal("GET", _vm.Method);
        Assert.Equal("https://example.com/items?id=1", _vm.Url);
        Assert.Equal("Accept: */*", _vm.HeadersText);
        Assert.Contains("Loaded #", _vm.ResultText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_without_capture_explains_why()
    {
        _vm.Url = "https://example.com/";

        await _vm.SendCommand.ExecuteAsync();

        Assert.True(_vm.ResultIsError);
        Assert.Contains("Start capturing", _vm.ResultText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_goes_through_the_capture_and_shows_the_response()
    {
        await _test.Host.StartAsync(new CaptureSettings { Port = 0 });
        _test.Host.Rules.Replace([new TrafficRule { Pattern = "composer.test", Actions = [new RuleAction(RuleActionKind.Respond, "200", "{\"pong\":true}")] }]);
        Session? sent = null;
        _vm.Sent += (_, s) => sent = s;
        _vm.Method = "GET";
        _vm.Url = "http://composer.test/ping";

        await _vm.SendCommand.ExecuteAsync();

        Assert.False(_vm.ResultIsError, _vm.ResultText);
        Assert.StartsWith("200 OK", _vm.ResultText, StringComparison.Ordinal);
        Assert.True(_vm.Response.IsPresent);
        Assert.Contains("pong", _vm.Response.BodyText, StringComparison.Ordinal);
        Assert.NotNull(sent);
        Assert.Same(sent, _vm.LastSession);
        Assert.True(sent!.Flags.HasFlag(SessionFlags.Composed));
        Assert.False(_vm.IsSending);
    }
}
