using Snoop.Core.Http;

namespace Snoop.Proxy.Http;

/// <summary>
/// Headers that describe one connection rather than the message, and so must not
/// be forwarded by a proxy (RFC 9110 section 7.6.1).
/// </summary>
public static class HopByHopHeaders
{
    private static readonly HashSet<string> Standard = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Connection",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
    };

    public static bool IsStandard(string name) => Standard.Contains(name);

    /// <summary>
    /// Returns a copy of <paramref name="headers"/> without hop-by-hop fields, including any
    /// extra names listed in the Connection header.
    /// </summary>
    public static HttpHeaderList Strip(HttpHeaderList headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var listed = new HashSet<string>(headers.GetListItems("Connection"), StringComparer.OrdinalIgnoreCase);
        return new HttpHeaderList(headers.Where(h => !Standard.Contains(h.Name) && !listed.Contains(h.Name)));
    }
}
