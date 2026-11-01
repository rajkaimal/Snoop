using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Snoop.Core.Http;
using Snoop.Core.Sessions;
using Snoop.Proxy;

namespace Snoop.Hosting.Composer;

/// <summary>A request written in the Composer.</summary>
/// <param name="Method">HTTP method, e.g. GET.</param>
/// <param name="Url">Absolute http or https URL.</param>
/// <param name="Headers">Headers to send. Host and Content-Length are worked out when missing.</param>
/// <param name="Body">Body bytes, or null for none.</param>
public sealed record ComposedRequest(string Method, Uri Url, HttpHeaderList Headers, byte[]? Body);

/// <summary>What happened to a Composer request.</summary>
/// <param name="Session">The recorded session, when the proxy saw the request (it does not for HTTPS while decryption is off).</param>
/// <param name="StatusCode">The status code the Composer received, or null when it got no response.</param>
/// <param name="ReasonPhrase">The reason phrase received.</param>
/// <param name="Error">Why there was no response, or null.</param>
public sealed record ComposerResult(Session? Session, int? StatusCode, string? ReasonPhrase, string? Error);

/// <summary>Sends Composer requests through Snoop's own proxy, so they are captured like any other traffic.</summary>
internal static class ComposerClient
{
    private static readonly HashSet<string> ContentHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Allow", "Content-Disposition", "Content-Encoding", "Content-Language", "Content-Location",
        "Content-MD5", "Content-Range", "Content-Type", "Expires", "Last-Modified",
    };

    /// <summary>How long to wait for the proxy to report the finished session after the response arrived.</summary>
    internal static TimeSpan SessionWait { get; set; } = TimeSpan.FromSeconds(3);

    public static async Task<ComposerResult> SendAsync(IPEndPoint proxy, X509Certificate2? snoopRoot, SessionStore store, ComposedRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var id = Guid.NewGuid().ToString("N");
        var finished = new TaskCompletionSource<Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, SessionChangedEventArgs e)
        {
            if (e.Change == SessionChange.Finished && e.Session.ComposerId == id)
            {
                finished.TrySetResult(e.Session);
            }
        }

        store.SessionChanged += OnChanged;
        try
        {
            var host = proxy.Address.Equals(IPAddress.Any) || proxy.Address.Equals(IPAddress.IPv6Any) ? IPAddress.Loopback : proxy.Address;
            using var handler = new SocketsHttpHandler
            {
                Proxy = new FixedProxy(new Uri($"http://{new IPEndPoint(host, proxy.Port)}")),
                UseProxy = true,
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,
                ActivityHeadersPropagator = null,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.Zero,
            };
            handler.SslOptions.RemoteCertificateValidationCallback = TrustSnoopOrSystem(snoopRoot);
            using var invoker = new HttpMessageInvoker(handler);
            using var message = BuildMessage(request, id);

            HttpResponseMessage response;
            try
            {
                response = await invoker.SendAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                return new ComposerResult(await WaitForSessionAsync(finished.Task).ConfigureAwait(false), null, null, ex.InnerException?.Message ?? ex.Message);
            }

            using (response)
            {
                // Read the body so the proxy finishes the exchange.
                try
                {
                    await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    return new ComposerResult(await WaitForSessionAsync(finished.Task).ConfigureAwait(false), (int)response.StatusCode, response.ReasonPhrase, ex.Message);
                }

                return new ComposerResult(await WaitForSessionAsync(finished.Task).ConfigureAwait(false), (int)response.StatusCode, response.ReasonPhrase, null);
            }
        }
        finally
        {
            store.SessionChanged -= OnChanged;
        }
    }

    internal static HttpRequestMessage BuildMessage(ComposedRequest request, string composerId)
    {
        var message = new HttpRequestMessage(new HttpMethod(request.Method.Trim().ToUpperInvariant()), request.Url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        if (request.Body is { Length: > 0 } body)
        {
            message.Content = new ByteArrayContent(body);
        }

        foreach (var header in request.Headers)
        {
            if (string.Equals(header.Name, "Host", StringComparison.OrdinalIgnoreCase))
            {
                message.Headers.Host = header.Value;
            }
            else if (string.Equals(header.Name, "Content-Length", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(header.Name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(header.Name, "Connection", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(header.Name, SnoopHeaders.ComposerId, StringComparison.OrdinalIgnoreCase))
            {
                // Framing and connection handling are worked out by the client.
            }
            else if (ContentHeaderNames.Contains(header.Name))
            {
                message.Content ??= new ByteArrayContent([]);
                message.Content.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
            else
            {
                message.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }

        message.Headers.TryAddWithoutValidation(SnoopHeaders.ComposerId, composerId);
        return message;
    }

    private static void ValidateRequest(ComposedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Url.IsAbsoluteUri || (request.Url.Scheme != Uri.UriSchemeHttp && request.Url.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("The URL must start with http:// or https://.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Method) || request.Method.Any(c => c <= ' ' || c >= 127))
        {
            throw new ArgumentException("Enter a method such as GET or POST.", nameof(request));
        }
    }

    private static async Task<Session?> WaitForSessionAsync(Task<Session> finished) =>
        await Task.WhenAny(finished, Task.Delay(SessionWait)).ConfigureAwait(false) == finished ? await finished.ConfigureAwait(false) : null;

    /// <summary>Accepts certificates that chain to Snoop's root (decrypted tunnels) or that Windows trusts (blind tunnels).</summary>
    private static RemoteCertificateValidationCallback TrustSnoopOrSystem(X509Certificate2? snoopRoot) =>
        (_, certificate, _, errors) =>
        {
            if (errors == SslPolicyErrors.None)
            {
                return true;
            }

            if (snoopRoot is null || certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            {
                return false;
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(snoopRoot);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            return chain.Build(leaf);
        };

    /// <summary>Sends everything to Snoop, including localhost (which WebProxy would bypass).</summary>
    private sealed class FixedProxy(Uri proxy) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => proxy;

        public bool IsBypassed(Uri host) => false;
    }
}
