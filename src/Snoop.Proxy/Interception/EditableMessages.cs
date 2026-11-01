using Snoop.Core.Http;

namespace Snoop.Proxy.Interception;

/// <summary>
/// A request held by Snoop before it goes to the server. Rules and breakpoint edits change it in
/// place. Hop-by-hop headers are not included; Content-Length is set from <see cref="Body"/> when sent.
/// </summary>
public sealed class EditableRequest(string method, Uri url, HttpHeaderList headers, byte[]? body)
{
    public string Method { get; set; } = method ?? throw new ArgumentNullException(nameof(method));

    /// <summary>Absolute http or https URL.</summary>
    public Uri Url { get; set; } = url ?? throw new ArgumentNullException(nameof(url));

    public HttpHeaderList Headers { get; set; } = headers ?? throw new ArgumentNullException(nameof(headers));

    /// <summary>The whole body, or null for none.</summary>
    public byte[]? Body { get; set; } = body;

    /// <summary>A copy, for telling later whether anything changed.</summary>
    public EditableRequest Clone() => new(Method, Url, Headers.Clone(), Body is null ? null : (byte[])Body.Clone());

    /// <summary>True when method, URL, headers or body differ from <paramref name="other"/>.</summary>
    public bool DiffersFrom(EditableRequest other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return !string.Equals(Method, other.Method, StringComparison.Ordinal)
               || Url != other.Url
               || !string.Equals(Url.OriginalString, other.Url.OriginalString, StringComparison.Ordinal)
               || !string.Equals(Headers.ToString(), other.Headers.ToString(), StringComparison.Ordinal)
               || !BodiesEqual(Body, other.Body);
    }

    internal static bool BodiesEqual(byte[]? a, byte[]? b) =>
        (a is null || a.Length == 0) ? (b is null || b.Length == 0) : b is not null && a.AsSpan().SequenceEqual(b);
}

/// <summary>
/// A response held by Snoop before it goes to the app: from the server, or made by a rule.
/// Content-Length is set from <see cref="Body"/> when sent.
/// </summary>
public sealed class EditableResponse(int statusCode, string reasonPhrase, HttpHeaderList headers, byte[] body)
{
    public int StatusCode { get; set; } = statusCode;

    public string ReasonPhrase { get; set; } = reasonPhrase ?? string.Empty;

    public HttpHeaderList Headers { get; set; } = headers ?? throw new ArgumentNullException(nameof(headers));

    public byte[] Body { get; set; } = body ?? [];

    public EditableResponse Clone() => new(StatusCode, ReasonPhrase, Headers.Clone(), (byte[])Body.Clone());

    public bool DiffersFrom(EditableResponse other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return StatusCode != other.StatusCode
               || !string.Equals(ReasonPhrase, other.ReasonPhrase, StringComparison.Ordinal)
               || !string.Equals(Headers.ToString(), other.Headers.ToString(), StringComparison.Ordinal)
               || !EditableRequest.BodiesEqual(Body, other.Body);
    }
}
