namespace Snoop.Proxy.Rules;

/// <summary>How a rule's pattern is compared with the full request URL.</summary>
public enum RuleMatchKind
{
    /// <summary>The URL contains the pattern (ignoring case).</summary>
    Contains,

    /// <summary>The whole URL matches a pattern where <c>*</c> stands for any text (ignoring case).</summary>
    Wildcard,

    /// <summary>The URL matches a .NET regular expression (ignoring case).</summary>
    Regex,

    /// <summary>The URL is exactly the pattern (ignoring case).</summary>
    Exact,
}

/// <summary>What a rule does to a matching request. See <see cref="RuleAction"/> for the meaning of Name and Value.</summary>
public enum RuleActionKind
{
    /// <summary>Answer without contacting the server. Name: status code (default 200). Value: response body text.</summary>
    Respond,

    /// <summary>Answer with a file's contents. Name: status code (default 200). Value: file path.</summary>
    RespondWithFile,

    /// <summary>Wait before sending the request on. Value: milliseconds.</summary>
    Delay,

    /// <summary>Close the connection without an answer, as if the server were unreachable.</summary>
    Block,

    /// <summary>Change the URL. Name: text to find (ignoring case). Value: replacement.</summary>
    RewriteUrl,

    /// <summary>Add or replace a request header. Name: header. Value: value.</summary>
    SetRequestHeader,

    /// <summary>Remove a request header. Name: header.</summary>
    RemoveRequestHeader,

    /// <summary>Add or replace a response header. Name: header. Value: value.</summary>
    SetResponseHeader,

    /// <summary>Remove a response header. Name: header.</summary>
    RemoveResponseHeader,

    /// <summary>Pause the request before it goes to the server, so it can be edited.</summary>
    BreakOnRequest,

    /// <summary>Pause the response before it goes to the app, so it can be edited.</summary>
    BreakOnResponse,
}

/// <summary>One step of a rule. The meaning of <see cref="Name"/> and <see cref="Value"/> depends on <see cref="Kind"/>.</summary>
public sealed record RuleAction(RuleActionKind Kind, string Name = "", string Value = "")
{
    /// <summary>True for actions that run before the request is sent; false for those that change the response.</summary>
    public bool IsRequestStage => Kind is not (RuleActionKind.SetResponseHeader or RuleActionKind.RemoveResponseHeader or RuleActionKind.BreakOnResponse);

    /// <summary>True for actions that answer the request themselves, ending the rule list.</summary>
    public bool IsTerminal => Kind is RuleActionKind.Respond or RuleActionKind.RespondWithFile or RuleActionKind.Block;

    public override string ToString() => Kind switch
    {
        RuleActionKind.Respond => $"Respond {StatusOrDefault(Name)}",
        RuleActionKind.RespondWithFile => $"Respond {StatusOrDefault(Name)} with {Path.GetFileName(Value)}",
        RuleActionKind.Delay => $"Delay {Value} ms",
        RuleActionKind.Block => "Block",
        RuleActionKind.RewriteUrl => $"Rewrite '{Name}' to '{Value}'",
        RuleActionKind.SetRequestHeader => $"Request header {Name}: {Value}",
        RuleActionKind.RemoveRequestHeader => $"Remove request header {Name}",
        RuleActionKind.SetResponseHeader => $"Response header {Name}: {Value}",
        RuleActionKind.RemoveResponseHeader => $"Remove response header {Name}",
        RuleActionKind.BreakOnRequest => "Break on request",
        RuleActionKind.BreakOnResponse => "Break on response",
        _ => Kind.ToString(),
    };

    private static string StatusOrDefault(string text) => string.IsNullOrWhiteSpace(text) ? "200" : text.Trim();
}

/// <summary>
/// A traffic rule: when a request matches, its actions run in order. Rules are evaluated top to
/// bottom and every matching rule applies, until one answers the request (Respond, Block).
/// </summary>
public sealed record TrafficRule
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "New rule";

    public bool Enabled { get; init; } = true;

    public RuleMatchKind MatchKind { get; init; } = RuleMatchKind.Contains;

    /// <summary>Compared with the full URL. An empty pattern matches nothing.</summary>
    public string Pattern { get; init; } = string.Empty;

    /// <summary>Only requests with this method match. Empty matches any method.</summary>
    public string Method { get; init; } = string.Empty;

    public IReadOnlyList<RuleAction> Actions { get; init; } = [];
}
