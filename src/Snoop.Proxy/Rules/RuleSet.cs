using System.Globalization;
using System.Text.RegularExpressions;

namespace Snoop.Proxy.Rules;

/// <summary>
/// The active traffic rules. The whole list is replaced at once (<see cref="Replace"/>), so the
/// proxy always sees a consistent set. Thread-safe; matching never blocks.
/// </summary>
public sealed class RuleSet
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private volatile CompiledRule[] _rules = [];

    /// <summary>Raised after <see cref="Replace"/>. Any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>All rules, in order, enabled or not.</summary>
    public IReadOnlyList<TrafficRule> Rules => [.. _rules.Select(r => r.Rule)];

    /// <summary>Number of enabled rules.</summary>
    public int EnabledCount => _rules.Count(r => r.Rule.Enabled);

    /// <summary>Replaces every rule. Invalid rules are kept but never match (see <see cref="Validate"/>).</summary>
    public void Replace(IEnumerable<TrafficRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = [.. rules.Select(Compile)];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The enabled rules that match a request, in order.</summary>
    public IReadOnlyList<TrafficRule> Match(string method, Uri url)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(url);
        var rules = _rules;
        if (rules.Length == 0)
        {
            return [];
        }

        var text = url.AbsoluteUri;
        List<TrafficRule>? matched = null;
        foreach (var rule in rules)
        {
            if (rule.IsMatch(method, text))
            {
                (matched ??= []).Add(rule.Rule);
            }
        }

        if (matched is null)
        {
            return [];
        }

        return matched;
    }

    /// <summary>Whether <paramref name="rule"/> matches the request, ignoring whether it is enabled.</summary>
    public static bool Matches(TrafficRule rule, string method, Uri url)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(url);
        return Compile(rule with { Enabled = true }).IsMatch(method, url.AbsoluteUri);
    }

    /// <summary>Describes what is wrong with a rule, or returns null when it is usable.</summary>
    public static string? Validate(TrafficRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (string.IsNullOrWhiteSpace(rule.Pattern))
        {
            return "Enter a URL pattern.";
        }

        if (rule.MatchKind == RuleMatchKind.Regex)
        {
            try
            {
                _ = new Regex(rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                return $"The regular expression is not valid: {ex.Message}";
            }
        }

        if (rule.Actions.Count == 0)
        {
            return "Add at least one action.";
        }

        foreach (var action in rule.Actions)
        {
            if (ValidateAction(action) is { } error)
            {
                return $"{action.Kind}: {error}";
            }
        }

        return null;
    }

    /// <summary>Parses a status code for Respond actions: 100-599, empty means 200. Null when invalid.</summary>
    public static int? ParseStatus(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 200;
        }

        return int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code is >= 100 and <= 599 ? code : null;
    }

    private static string? ValidateAction(RuleAction action) => action.Kind switch
    {
        RuleActionKind.Respond or RuleActionKind.RespondWithFile when ParseStatus(action.Name) is null => "the status code must be a number from 100 to 599.",
        RuleActionKind.RespondWithFile when string.IsNullOrWhiteSpace(action.Value) => "choose a file.",
        RuleActionKind.Delay when !int.TryParse(action.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) || ms > 600_000 => "the delay must be a number of milliseconds up to 600000.",
        RuleActionKind.RewriteUrl when string.IsNullOrEmpty(action.Name) => "enter the text to replace.",
        RuleActionKind.SetRequestHeader or RuleActionKind.SetResponseHeader or RuleActionKind.RemoveRequestHeader or RuleActionKind.RemoveResponseHeader
            when !IsToken(action.Name) => "enter a valid header name.",
        RuleActionKind.SetRequestHeader or RuleActionKind.SetResponseHeader
            when action.Value.Contains('\r', StringComparison.Ordinal) || action.Value.Contains('\n', StringComparison.Ordinal) => "a header value cannot contain line breaks.",
        _ => null,
    };

    private static bool IsToken(string name) =>
        name.Length > 0 && name.All(c => c is > ' ' and < (char)127 && !"()<>@,;:\\\"/[]?={}".Contains(c, StringComparison.Ordinal));

    private static CompiledRule Compile(TrafficRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        Func<string, bool> matcher;
        if (!rule.Enabled || Validate(rule) is not null)
        {
            matcher = static _ => false;
        }
        else
        {
            var pattern = rule.Pattern.Trim();
            switch (rule.MatchKind)
            {
                case RuleMatchKind.Exact:
                    matcher = url => string.Equals(url, pattern, StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(url.TrimEnd('/'), pattern.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
                    break;
                case RuleMatchKind.Contains:
                    matcher = url => url.Contains(pattern, StringComparison.OrdinalIgnoreCase);
                    break;
                default:
                    var regex = rule.MatchKind == RuleMatchKind.Regex
                        ? new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout)
                        : new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, RegexTimeout);
                    matcher = url =>
                    {
                        try
                        {
                            return regex.IsMatch(url);
                        }
                        catch (RegexMatchTimeoutException)
                        {
                            return false;
                        }
                    };
                    break;
            }
        }

        return new CompiledRule(rule, matcher);
    }

    private sealed record CompiledRule(TrafficRule Rule, Func<string, bool> UrlMatcher)
    {
        public bool IsMatch(string method, string url) =>
            (string.IsNullOrWhiteSpace(Rule.Method) || string.Equals(Rule.Method.Trim(), method, StringComparison.OrdinalIgnoreCase))
            && UrlMatcher(url);
    }
}
