using System.Text;
using Snoop.Core.Http;
using Snoop.Proxy.Interception;
using Snoop.Proxy.Rules;

namespace Snoop.Proxy.Tests.Rules;

public sealed class RuleEngineTests
{
    private static EditableRequest NewRequest(string url = "https://api.example.com/v1/items") =>
        new("GET", new Uri(url), new HttpHeaderList([new("Host", new Uri(url).Authority), new("Accept", "*/*"), new("Cookie", "a=1")]), null);

    private static TrafficRule Rule(string name, params RuleAction[] actions) => new() { Name = name, Pattern = "example", Actions = actions };

    [Fact]
    public async Task Header_actions_change_the_request_and_name_the_rule()
    {
        var request = NewRequest();

        var outcome = await RuleEngine.ApplyToRequestAsync(
            request,
            [Rule("auth", new RuleAction(RuleActionKind.SetRequestHeader, "Authorization", "Bearer test"), new RuleAction(RuleActionKind.RemoveRequestHeader, "cookie"))],
            CancellationToken.None);

        Assert.Equal(RuleResult.Continue, outcome.Result);
        Assert.Equal("Bearer test", request.Headers.Get("Authorization"));
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.Equal(new[] { "auth" }, outcome.AppliedRules);
    }

    [Fact]
    public async Task Rewrite_to_another_host_updates_the_host_header()
    {
        var request = NewRequest();

        await RuleEngine.ApplyToRequestAsync(request, [Rule("local", new RuleAction(RuleActionKind.RewriteUrl, "https://api.example.com", "http://localhost:5000"))], CancellationToken.None);

        Assert.Equal(new Uri("http://localhost:5000/v1/items"), request.Url);
        Assert.Equal("localhost:5000", request.Headers.Get("Host"));
    }

    [Fact]
    public async Task Rewrite_that_breaks_the_url_is_ignored()
    {
        var request = NewRequest();

        var outcome = await RuleEngine.ApplyToRequestAsync(request, [Rule("bad", new RuleAction(RuleActionKind.RewriteUrl, "https://", "ftp://"))], CancellationToken.None);

        Assert.Equal(new Uri("https://api.example.com/v1/items"), request.Url);
        Assert.Empty(outcome.AppliedRules);
    }

    [Fact]
    public async Task Respond_stops_at_the_first_answer_and_guesses_json()
    {
        var request = NewRequest();

        var outcome = await RuleEngine.ApplyToRequestAsync(
            request,
            [
                Rule("mock", new RuleAction(RuleActionKind.Respond, "201", "{\"id\":7}")),
                Rule("never", new RuleAction(RuleActionKind.SetRequestHeader, "X-Never", "1")),
            ],
            CancellationToken.None);

        Assert.Equal(RuleResult.Respond, outcome.Result);
        Assert.Equal("mock", outcome.DecidedBy);
        Assert.Equal(201, outcome.Response!.StatusCode);
        Assert.Equal("Created", outcome.Response.ReasonPhrase);
        Assert.Equal("application/json; charset=utf-8", outcome.Response.Headers.Get("Content-Type"));
        Assert.Equal("{\"id\":7}", Encoding.UTF8.GetString(outcome.Response.Body));
        Assert.False(request.Headers.Contains("X-Never"));
    }

    [Fact]
    public async Task Respond_with_file_sends_the_file_with_its_type()
    {
        var path = Path.Combine(Path.GetTempPath(), $"snoop-rule-{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(path, "<p>hi</p>");
        try
        {
            var outcome = await RuleEngine.ApplyToRequestAsync(NewRequest(), [Rule("file", new RuleAction(RuleActionKind.RespondWithFile, "", path))], CancellationToken.None);

            Assert.Equal(200, outcome.Response!.StatusCode);
            Assert.Equal("text/html; charset=utf-8", outcome.Response.Headers.Get("Content-Type"));
            Assert.Equal("<p>hi</p>", Encoding.UTF8.GetString(outcome.Response.Body));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Respond_with_a_missing_file_gives_404_with_the_reason()
    {
        var outcome = await RuleEngine.ApplyToRequestAsync(NewRequest(), [Rule("file", new RuleAction(RuleActionKind.RespondWithFile, "", "/no/such/file.json"))], CancellationToken.None);

        Assert.Equal(404, outcome.Response!.StatusCode);
        Assert.Contains("does not exist", Encoding.UTF8.GetString(outcome.Response.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Block_is_terminal()
    {
        var outcome = await RuleEngine.ApplyToRequestAsync(NewRequest(), [Rule("block", new RuleAction(RuleActionKind.Block))], CancellationToken.None);

        Assert.Equal(RuleResult.Block, outcome.Result);
        Assert.Equal("block", outcome.DecidedBy);
    }

    [Fact]
    public async Task Delay_waits_before_continuing()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();

        var outcome = await RuleEngine.ApplyToRequestAsync(NewRequest(), [Rule("slow", new RuleAction(RuleActionKind.Delay, "", "150"))], CancellationToken.None);

        Assert.True(started.ElapsedMilliseconds >= 140, $"waited {started.ElapsedMilliseconds} ms");
        Assert.Equal(new[] { "slow" }, outcome.AppliedRules);
    }

    [Fact]
    public void Response_header_actions_report_rules_that_changed_something()
    {
        var headers = new HttpHeaderList([new("Cache-Control", "max-age=60"), new("Server", "kestrel")]);

        var applied = RuleEngine.ApplyToResponseHeaders(
            headers,
            [
                Rule("cors", new RuleAction(RuleActionKind.SetResponseHeader, "Access-Control-Allow-Origin", "*")),
                Rule("noop", new RuleAction(RuleActionKind.RemoveResponseHeader, "X-Missing")),
                Rule("hide", new RuleAction(RuleActionKind.RemoveResponseHeader, "server")),
            ]);

        Assert.Equal(new[] { "cors", "hide" }, applied);
        Assert.Equal("*", headers.Get("Access-Control-Allow-Origin"));
        Assert.False(headers.Contains("Server"));
    }

    [Theory]
    [InlineData("  [1,2]", "application/json; charset=utf-8")]
    [InlineData("<!DOCTYPE html><html>", "text/html; charset=utf-8")]
    [InlineData("<?xml version=\"1.0\"?><a/>", "application/xml; charset=utf-8")]
    [InlineData("plain", "text/plain; charset=utf-8")]
    public void Text_media_type_is_guessed_from_the_body(string body, string expected)
    {
        Assert.Equal(expected, RuleEngine.GuessTextMediaType(body));
    }

    [Fact]
    public void Editable_messages_detect_changes()
    {
        var request = NewRequest();
        var copy = request.Clone();
        Assert.False(request.DiffersFrom(copy));

        request.Body = [];
        Assert.False(request.DiffersFrom(copy)); // empty body is the same as none

        request.Headers.Add("X-New", "1");
        Assert.True(request.DiffersFrom(copy));

        var response = new EditableResponse(200, "OK", new HttpHeaderList(), [1, 2]);
        var responseCopy = response.Clone();
        response.Body[0] = 9;
        Assert.True(response.DiffersFrom(responseCopy));
    }
}
