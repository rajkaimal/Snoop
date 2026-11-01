using Snoop.Core.Sessions;
using Snoop.Proxy.Rules;

namespace Snoop.Proxy.Interception;

/// <summary>What Snoop will do with one request: rule actions to run and whether to pause it.</summary>
public sealed class InterceptionPlan
{
    public static InterceptionPlan Empty { get; } = new([], false, false);

    public InterceptionPlan(IReadOnlyList<TrafficRule> rules, bool breakOnRequest, bool breakOnResponse)
    {
        ArgumentNullException.ThrowIfNull(rules);
        Rules = rules;
        BreakOnRequest = breakOnRequest || rules.Any(r => r.Actions.Any(a => a.Kind == RuleActionKind.BreakOnRequest));
        BreakOnResponse = breakOnResponse || rules.Any(r => r.Actions.Any(a => a.Kind == RuleActionKind.BreakOnResponse));
    }

    /// <summary>Matching rules, in order.</summary>
    public IReadOnlyList<TrafficRule> Rules { get; }

    public bool BreakOnRequest { get; }

    public bool BreakOnResponse { get; }

    /// <summary>True when nothing would happen, so the request can be proxied untouched.</summary>
    public bool IsEmpty => Rules.Count == 0 && !BreakOnRequest && !BreakOnResponse;

    /// <summary>True when some rule changes response headers.</summary>
    public bool HasResponseHeaderActions => Rules.Any(r => r.Actions.Any(a => a.Kind is RuleActionKind.SetResponseHeader or RuleActionKind.RemoveResponseHeader));
}

/// <summary>
/// Lets something outside the engine change or pause traffic: rules and breakpoints. The proxy
/// asks <see cref="Plan"/> for every HTTP request (not for tunnels or WebSocket upgrades).
/// </summary>
public interface ITrafficInterceptor
{
    /// <summary>Decides what to do with a request. Called on proxy threads; must be fast and must not throw.</summary>
    InterceptionPlan Plan(RequestInfo request);

    /// <summary>Holds an exchange at a breakpoint until the user resumes or aborts it, or <paramref name="cancellationToken"/> fires.</summary>
    Task<BreakpointDecision> PauseAsync(PausedExchange exchange, CancellationToken cancellationToken);
}

/// <summary>The standard interceptor: the rule list plus breakpoints. One instance lives for the whole capture.</summary>
public sealed class TrafficInterceptor : ITrafficInterceptor
{
    public RuleSet Rules { get; } = new();

    public BreakpointController Breakpoints { get; } = new();

    public InterceptionPlan Plan(RequestInfo request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rules = Rules.Match(request.Method, request.Url);
        var breakRequest = Breakpoints.BreakOnRequests && Breakpoints.Matches(request.Url);
        var breakResponse = Breakpoints.BreakOnResponses && Breakpoints.Matches(request.Url);
        return rules.Count == 0 && !breakRequest && !breakResponse
            ? InterceptionPlan.Empty
            : new InterceptionPlan(rules, breakRequest, breakResponse);
    }

    public Task<BreakpointDecision> PauseAsync(PausedExchange exchange, CancellationToken cancellationToken) =>
        Breakpoints.PauseAsync(exchange, cancellationToken);
}
