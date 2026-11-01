using Snoop.Core.Http;

namespace Snoop.Proxy.Http;

/// <summary>The start line and headers of an HTTP/1.x request.</summary>
/// <param name="Method">Request method, e.g. GET or CONNECT.</param>
/// <param name="Target">Request target as sent: absolute-form, origin-form, authority-form or "*".</param>
/// <param name="Version">"HTTP/1.1" or "HTTP/1.0".</param>
/// <param name="Headers">Header fields in wire order.</param>
public sealed record RequestHead(string Method, string Target, string Version, HttpHeaderList Headers)
{
    public bool IsHttp10 => Version == "HTTP/1.0";

    /// <summary>
    /// Whether the client wants the connection kept open after this exchange
    /// (HTTP/1.1 default unless "Connection: close"; HTTP/1.0 only with "keep-alive").
    /// </summary>
    public bool WantsKeepAlive =>
        IsHttp10
            ? Headers.HasToken("Connection", "keep-alive") || Headers.HasToken("Proxy-Connection", "keep-alive")
            : !Headers.HasToken("Connection", "close") && !Headers.HasToken("Proxy-Connection", "close");
}

/// <summary>The status line and headers of an HTTP/1.x response.</summary>
public sealed record ResponseHead(string Version, int StatusCode, string ReasonPhrase, HttpHeaderList Headers);
