using System.Net.Security;
using System.Net.Sockets;

namespace Snoop.Proxy.Upstream;

/// <summary>
/// Opens plain TCP (optionally TLS) connections to servers, for traffic the HTTP client
/// cannot carry: blind CONNECT tunnels and protocol upgrades such as WebSocket.
/// </summary>
public sealed class RawUpstreamConnector(ProxyOptions options)
{
    private readonly ProxyOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Connects to <paramref name="host"/>:<paramref name="port"/>.</summary>
    /// <param name="host">Server host name or IP address.</param>
    /// <param name="port">Server port.</param>
    /// <param name="useTls">Wrap the connection in TLS (ALPN http/1.1).</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    public async Task<Stream> ConnectAsync(string host, int port, bool useTls, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ConnectTimeout);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TimeoutException($"Connecting to {host}:{port} timed out.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        Stream stream = new NetworkStream(socket, ownsSocket: true);
        if (!useTls)
        {
            return stream;
        }

        var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                    RemoteCertificateValidationCallback = _options.UpstreamCertificateValidation,
                },
                timeout.Token).ConfigureAwait(false);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
