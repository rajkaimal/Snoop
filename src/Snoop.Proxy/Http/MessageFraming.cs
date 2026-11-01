using System.Globalization;
using Snoop.Core.Http;

namespace Snoop.Proxy.Http;

/// <summary>How the length of a message body is determined (RFC 9112 section 6.3).</summary>
public enum BodyFraming
{
    None,
    ContentLength,
    Chunked,
    UntilClose,
}

/// <summary>The framing of one message body.</summary>
public readonly record struct BodyLength(BodyFraming Framing, long Length = 0)
{
    public static BodyLength Empty => new(BodyFraming.None);

    public bool HasBody => Framing != BodyFraming.None && !(Framing == BodyFraming.ContentLength && Length == 0);

    /// <summary>Creates a stream that reads exactly this body from <paramref name="source"/>.</summary>
    public ReadOnlyForwardStream CreateReader(BufferedConnectionStream source) =>
        Framing switch
        {
            BodyFraming.ContentLength when Length > 0 => new ContentLengthReadStream(source, Length),
            BodyFraming.Chunked => new ChunkedReadStream(source),
            BodyFraming.UntilClose => new ReadToEndStream(source),
            _ => EmptyBodyStream.Instance,
        };
}

/// <summary>Works out body framing from headers, rejecting ambiguous messages.</summary>
public static class MessageFraming
{
    /// <summary>Framing of a request body. Requests never use read-until-close.</summary>
    /// <exception cref="HttpParseException">Ambiguous or unsupported framing (possible request smuggling).</exception>
    public static BodyLength ForRequest(HttpHeaderList headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (headers.Contains("Transfer-Encoding"))
        {
            if (headers.Contains("Content-Length"))
            {
                throw new HttpParseException("Request has both Transfer-Encoding and Content-Length.");
            }

            if (!IsChunkedFinal(headers))
            {
                throw new HttpParseException("Request Transfer-Encoding must end with chunked.", 501);
            }

            return new BodyLength(BodyFraming.Chunked);
        }

        var length = ParseContentLength(headers);
        return length is > 0 ? new BodyLength(BodyFraming.ContentLength, length.Value) : BodyLength.Empty;
    }

    /// <summary>Framing of a response body to a request with <paramref name="requestMethod"/>.</summary>
    public static BodyLength ForResponse(string requestMethod, int statusCode, HttpHeaderList headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (!ResponseMayHaveBody(requestMethod, statusCode))
        {
            return BodyLength.Empty;
        }

        if (headers.Contains("Transfer-Encoding"))
        {
            return IsChunkedFinal(headers) ? new BodyLength(BodyFraming.Chunked) : new BodyLength(BodyFraming.UntilClose);
        }

        var length = ParseContentLength(headers);
        return length is null ? new BodyLength(BodyFraming.UntilClose) : new BodyLength(BodyFraming.ContentLength, length.Value);
    }

    /// <summary>HEAD responses, 1xx, 204 and 304 never carry a body.</summary>
    public static bool ResponseMayHaveBody(string requestMethod, int statusCode) =>
        !string.Equals(requestMethod, "HEAD", StringComparison.OrdinalIgnoreCase)
        && statusCode >= 200
        && statusCode != 204
        && statusCode != 304;

    /// <summary>
    /// Parses Content-Length. Repeated identical values are accepted (RFC 9110 section 8.6);
    /// differing or invalid values are rejected.
    /// </summary>
    public static long? ParseContentLength(HttpHeaderList headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        long? result = null;
        foreach (var item in headers.GetListItems("Content-Length"))
        {
            if (!long.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                throw new HttpParseException($"Invalid Content-Length '{item}'.");
            }

            if (result is not null && result != value)
            {
                throw new HttpParseException("Conflicting Content-Length values.");
            }

            result = value;
        }

        if (result is null && headers.Contains("Content-Length"))
        {
            throw new HttpParseException("Empty Content-Length.");
        }

        return result;
    }

    private static bool IsChunkedFinal(HttpHeaderList headers)
    {
        var codings = headers.GetListItems("Transfer-Encoding");
        return codings.Count > 0 && string.Equals(codings[^1], "chunked", StringComparison.OrdinalIgnoreCase);
    }
}
