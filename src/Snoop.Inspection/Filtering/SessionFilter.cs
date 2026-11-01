using System.Globalization;
using System.Runtime.CompilerServices;
using Snoop.Core.Bodies;
using Snoop.Core.Http;
using Snoop.Core.Sessions;
using Snoop.Inspection.Bodies;
using Snoop.Inspection.Formatting;

namespace Snoop.Inspection.Filtering;

/// <summary>
/// Which sessions to show. Quick toggles plus free-text search across URL, method, status,
/// process name, header values and (optionally) text bodies. An empty filter shows everything.
/// </summary>
public sealed record SessionFilter
{
    public static SessionFilter None { get; } = new();

    /// <summary>Free text. Every word must match somewhere (URL, method, status, process, header values).</summary>
    public string SearchText { get; init; } = string.Empty;

    /// <summary>Hide CONNECT tunnel rows.</summary>
    public bool HideTunnels { get; init; }

    /// <summary>Hide images, fonts and stylesheets.</summary>
    public bool HideStatic { get; init; }

    /// <summary>Show only failures and 4xx/5xx responses.</summary>
    public bool OnlyErrors { get; init; }

    /// <summary>Show only sessions from processes whose name contains this text.</summary>
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>Show only sessions to this host (exact, ignoring case). Empty shows every host.</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>Let the search match text in request and response bodies too.</summary>
    public bool SearchBodies { get; init; }

    /// <summary>Bodies larger than this (after decoding) are not searched.</summary>
    public const int MaxSearchedBodyBytes = 2 * 1024 * 1024;

    private static readonly ConditionalWeakTable<CapturedBody, SearchableBody> BodyText = new();

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(SearchText) && !HideTunnels && !HideStatic && !OnlyErrors
        && string.IsNullOrWhiteSpace(ProcessName) && string.IsNullOrWhiteSpace(Host);

    /// <summary>The words of <paramref name="searchText"/>, as the search uses them (for highlighting).</summary>
    public static IReadOnlyList<string> SearchWords(string? searchText) =>
        (searchText ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool Matches(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (HideTunnels && session.Kind == SessionKind.Connect)
        {
            return false;
        }

        if (OnlyErrors && !IsError(session))
        {
            return false;
        }

        if (HideStatic && IsStatic(session))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(ProcessName)
            && !(session.ClientProcess?.Name.Contains(ProcessName.Trim(), StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Host)
            && !string.Equals(session.Request.Url.Host, Host.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return MatchesSearch(session);
    }

    public static bool IsError(Session session) =>
        session.State is SessionState.Failed or SessionState.Aborted || session.Response?.StatusCode >= 400;

    public static bool IsStatic(Session session)
    {
        var kind = ContentSniffer.FromContentType(session.Response?.Headers.Get("Content-Type"));
        return kind is ContentKind.Image or ContentKind.Font or ContentKind.Css;
    }

    private bool MatchesSearch(Session session)
    {
        foreach (var word in SearchWords(SearchText))
        {
            if (!MatchesWord(session, word))
            {
                return false;
            }
        }

        return true;
    }

    private bool MatchesWord(Session session, string word)
    {
        static bool Has(string? text, string word) => text is not null && text.Contains(word, StringComparison.OrdinalIgnoreCase);

        return Has(session.Request.Url.ToString(), word)
               || Has(session.Request.Method, word)
               || Has(session.Response?.StatusCode.ToString(CultureInfo.InvariantCulture), word)
               || Has(session.ClientProcess?.Name, word)
               || Has(session.Error, word)
               || session.Request.Headers.Any(h => Has(h.Value, word))
               || (session.Response?.Headers.Any(h => Has(h.Value, word)) ?? false)
               || (SearchBodies && (Has(TextOf(session.Request.Body, session.Request.Headers), word)
                                    || (session.Response is { } response && Has(TextOf(response.Body, response.Headers), word))));
    }

    /// <summary>A body's decoded text, cached per body until it grows; null for binary or very large bodies.</summary>
    private static string? TextOf(CapturedBody? body, HttpHeaderList headers)
    {
        if (body is null || body.TotalBytes == 0 || body.TotalBytes > MaxSearchedBodyBytes)
        {
            return null;
        }

        var total = body.TotalBytes;
        if (BodyText.TryGetValue(body, out var cached) && cached.TotalBytes == total)
        {
            return cached.Text;
        }

        string? text = null;
        try
        {
            var decoded = ContentDecoder.Decode(body.ToArray(), headers, MaxSearchedBodyBytes);
            if (ContentSniffer.LooksLikeText(decoded.Bytes) && !ContentSniffer.IsImage(decoded.Bytes))
            {
                text = TextDecoder.Decode(decoded.Bytes, ContentSniffer.Charset(headers.Get("Content-Type")));
            }
        }
        catch (ObjectDisposedException)
        {
            // The session was removed while being searched.
            return null;
        }

        BodyText.AddOrUpdate(body, new SearchableBody(total, text));
        return text;
    }

    private sealed record SearchableBody(long TotalBytes, string? Text);
}
