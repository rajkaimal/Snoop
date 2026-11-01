namespace Snoop.Proxy.Http;

/// <summary>
/// The peer sent something that is not valid HTTP/1.x. Carries the status code
/// the proxy should answer with when the bad message came from the client.
/// </summary>
public sealed class HttpParseException : Exception
{
    public HttpParseException()
        : this("Malformed HTTP message.")
    {
    }

    public HttpParseException(string message)
        : this(message, 400)
    {
    }

    public HttpParseException(string message, Exception innerException)
        : base(message, innerException) => StatusCode = 400;

    public HttpParseException(string message, int statusCode)
        : base(message) => StatusCode = statusCode;

    /// <summary>400 for malformed input, 431 for oversized headers, 501 for unsupported features.</summary>
    public int StatusCode { get; }
}
