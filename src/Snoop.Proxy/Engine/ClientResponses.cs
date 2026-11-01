using System.Globalization;
using System.Net;
using System.Text;
using Snoop.Core.Http;
using Snoop.Proxy.Http;

namespace Snoop.Proxy.Engine;

/// <summary>Responses the proxy writes to the client itself (errors, local pages).</summary>
internal static class ClientResponses
{
    /// <summary>Writes a complete response. Never throws for a client that has gone away.</summary>
    public static async Task WriteAsync(
        Stream client,
        int statusCode,
        string contentType,
        byte[] body,
        bool keepAlive,
        CancellationToken cancellationToken,
        IEnumerable<HttpHeader>? extraHeaders = null)
    {
        var headers = new HttpHeaderList();
        headers.Add("Content-Type", contentType);
        headers.Add("Content-Length", body.Length.ToString(CultureInfo.InvariantCulture));
        headers.Add("Cache-Control", "no-store");
        foreach (var header in extraHeaders ?? [])
        {
            headers.Add(header);
        }

        headers.Add("Connection", keepAlive ? "keep-alive" : "close");
        var head = HttpHeadWriter.SerializeResponse("HTTP/1.1", statusCode, ReasonPhrases.Get(statusCode), headers);
        try
        {
            await client.WriteAsync(head, cancellationToken).ConfigureAwait(false);
            await client.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await client.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The client is gone; nothing to tell it.
        }
    }

    /// <summary>Writes a plain-text error that also says it came from Snoop, then expects the caller to close.</summary>
    public static Task WriteErrorAsync(Stream client, int statusCode, string message, CancellationToken cancellationToken)
    {
        var text = $"Snoop: {ReasonPhrases.Get(statusCode)}\r\n\r\n{message}\r\n";
        return WriteAsync(
            client,
            statusCode,
            "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes(text),
            keepAlive: false,
            cancellationToken,
            [new HttpHeader("X-Snoop-Error", "true")]);
    }

    public static string FormatVersion(Version version) =>
        version.Major switch
        {
            2 => "HTTP/2",
            3 => "HTTP/3",
            _ => $"HTTP/{version.Major}.{version.Minor}",
        };
}

/// <summary>Standard reason phrases for status lines.</summary>
internal static class ReasonPhrases
{
    public static string Get(int statusCode) =>
        statusCode switch
        {
            100 => "Continue",
            101 => "Switching Protocols",
            200 => "OK",
            201 => "Created",
            202 => "Accepted",
            204 => "No Content",
            206 => "Partial Content",
            301 => "Moved Permanently",
            302 => "Found",
            303 => "See Other",
            304 => "Not Modified",
            307 => "Temporary Redirect",
            308 => "Permanent Redirect",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            408 => "Request Timeout",
            409 => "Conflict",
            410 => "Gone",
            411 => "Length Required",
            413 => "Content Too Large",
            414 => "URI Too Long",
            415 => "Unsupported Media Type",
            429 => "Too Many Requests",
            431 => "Request Header Fields Too Large",
            500 => "Internal Server Error",
            501 => "Not Implemented",
            502 => "Bad Gateway",
            503 => "Service Unavailable",
            504 => "Gateway Timeout",
            505 => "HTTP Version Not Supported",
            _ => Enum.IsDefined(typeof(HttpStatusCode), statusCode) ? SplitWords(((HttpStatusCode)statusCode).ToString()) : "Unknown",
        };

    private static string SplitWords(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(name[i]);
        }

        return builder.ToString();
    }
}
