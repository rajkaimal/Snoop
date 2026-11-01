using Snoop.Proxy.Rules;

namespace Snoop.Proxy.Tests.Rules;

public sealed class RuleSetTests
{
    private static readonly RuleAction Respond = new(RuleActionKind.Respond, "200", "ok");

    [Theory]
    [InlineData(RuleMatchKind.Contains, "/API/items", "https://example.com/api/items?page=2", true)]
    [InlineData(RuleMatchKind.Contains, "/orders", "https://example.com/api/items", false)]
    [InlineData(RuleMatchKind.Exact, "https://example.com/api/items", "https://EXAMPLE.com/api/items", true)]
    [InlineData(RuleMatchKind.Exact, "https://example.com/api", "https://example.com/api/items", false)]
    [InlineData(RuleMatchKind.Wildcard, "https://*.example.com/*.js", "https://cdn.example.com/app/main.js", true)]
    [InlineData(RuleMatchKind.Wildcard, "https://*.example.com/*.js", "https://cdn.example.com/app/main.css", false)]
    [InlineData(RuleMatchKind.Wildcard, "*example.com/a.b*", "https://example.com/aXb", false)]
    [InlineData(RuleMatchKind.Regex, @"/items/\d+$", "https://example.com/api/items/42", true)]
    [InlineData(RuleMatchKind.Regex, @"/items/\d+$", "https://example.com/api/items/new", false)]
    public void Match_kinds_compare_the_full_url(RuleMatchKind kind, string pattern, string url, bool expected)
    {
        var rule = new TrafficRule { MatchKind = kind, Pattern = pattern, Actions = [Respond] };

        Assert.Equal(expected, RuleSet.Matches(rule, "GET", new Uri(url)));
    }

    [Fact]
    public void Only_enabled_valid_rules_match_in_order()
    {
        var set = new RuleSet();
        var first = new TrafficRule { Name = "first", Pattern = "example", Actions = [Respond] };
        var disabled = new TrafficRule { Name = "disabled", Pattern = "example", Enabled = false, Actions = [Respond] };
        var noActions = new TrafficRule { Name = "no actions", Pattern = "example" };
        var badRegex = new TrafficRule { Name = "bad", MatchKind = RuleMatchKind.Regex, Pattern = "([", Actions = [Respond] };
        var last = new TrafficRule { Name = "last", Pattern = "example.com", Actions = [Respond] };
        set.Replace([first, disabled, noActions, badRegex, last]);

        var matched = set.Match("GET", new Uri("https://example.com/"));

        Assert.Equal(new[] { "first", "last" }, matched.Select(r => r.Name));
        Assert.Equal(4, set.EnabledCount);
    }

    [Fact]
    public void Method_filter_ignores_case()
    {
        var rule = new TrafficRule { Pattern = "example", Method = "post", Actions = [Respond] };

        Assert.True(RuleSet.Matches(rule, "POST", new Uri("https://example.com/")));
        Assert.False(RuleSet.Matches(rule, "GET", new Uri("https://example.com/")));
    }

    [Fact]
    public void Replace_raises_changed()
    {
        var set = new RuleSet();
        var raised = 0;
        set.Changed += (_, _) => raised++;

        set.Replace([new TrafficRule { Pattern = "x", Actions = [Respond] }]);

        Assert.Equal(1, raised);
        Assert.Single(set.Rules);
    }

    [Theory]
    [InlineData("", RuleActionKind.Respond, "200", "", "Enter a URL pattern.")]
    [InlineData("x", RuleActionKind.Respond, "abc", "", "status code")]
    [InlineData("x", RuleActionKind.Respond, "99", "", "status code")]
    [InlineData("x", RuleActionKind.Delay, "", "fast", "milliseconds")]
    [InlineData("x", RuleActionKind.SetRequestHeader, "Bad Name", "v", "header name")]
    [InlineData("x", RuleActionKind.SetResponseHeader, "X-Ok", "a\r\nb", "line breaks")]
    [InlineData("x", RuleActionKind.RespondWithFile, "", "", "choose a file")]
    [InlineData("x", RuleActionKind.RewriteUrl, "", "y", "text to replace")]
    public void Validate_explains_problems(string pattern, RuleActionKind kind, string name, string value, string expected)
    {
        var rule = new TrafficRule { Pattern = pattern, Actions = [new RuleAction(kind, name, value)] };

        Assert.Contains(expected, RuleSet.Validate(rule), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_accepts_a_good_rule_and_rejects_one_without_actions()
    {
        Assert.Null(RuleSet.Validate(new TrafficRule { Pattern = "x", Actions = [new RuleAction(RuleActionKind.Delay, "", "250")] }));
        Assert.Contains("at least one action", RuleSet.Validate(new TrafficRule { Pattern = "x" }), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", 200)]
    [InlineData(" 404 ", 404)]
    [InlineData("600", null)]
    [InlineData("-1", null)]
    public void ParseStatus_accepts_100_to_599(string text, int? expected)
    {
        Assert.Equal(expected, RuleSet.ParseStatus(text));
    }
}
