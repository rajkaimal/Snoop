using System.Text;
using Snoop.Core.Http;
using Snoop.Presentation.Tests.TestSupport;
using Snoop.Presentation.ViewModels;
using Snoop.Proxy.Interception;

namespace Snoop.Presentation.Tests.ViewModels;

public sealed class BreakpointsViewModelTests
{
    private readonly BreakpointController _controller = new();
    private readonly BreakpointsViewModel _vm;
    private int _arrived;

    public BreakpointsViewModelTests()
    {
        _vm = new BreakpointsViewModel(_controller, new FakeDispatcher());
        _vm.PausedArrived += (_, _) => _arrived++;
    }

    private (PausedExchange Exchange, Task<BreakpointDecision> Decision) PauseRequest(string url = "https://example.com/api", string body = "{\"a\":1}")
    {
        var request = new EditableRequest("POST", new Uri(url), new HttpHeaderList([new("Host", new Uri(url).Host), new("Content-Type", "application/json")]), Encoding.UTF8.GetBytes(body));
        var exchange = new PausedExchange(TestHost.NewSession(url), request);
        return (exchange, _controller.PauseAsync(exchange, CancellationToken.None));
    }

    private (PausedExchange Exchange, Task<BreakpointDecision> Decision) PauseResponse()
    {
        var request = new EditableRequest("GET", new Uri("https://example.com/page"), new HttpHeaderList(), null);
        var response = new EditableResponse(200, "OK", new HttpHeaderList([new("Content-Type", "text/html")]), Encoding.UTF8.GetBytes("<p>hi</p>"));
        var exchange = new PausedExchange(TestHost.NewSession("https://example.com/page"), request, response);
        return (exchange, _controller.PauseAsync(exchange, CancellationToken.None));
    }

    [Fact]
    public void Switches_drive_the_controller()
    {
        _vm.BreakOnRequests = true;
        _vm.Filter = "  /api ";

        Assert.True(_controller.BreakOnRequests);
        Assert.Equal("/api", _controller.Filter);
        Assert.True(_vm.IsArmed);
        Assert.Equal("Breakpoints (on)", _vm.HeaderText);
    }

    [Fact]
    public void Paused_request_is_listed_selected_and_shown_for_editing()
    {
        PauseRequest();

        var item = Assert.Single(_vm.Pending);
        Assert.Same(item, _vm.Selected);
        Assert.Equal(1, _arrived);
        Assert.Equal("Breakpoints (1 paused)", _vm.HeaderText);
        Assert.StartsWith("POST https://example.com/api HTTP/1.1\nHost: example.com", _vm.HeadText, StringComparison.Ordinal);
        Assert.Equal("{\"a\":1}", _vm.BodyText);
        Assert.True(_vm.IsBodyEditable);
    }

    [Fact]
    public async Task Resume_applies_edits_and_removes_the_item()
    {
        var (exchange, decision) = PauseRequest();

        _vm.HeadText = "PUT https://example.com/other HTTP/1.1\rX-Edited: yes";
        _vm.BodyText = "{\"a\":2}";
        _vm.ResumeCommand.Execute(null);

        Assert.Equal(BreakpointDecision.Continue, await decision);
        Assert.Equal("PUT", exchange.Request.Method);
        Assert.Equal("/other", exchange.Request.Url.AbsolutePath);
        Assert.Equal("yes", exchange.Request.Headers.Get("X-Edited"));
        Assert.Equal("{\"a\":2}", Encoding.UTF8.GetString(exchange.Request.Body!));
        Assert.Empty(_vm.Pending);
        Assert.False(_vm.HasSelection);
        Assert.Equal("Breakpoints", _vm.HeaderText);
    }

    [Fact]
    public void Invalid_edit_shows_an_error_and_keeps_it_paused()
    {
        var (exchange, decision) = PauseRequest();

        _vm.HeadText = "not a request line";
        _vm.ResumeCommand.Execute(null);

        Assert.True(_vm.HasError);
        Assert.False(decision.IsCompleted);
        Assert.Equal("POST", exchange.Request.Method);
        exchange.Abort();
    }

    [Fact]
    public async Task Abort_drops_the_exchange()
    {
        var (_, decision) = PauseRequest();

        _vm.AbortCommand.Execute(null);

        Assert.Equal(BreakpointDecision.Abort, await decision);
        Assert.Empty(_vm.Pending);
    }

    [Fact]
    public async Task Response_edits_change_status_and_body()
    {
        var (exchange, decision) = PauseResponse();
        Assert.Equal("GET https://example.com/page", _vm.ContextText);
        Assert.StartsWith("HTTP/1.1 200 OK", _vm.HeadText, StringComparison.Ordinal);

        _vm.HeadText = "HTTP/1.1 503 Down\nContent-Type: text/html";
        _vm.BodyText = "<p>maintenance</p>";
        _vm.ResumeCommand.Execute(null);

        await decision;
        Assert.Equal(503, exchange.Response!.StatusCode);
        Assert.Equal("Down", exchange.Response.ReasonPhrase);
        Assert.Equal("<p>maintenance</p>", Encoding.UTF8.GetString(exchange.Response.Body));
    }

    [Fact]
    public async Task Drafts_survive_switching_between_paused_items()
    {
        var first = PauseRequest("https://example.com/one");
        var second = PauseRequest("https://example.com/two");
        Assert.Equal(0, _vm.SelectedIndex);

        _vm.BodyText = "draft one";
        _vm.SelectedIndex = 1;
        Assert.Equal("{\"a\":1}", _vm.BodyText);
        _vm.SelectedIndex = 0;
        Assert.Equal("draft one", _vm.BodyText);

        _vm.ResumeAllCommand.Execute(null);

        await Task.WhenAll(first.Decision, second.Decision);
        Assert.Equal("draft one", Encoding.UTF8.GetString(first.Exchange.Request.Body!));
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(second.Exchange.Request.Body!));
        Assert.Empty(_vm.Pending);
    }

    [Fact]
    public async Task Exchange_resolved_elsewhere_leaves_the_list()
    {
        var first = PauseRequest("https://example.com/one");
        PauseRequest("https://example.com/two");
        _vm.SelectedIndex = 1;

        first.Exchange.Resume();
        await first.Decision;

        var remaining = Assert.Single(_vm.Pending);
        Assert.Equal("https://example.com/two", remaining.Url);
        Assert.Equal(0, _vm.SelectedIndex);
        remaining.Exchange.Resume();
    }

    [Fact]
    public void Filter_keeps_what_was_typed_and_gives_the_controller_trimmed_text()
    {
        _vm.Filter = "foo ";

        Assert.Equal("foo ", _vm.Filter);
        Assert.Equal("foo", _controller.Filter);
    }
}
