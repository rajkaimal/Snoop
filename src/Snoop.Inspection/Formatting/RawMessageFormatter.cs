using System.Globalization;
using System.Text;
using Snoop.Core.Bodies;
using Snoop.Core.Http;
using Snoop.Core.Sessions;
using Snoop.Inspection.Bodies;

namespace Snoop.Inspection.Formatting;

/// <summary>Renders a request or response as it looked on the wire (start line, headers, body).</summary>
public static class RawMessageFormatter
{
    /// <summary>Bodies longer than this are cut off in the raw view.</summary>
    public const int MaxBodyChars = 256 * 1024;

    public static string FormatRequest(RequestInfo request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = request.Method == "CONNECT"
            ? request.Url.GetComponents(UriComponents.HostAndPort, UriFormat.UriEscaped)
            : request.Url.PathAndQuery;
        var builder = new StringBuilder();
        builder.Append(request.Method).Append(' ').Append(target).Append(' ').Append(request.HttpVersion).Append("\r\n");
        AppendHeadersAndBody(builder, request.Headers, request.Body);
        return builder.ToString();
    }

    public static string FormatResponse(ResponseInfo response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var builder = new StringBuilder();
        builder.Append(response.HttpVersion).Append(' ')
            .Append(response.StatusCode.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(response.ReasonPhrase).Append("\r\n");
        AppendHeadersAndBody(builder, response.Headers, response.Body);
        return builder.ToString();
    }

    private static void AppendHeadersAndBody(StringBuilder builder, HttpHeaderList headers, CapturedBody? body)
    {
        builder.Append(headers.ToString()).Append("\r\n");
        if (body is null || body.TotalBytes == 0)
        {
            return;
        }

        var bytes = body.ToArray(MaxBodyChars);
        var encoded = headers.Contains("Content-Encoding")
                      && !string.Equals(headers.Get("Content-Encoding"), "identity", StringComparison.OrdinalIgnoreCase);
        if (encoded || !ContentSniffer.LooksLikeText(bytes))
        {
            builder.Append(CultureInfo.InvariantCulture, $"[{body.TotalBytes:N0} bytes of {(encoded ? "encoded" : "binary")} data; see the Body tab]");
            return;
        }

        builder.Append(TextDecoder.Decode(bytes, ContentSniffer.Charset(headers.Get("Content-Type"))));
        if (body.TotalBytes > bytes.Length)
        {
            builder.Append(CultureInfo.InvariantCulture, $"\r\n... {body.TotalBytes - bytes.Length:N0} more bytes not shown");
        }
    }
}
