using System.Net;
using System.Net.Http.Headers;
using Snoop.Core.Http;
using Snoop.Proxy.Http;

namespace Snoop.Proxy.Upstream;

/// <summary>
/// Sends client requests to the real server. Built on <see cref="SocketsHttpHandler"/> so it
/// pools connections and speaks HTTP/2 to servers that support it. Never follows redirects,
/// never decompresses, never adds cookies or tracing headers: the server sees what the client sent.
/// </summary>
public sealed class UpstreamHttpClient : IDisposable
{
    private static readonly HashSet<string> ContentHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Allow",
        "Content-Disposition",
        "Content-Encoding",
        "Content-Language",
        "Content-Location",
        "Content-MD5",
        "Content-Range",
        "Content-Type",
        "Expires",
        "Last-Modified",
    };

    private readonly HttpMessageInvoker _invoker;

    public UpstreamHttpClient(ProxyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = options.UpstreamProxy is not null,
            Proxy = options.UpstreamProxy is null ? null : new WebProxy(options.UpstreamProxy),
            ConnectTimeout = options.ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
            MaxResponseHeadersLength = Math.Max(1, options.MaxHeadBytes / 1024),
            EnableMultipleHttp2Connections = true,
            ActivityHeadersPropagator = null, // Do not inject traceparent headers.
        };

        if (options.UpstreamCertificateValidation is not null)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = options.UpstreamCertificateValidation;
        }

        // HttpMessageInvoker, unlike HttpClient, adds no default headers and has no timeout;
        // cancellation comes from the connection.
        _invoker = new HttpMessageInvoker(handler, disposeHandler: true);
    }

    /// <summary>
    /// Sends the request and returns as soon as response headers arrive. The caller owns the
    /// response and must dispose it (see <see cref="DisposeExchange"/>) after copying the body.
    /// The request message is not disposed here because its body may still be streaming.
    /// </summary>
    /// <param name="method">Request method.</param>
    /// <param name="url">Absolute URL of the target.</param>
    /// <param name="headers">Client headers; hop-by-hop fields must already be removed.</param>
    /// <param name="body">Request body stream, or null for none.</param>
    /// <param name="bodyLength">Body length when known (Content-Length), else null for chunked.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    public async Task<HttpResponseMessage> SendAsync(
        string method,
        Uri url,
        HttpHeaderList headers,
        Stream? body,
        long? bodyLength,
        CancellationToken cancellationToken)
    {
        var request = BuildRequest(method, url, headers, body, bodyLength);
        try
        {
            return await _invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    /// <summary>Disposes a response and the request that produced it.</summary>
    public static void DisposeExchange(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var request = response.RequestMessage;
        response.Dispose();
        request?.Dispose();
    }

    public void Dispose() => _invoker.Dispose();

    internal static HttpRequestMessage BuildRequest(string method, Uri url, HttpHeaderList headers, Stream? body, long? bodyLength)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        if (body is not null)
        {
            request.Content = new StreamContent(body, 16 * 1024);
            if (bodyLength is not null)
            {
                request.Content.Headers.ContentLength = bodyLength;
            }
        }

        foreach (var header in headers)
        {
            if (string.Equals(header.Name, "Host", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Host = header.Value;
            }
            else if (string.Equals(header.Name, "Content-Length", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(header.Name, "Expect", StringComparison.OrdinalIgnoreCase)
                     || HopByHopHeaders.IsStandard(header.Name))
            {
                // Framing and connection management are handled by the upstream handler.
            }
            else if (ContentHeaderNames.Contains(header.Name))
            {
                request.Content ??= new ByteArrayContent([]);
                request.Content.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }

        return request;
    }

    /// <summary>Collects response and content headers in a single ordered list.</summary>
    internal static HttpHeaderList CollectResponseHeaders(HttpResponseMessage response)
    {
        var list = new HttpHeaderList();
        AddAll(list, response.Headers);
        AddAll(list, response.Content.Headers);
        return list;
    }

    private static void AddAll(HttpHeaderList list, HttpHeaders headers)
    {
        foreach (var header in headers.NonValidated)
        {
            foreach (var value in header.Value)
            {
                list.Add(header.Key, value);
            }
        }
    }
}
