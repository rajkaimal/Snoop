namespace Snoop.Proxy.Util;

/// <summary>
/// Matches host names against a list of patterns: <c>example.com</c> (exact),
/// <c>*.example.com</c> (any subdomain, not the apex) or <c>*</c> (everything).
/// Comparison ignores case and a trailing dot.
/// </summary>
public sealed class HostMatcher
{
    private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _suffixes = [];
    private readonly bool _matchAll;

    public HostMatcher(IEnumerable<string>? patterns)
    {
        foreach (var raw in patterns ?? [])
        {
            var pattern = raw.Trim().TrimEnd('.');
            if (pattern.Length == 0)
            {
                continue;
            }

            if (pattern == "*")
            {
                _matchAll = true;
            }
            else if (pattern.StartsWith("*.", StringComparison.Ordinal) && pattern.Length > 2)
            {
                _suffixes.Add(pattern[1..]); // ".example.com"
            }
            else
            {
                _exact.Add(pattern);
            }
        }
    }

    public static HostMatcher Empty { get; } = new([]);

    public bool IsMatch(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        if (_matchAll)
        {
            return true;
        }

        var normalized = host.TrimEnd('.');
        return _exact.Contains(normalized)
            || _suffixes.Exists(suffix => normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}
