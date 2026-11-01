using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Snoop.Core.Bodies;
using Snoop.Core.Diagnostics;
using Snoop.Core.Http;
using Snoop.Core.Sessions;
using Snoop.Proxy.Http;
using Snoop.Proxy.Interception;
using Snoop.Proxy.Upstream;
using Snoop.Proxy.WebSockets;

namespace Snoop.Proxy.Engine;

/// <summary>
/// Serves one client TCP connection: a sequence of proxied HTTP requests, or a CONNECT
/// tunnel that is decrypted (and then serves requests inside it) or relayed blind.
/// </summary>
internal sealed partial class ClientConnection
{
    private const byte TlsHandshakeRecord = 0x16;
    private const int CopyBufferSize = 32 * 1024;

    /// <summary>A decrypted tunnel closed this soon without a request counts as a certificate rejection.</summary>
    private static readonly TimeSpan QuickCloseWindow = TimeSpan.FromSeconds(2);

    private readonly ProxyContext _context;
    private readonly Socket _socket;
    private readonly LocalPages _localPages;
    private readonly ConnectionEndpoints _endpoints;
    private ClientProcessInfo? _process;
    private bool _processResolved;

    public ClientConnection(ProxyContext context, Socket socket)
    {
        _context = context;
        _socket = socket;
        _localPages = new LocalPages(context);
        _endpoints = new ConnectionEndpoints((IPEndPoint)socket.RemoteEndPoint!, (IPEndPoint)socket.LocalEndPoint!);
    }

    private ISnoopLogger Log => _context.Logger;

    private ProxyOptions Options => _context.Options;

    /// <summary>The local process behind this connection, looked up once on first use.</summary>
    private ClientProcessInfo? Process
    {
        get
        {
            if (!_processResolved)
            {
                _processResolved = true;
                try
                {
                    _process = _context.ProcessResolver.Resolve(_endpoints.Client, _endpoints.Proxy);
                }
#pragma warning disable CA1031 // Process lookup is best-effort.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    Log.Debug($"Process lookup failed for {_endpoints.Client}: {ex.Message}");
                }
            }

            return _process;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var network = new NetworkStream(_socket, ownsSocket: true);
        await using (network.ConfigureAwait(false))
        {
            var client = new BufferedConnectionStream(network, leaveOpen: true);
            try
            {
                await ServeAsync(client, tunnel: null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsConnectionError(ex) || ex is OperationCanceledException)
            {
                Log.Debug($"Connection from {_endpoints.Client} ended: {ex.Message}");
            }
#pragma warning disable CA1031 // Last line of defence: one bad connection must not take down the proxy.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Log.Error($"Unexpected error on connection from {_endpoints.Client}.", ex);
            }
        }
    }

    /// <summary>Reads and handles requests until the connection should close. Returns how many requests arrived.</summary>
    private async Task<int> ServeAsync(BufferedConnectionStream client, TunnelContext? tunnel, CancellationToken cancellationToken)
    {
        var requests = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var head = await ReadRequestHeadAsync(client, cancellationToken).ConfigureAwait(false);
            if (head is null)
            {
                return requests;
            }

            requests++;
            if (tunnel is not null)
            {
                tunnel.RequestCount++;
            }

            if (string.Equals(head.Method, "CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                if (tunnel is not null)
                {
                    await ClientResponses.WriteErrorAsync(client, 400, "CONNECT is not allowed inside a tunnel.", cancellationToken).ConfigureAwait(false);
                    return requests;
                }

                await HandleConnectAsync(client, head, cancellationToken).ConfigureAwait(false);
                return requests;
            }

            if (!await HandleRequestAsync(client, head, tunnel, cancellationToken).ConfigureAwait(false))
            {
                return requests;
            }
        }

        return requests;
    }

    /// <summary>Reads the next request head. Null means close quietly (end of stream, idle, or a bad request already answered).</summary>
    private async Task<RequestHead?> ReadRequestHeadAsync(BufferedConnectionStream client, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(Options.ClientIdleTimeout);
        try
        {
            var bytes = await client.ReadHeadAsync(Options.MaxHeadBytes, idle.Token).ConfigureAwait(false);
            return bytes is null ? null : HttpHeadParser.ParseRequest(bytes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null; // Idle timeout.
        }
        catch (HttpParseException ex)
        {
            Log.Debug($"Bad request from {_endpoints.Client}: {ex.Message}");
            await ClientResponses.WriteErrorAsync(client, ex.StatusCode, ex.Message, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    // ---------------------------------------------------------------- HTTP requests

    /// <summary>Proxies one request. Returns whether the client connection may be reused.</summary>
    private async Task<bool> HandleRequestAsync(BufferedConnectionStream client, RequestHead head, TunnelContext? tunnel, CancellationToken cancellationToken)
    {
        BodyLength requestFraming;
        try
        {
            requestFraming = MessageFraming.ForRequest(head.Headers);
        }
        catch (HttpParseException ex)
        {
            await ClientResponses.WriteErrorAsync(client, ex.StatusCode, ex.Message, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var target = ResolveTarget(head, tunnel);
        if (target.IsLocal)
        {
            return await _localPages.HandleAsync(client, head, requestFraming.HasBody, cancellationToken).ConfigureAwait(false);
        }

        if (target.Url is null)
        {
            await ClientResponses.WriteErrorAsync(client, 400, target.Error ?? "Invalid request target.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        // The Composer marks its requests; the marker is Snoop's own and never leaves the proxy.
        var composerId = head.Headers.Get(SnoopHeaders.ComposerId);
        if (composerId is not null)
        {
            head.Headers.Remove(SnoopHeaders.ComposerId);
        }

        if (Options.RemoveZstdFromAcceptEncoding)
        {
            AcceptEncoding.RemoveZstd(head.Headers);
        }

        var requestBody = requestFraming.HasBody ? new CapturedBody(Options.BodyCapture) : null;
        var isUpgrade = head.Headers.Contains("Upgrade") && head.Headers.HasToken("Connection", "upgrade");
        var session = new Session(
            _context.NextSessionId(),
            isUpgrade ? SessionKind.Upgrade : SessionKind.Http,
            _endpoints,
            Process,
            new RequestInfo(head.Method, target.Url, head.Version, head.Headers) { Body = requestBody })
        {
            Tls = tunnel?.Tls,
            Flags = (tunnel?.Tls?.Decrypted == true ? SessionFlags.Decrypted : SessionFlags.None)
                    | (composerId is null ? SessionFlags.None : SessionFlags.Composed),
            ComposerId = composerId,
        };
        _context.Started(session);
        var plan = isUpgrade ? null : PlanInterception(session);

        try
        {
            if (requestFraming.HasBody && head.Headers.HasToken("Expect", "100-continue"))
            {
                await client.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                await client.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (isUpgrade)
            {
                await HandleUpgradeAsync(client, head, target.Url, session, requestFraming, cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (plan is not null)
            {
                return await ForwardInterceptedAsync(client, head, target.Url, session, requestFraming, requestBody, plan, cancellationToken).ConfigureAwait(false);
            }

            return await ForwardRequestAsync(client, head, target.Url, session, requestFraming, requestBody, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            FinishWithError(session, "The proxy was stopped.", SessionState.Aborted);
            throw;
        }
        catch (Exception ex) when (IsConnectionError(ex) || ex is HttpParseException)
        {
            FinishWithError(session, Describe(ex), SessionState.Aborted);
            return false;
        }
        finally
        {
            requestBody?.Complete();
        }
    }

    private async Task<bool> ForwardRequestAsync(
        BufferedConnectionStream client,
        RequestHead head,
        Uri url,
        Session session,
        BodyLength requestFraming,
        CapturedBody? requestBody,
        CancellationToken cancellationToken)
    {
        var bodyReader = requestBody is null
            ? null
            : new CapturingReadStream(requestFraming.CreateReader(client), requestBody);

        HttpResponseMessage response;
        try
        {
            response = await _context.Upstream.SendAsync(
                head.Method,
                url,
                HopByHopHeaders.Strip(head.Headers),
                bodyReader,
                requestFraming.Framing == BodyFraming.ContentLength ? requestFraming.Length : null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUpstreamError(ex, cancellationToken))
        {
            var message = Describe(ex);
            FinishWithError(session, message, SessionState.Failed);
            await ClientResponses.WriteErrorAsync(client, 502, $"Could not get a response from {url.Authority}: {message}", cancellationToken).ConfigureAwait(false);
            return false;
        }

        try
        {
            session.Timings.MarkRequestSent();
            return await RelayResponseAsync(client, head, session, response, bodyReader, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            UpstreamHttpClient.DisposeExchange(response);
        }
    }

    private async Task<bool> RelayResponseAsync(
        BufferedConnectionStream client,
        RequestHead head,
        Session session,
        HttpResponseMessage response,
        ReadOnlyForwardStream? requestBodyReader,
        InterceptionPlan? headerRules,
        CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var reason = string.IsNullOrEmpty(response.ReasonPhrase) ? ReasonPhrases.Get(status) : response.ReasonPhrase;
        var receivedHeaders = UpstreamHttpClient.CollectResponseHeaders(response);
        if (headerRules is not null)
        {
            ApplyResponseHeaderRules(session, receivedHeaders, headerRules);
        }

        var mayHaveBody = MessageFraming.ResponseMayHaveBody(head.Method, status);
        var responseBody = mayHaveBody ? new CapturedBody(Options.BodyCapture) : null;

        session.Response = new ResponseInfo(status, reason, ClientResponses.FormatVersion(response.Version), receivedHeaders) { Body = responseBody };
        session.State = SessionState.ReceivingResponse;
        session.Timings.MarkResponseHeaders();
        _context.Updated(session);

        // Decide framing towards the client; the upstream framing may differ (e.g. HTTP/2).
        var clientHeaders = HopByHopHeaders.Strip(receivedHeaders);
        clientHeaders.Remove("Content-Length");
        var contentLength = response.Content.Headers.ContentLength;
        var chunked = false;
        var closeDelimited = false;
        if (!mayHaveBody)
        {
            if (contentLength is not null && (status == 304 || string.Equals(head.Method, "HEAD", StringComparison.OrdinalIgnoreCase)))
            {
                clientHeaders.Add("Content-Length", contentLength.Value.ToString(CultureInfo.InvariantCulture));
            }
        }
        else if (contentLength is not null)
        {
            clientHeaders.Add("Content-Length", contentLength.Value.ToString(CultureInfo.InvariantCulture));
        }
        else if (!head.IsHttp10)
        {
            clientHeaders.Add("Transfer-Encoding", "chunked");
            chunked = true;
        }
        else
        {
            closeDelimited = true;
        }

        var keepAlive = head.WantsKeepAlive && !closeDelimited;
        if (!keepAlive)
        {
            clientHeaders.Add("Connection", "close");
        }
        else if (head.IsHttp10)
        {
            clientHeaders.Add("Connection", "keep-alive");
        }

        var responseHead = HttpHeadWriter.SerializeResponse(head.IsHttp10 ? "HTTP/1.0" : "HTTP/1.1", status, reason, clientHeaders);
        await client.WriteAsync(responseHead, cancellationToken).ConfigureAwait(false);

        try
        {
            if (mayHaveBody)
            {
                var upstreamBody = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (upstreamBody.ConfigureAwait(false))
                {
                    var copied = await CopyBodyAsync(upstreamBody, client, chunked, responseBody!, cancellationToken).ConfigureAwait(false);
                    if (contentLength is not null && copied != contentLength)
                    {
                        FinishWithError(session, $"The server sent {copied} of {contentLength} body bytes.", SessionState.Aborted);
                        return false;
                    }
                }
            }

            await client.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            responseBody?.Complete();
        }

        session.Complete();
        _context.Finished(session);

        // A request body the server never read leaves bytes on the connection: it cannot be reused.
        return keepAlive && (requestBodyReader?.IsComplete ?? true);
    }

    private static async Task<long> CopyBodyAsync(Stream source, Stream client, bool chunked, CapturedBody capture, CancellationToken cancellationToken)
    {
        var writer = chunked ? new ChunkedWriteStream(client) : client;
        var buffer = new byte[CopyBufferSize];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            capture.Append(buffer.AsSpan(0, read));
            await writer.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

            // Flush every block so streamed responses (server-sent events, long polls) reach the client promptly.
            await client.FlushAsync(cancellationToken).ConfigureAwait(false);
            total += read;
        }

        if (writer is ChunkedWriteStream chunkedWriter)
        {
            await chunkedWriter.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    // ---------------------------------------------------------------- Upgrades (WebSocket)

    /// <summary>
    /// Forwards a request that asks to switch protocols over a raw connection, then relays
    /// bytes both ways once the server agrees (101). The client connection is consumed.
    /// </summary>
    private async Task HandleUpgradeAsync(BufferedConnectionStream client, RequestHead head, Uri url, Session session, BodyLength requestFraming, CancellationToken cancellationToken)
    {
        if (requestFraming.HasBody)
        {
            FinishWithError(session, "Upgrade requests with a body are not supported.", SessionState.Failed);
            await ClientResponses.WriteErrorAsync(client, 400, "Upgrade requests with a body are not supported.", cancellationToken).ConfigureAwait(false);
            return;
        }

        Stream server;
        try
        {
            server = await _context.RawConnector.ConnectAsync(url.IdnHost, url.Port, url.Scheme == Uri.UriSchemeHttps, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUpstreamError(ex, cancellationToken))
        {
            FinishWithError(session, Describe(ex), SessionState.Failed);
            await ClientResponses.WriteErrorAsync(client, 502, $"Could not connect to {url.Authority}: {Describe(ex)}", cancellationToken).ConfigureAwait(false);
            return;
        }

        await using (server.ConfigureAwait(false))
        {
            var serverStream = new BufferedConnectionStream(server, leaveOpen: true);
            var forwardHeaders = head.Headers.Clone();
            forwardHeaders.Remove("Proxy-Connection");
            forwardHeaders.Remove("Proxy-Authorization");
            if (!forwardHeaders.Contains("Host"))
            {
                forwardHeaders.Add("Host", url.Authority);
            }

            var requestBytes = HttpHeadWriter.SerializeRequest(head.Method, url.PathAndQuery, "HTTP/1.1", forwardHeaders);
            await serverStream.WriteAsync(requestBytes, cancellationToken).ConfigureAwait(false);
            await serverStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            session.Timings.MarkRequestSent();

            var responseBytes = await serverStream.ReadHeadAsync(Options.MaxHeadBytes, cancellationToken).ConfigureAwait(false);
            if (responseBytes is null)
            {
                FinishWithError(session, "The server closed the connection without responding.", SessionState.Failed);
                await ClientResponses.WriteErrorAsync(client, 502, "The server closed the connection without responding.", cancellationToken).ConfigureAwait(false);
                return;
            }

            var responseHead = HttpHeadParser.ParseResponse(responseBytes);
            var mayHaveBody = responseHead.StatusCode != 101 && MessageFraming.ResponseMayHaveBody(head.Method, responseHead.StatusCode);
            var responseBody = mayHaveBody ? new CapturedBody(Options.BodyCapture) : null;
            session.Response = new ResponseInfo(responseHead.StatusCode, responseHead.ReasonPhrase, responseHead.Version, responseHead.Headers) { Body = responseBody };
            session.Timings.MarkResponseHeaders();
            _context.Updated(session);

            // Pass the server's head on exactly as received.
            await client.WriteAsync(responseBytes, cancellationToken).ConfigureAwait(false);
            await client.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (responseHead.StatusCode == 101)
            {
                session.State = SessionState.Tunneling;
                var (watchClient, watchServer) = WatchWebSocket(session, head, responseHead);
                _context.Updated(session);
                var result = await StreamRelay.RunAsync(client, serverStream, cancellationToken, watchClient, watchServer).ConfigureAwait(false);
                session.BytesSent = result.ClientToServer;
                session.BytesReceived = result.ServerToClient;
                session.Complete();
                _context.Finished(session);
                return;
            }

            // The server declined the upgrade: pass its response body on, then close.
            if (responseBody is not null)
            {
                try
                {
                    var framing = MessageFraming.ForResponse(head.Method, responseHead.StatusCode, responseHead.Headers);
                    var reader = framing.CreateReader(serverStream);
                    await CopyBodyAsync(reader, client, framing.Framing == BodyFraming.Chunked, responseBody, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    responseBody.Complete();
                }
            }

            session.Complete();
            _context.Finished(session);
        }
    }

    /// <summary>
    /// For an accepted WebSocket upgrade: watchers that record each message in the session while the relay
    /// forwards the bytes untouched. Null watchers for other upgrades.
    /// </summary>
    private (Action<ReadOnlyMemory<byte>>? Client, Action<ReadOnlyMemory<byte>>? Server) WatchWebSocket(Session session, RequestHead request, ResponseHead response)
    {
        if (!string.Equals(request.Headers.Get("Upgrade"), "websocket", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        var log = new WebSocketLog();
        session.WebSocket = log;
        var (clientInflater, serverInflater) = PerMessageInflater.FromHandshake(response.Headers);
        var lastUpdate = 0L;
        void OnMessage(WebSocketMessage message)
        {
            log.Add(message);

            // Several updates a second are plenty for the UI, which batches them anyway.
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref lastUpdate), now) > TimeSpan.FromMilliseconds(100))
            {
                Interlocked.Exchange(ref lastUpdate, now);
                _context.Updated(session);
            }
        }

        Action<ReadOnlyMemory<byte>> Watch(WebSocketFrameParser parser) => data =>
        {
            try
            {
                parser.Feed(data.Span);
            }
#pragma warning disable CA1031 // Watching is best-effort; the relay must go on.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Log.Debug($"WebSocket parsing stopped for session #{session.Id}: {ex.Message}");
            }
        };

        // Messages from both directions are added under the log's lock; the parser per direction is single-threaded.
        var gate = new Lock();
        void Locked(WebSocketMessage message)
        {
            lock (gate)
            {
                OnMessage(message);
            }
        }

        return (
            Watch(new WebSocketFrameParser(fromClient: true, clientInflater, log.NextIndex, Locked)),
            Watch(new WebSocketFrameParser(fromClient: false, serverInflater, log.NextIndex, Locked)));
    }

    // ---------------------------------------------------------------- CONNECT tunnels

    private async Task HandleConnectAsync(BufferedConnectionStream client, RequestHead head, CancellationToken cancellationToken)
    {
        if (!TryParseAuthority(head.Target, out var host, out var port))
        {
            await ClientResponses.WriteErrorAsync(client, 400, $"Invalid CONNECT target '{head.Target}'.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var authority = FormatAuthority(host, port);
        var url = new Uri($"{(port == 443 ? "https" : "http")}://{authority}/");
        var session = new Session(_context.NextSessionId(), SessionKind.Connect, _endpoints, Process, new RequestInfo(head.Method, url, head.Version, head.Headers));
        _context.Started(session);

        var decrypt = _context.ShouldDecrypt(host);
        try
        {
            if (decrypt)
            {
                await AcceptTunnelAsync(client, head, session, cancellationToken).ConfigureAwait(false);
                await ServeDecryptedTunnelAsync(client, session, host, port, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RelayBlindTunnelAsync(client, head, session, host, port, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            FinishWithError(session, "The proxy was stopped.", SessionState.Aborted);
            throw;
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            FinishWithError(session, Describe(ex), SessionState.Aborted);
        }
        finally
        {
            if (!session.IsFinished)
            {
                session.Complete();
                _context.Finished(session);
            }
        }
    }

    private async Task AcceptTunnelAsync(BufferedConnectionStream client, RequestHead head, Session session, CancellationToken cancellationToken)
    {
        var line = Encoding.ASCII.GetBytes($"{head.Version} 200 Connection Established\r\n\r\n");
        await client.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        await client.FlushAsync(cancellationToken).ConfigureAwait(false);
        session.Response = new ResponseInfo(200, "Connection Established", head.Version, new HttpHeaderList());
        session.State = SessionState.Tunneling;
        session.Timings.MarkResponseHeaders();
        _context.Updated(session);
    }

    private async Task ServeDecryptedTunnelAsync(BufferedConnectionStream client, Session session, string host, int port, CancellationToken cancellationToken)
    {
        int firstByte;
        using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            idle.CancelAfter(Options.ClientIdleTimeout);
            try
            {
                firstByte = await client.PeekByteAsync(idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        if (firstByte < 0)
        {
            return;
        }

        if (firstByte != TlsHandshakeRecord)
        {
            // Not TLS (e.g. ws:// sent through CONNECT): serve it as plain HTTP.
            await ServeAsync(client, new TunnelContext(Uri.UriSchemeHttp, host, port, null), cancellationToken).ConfigureAwait(false);
            return;
        }

        var ssl = new SslStream(client, leaveInnerStreamOpen: true);
        await using (ssl.ConfigureAwait(false))
        {
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                handshake.CancelAfter(Options.TlsHandshakeTimeout);
                try
                {
                    var authority = _context.CertificateAuthority!;
                    await ssl.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions
                        {
                            ServerCertificateSelectionCallback = (_, serverName) =>
                                authority.GetLeafCertificate(string.IsNullOrEmpty(serverName) ? host : serverName),
                            ApplicationProtocols = [SslApplicationProtocol.Http11],
                            ClientCertificateRequired = false,
                            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        },
                        handshake.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is AuthenticationException or IOException
                                           || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    session.Tls = new TlsInfo(host, SslProtocols.None, null, Decrypted: false);
                    if (ex is AuthenticationException)
                    {
                        // The client sent a TLS alert: it refused our certificate. (A bare IOException here is
                        // usually a browser dropping a spare connection, so it does not count.)
                        _context.ReportCertificateRejected(host);
                        FinishWithError(session, $"The app refused Snoop's certificate during the TLS handshake. {CertificateRejectedHint(host)} ({Describe(ex)})", SessionState.Failed);
                    }
                    else
                    {
                        FinishWithError(session, $"The TLS handshake with the app did not complete. {Describe(ex)}", SessionState.Failed);
                    }

                    return;
                }
            }

            var tls = new TlsInfo(
                string.IsNullOrEmpty(ssl.TargetHostName) ? host : ssl.TargetHostName,
                ssl.SslProtocol,
                ssl.NegotiatedCipherSuite.ToString(),
                Decrypted: true);
            session.Tls = tls;
            session.Flags |= SessionFlags.Decrypted;
            _context.Updated(session);

            // With TLS 1.3 a client that rejects our certificate does so after the handshake has
            // completed on our side. It then either resets the connection (Windows) or closes it
            // (Linux, macOS) before sending a single request.
            var decrypted = new BufferedConnectionStream(ssl, leaveOpen: true);
            var tunnel = new TunnelContext(Uri.UriSchemeHttps, host, port, tls);
            var handshakeDone = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                await ServeAsync(decrypted, tunnel, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (tunnel.RequestCount == 0 && IsConnectionError(ex))
            {
                _context.ReportCertificateRejected(host);
                FinishWithError(session, $"The app dropped the connection right after the TLS handshake. {CertificateRejectedHint(host)} ({Describe(ex)})", SessionState.Failed);
                return;
            }

            if (tunnel.RequestCount > 0)
            {
                _context.Rejections.RecordSuccess(host);
            }

            if (tunnel.RequestCount == 0)
            {
                // A clean close with no request is ambiguous: browsers also open spare connections they
                // never use. So this is a note, not an error. Closing almost at once is typical of a
                // client that checked our certificate after the handshake (as .NET does) and refused it.
                session.Note = $"No requests were sent through this tunnel. If the app reported a certificate error: {CertificateRejectedHint(host)}";
                if (System.Diagnostics.Stopwatch.GetElapsedTime(handshakeDone) < QuickCloseWindow)
                {
                    _context.ReportCertificateRejected(host);
                }
            }
        }
    }

    private async Task RelayBlindTunnelAsync(BufferedConnectionStream client, RequestHead head, Session session, string host, int port, CancellationToken cancellationToken)
    {
        Stream server;
        try
        {
            server = await _context.RawConnector.ConnectAsync(host, port, useTls: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUpstreamError(ex, cancellationToken))
        {
            FinishWithError(session, Describe(ex), SessionState.Failed);
            await ClientResponses.WriteErrorAsync(client, 502, $"Could not connect to {FormatAuthority(host, port)}: {Describe(ex)}", cancellationToken).ConfigureAwait(false);
            return;
        }

        await using (server.ConfigureAwait(false))
        {
            await AcceptTunnelAsync(client, head, session, cancellationToken).ConfigureAwait(false);
            session.Tls = new TlsInfo(host, SslProtocols.None, null, Decrypted: false);
            var result = await StreamRelay.RunAsync(client, server, cancellationToken).ConfigureAwait(false);
            session.BytesSent = result.ClientToServer;
            session.BytesReceived = result.ServerToClient;
        }
    }

    // ---------------------------------------------------------------- Helpers

    private void FinishWithError(Session session, string message, SessionState state)
    {
        if (session.IsFinished)
        {
            return;
        }

        session.Fail(message, state);
        _context.Finished(session);
    }

    /// <summary>Works out where a request is going.</summary>
    private RequestTarget ResolveTarget(RequestHead head, TunnelContext? tunnel)
    {
        var target = head.Target;
        var isAbsolute = target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                         || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        if (isAbsolute)
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out var absolute) || string.IsNullOrEmpty(absolute.Host))
            {
                return RequestTarget.Invalid($"Invalid URL '{target}'.");
            }

            return tunnel is null && _context.IsSelf(absolute.Host, absolute.Port)
                ? RequestTarget.Local
                : RequestTarget.For(absolute);
        }

        if (tunnel is null)
        {
            // Origin-form on the plain listener: someone is talking to the proxy directly.
            return target.StartsWith('/') ? RequestTarget.Local : RequestTarget.Invalid($"Invalid request target '{target}'.");
        }

        if (target == "*")
        {
            target = "/";
        }

        if (!target.StartsWith('/'))
        {
            return RequestTarget.Invalid($"Invalid request target '{target}'.");
        }

        // Inside a tunnel the CONNECT authority decides where traffic goes; the Host header is passed on unchanged.
        var defaultPort = tunnel.Scheme == Uri.UriSchemeHttps ? 443 : 80;
        var authority = tunnel.Port == defaultPort ? FormatHost(tunnel.Host) : FormatAuthority(tunnel.Host, tunnel.Port);
        return Uri.TryCreate($"{tunnel.Scheme}://{authority}{target}", UriKind.Absolute, out var url)
            ? RequestTarget.For(url)
            : RequestTarget.Invalid($"Invalid request target '{target}'.");
    }

    /// <summary>Parses a CONNECT target such as <c>example.com:443</c> or <c>[::1]:8443</c>.</summary>
    internal static bool TryParseAuthority(string authority, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        string portText;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            if (close < 0 || close + 1 >= authority.Length || authority[close + 1] != ':')
            {
                return false;
            }

            host = authority[1..close];
            portText = authority[(close + 2)..];
            if (!IPAddress.TryParse(host, out _))
            {
                return false;
            }
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            if (colon <= 0 || authority.IndexOf(':', StringComparison.Ordinal) != colon)
            {
                return false;
            }

            host = authority[..colon];
            portText = authority[(colon + 1)..];
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
            {
                return false;
            }
        }

        return int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;
    }

    private static string FormatHost(string host) => host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;

    private static string FormatAuthority(string host, int port) => $"{FormatHost(host)}:{port.ToString(CultureInfo.InvariantCulture)}";

    private static bool IsConnectionError(Exception ex) =>
        ex is IOException or SocketException or ObjectDisposedException or AuthenticationException;

    private static bool IsUpstreamError(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or IOException or SocketException or TimeoutException or AuthenticationException or HttpParseException
        || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    /// <summary>
    /// A one-line reason for an exception, including inner exceptions but without
    /// .NET's "see inner exception" filler.
    /// </summary>
    internal static string Describe(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return "The operation timed out.";
        }

        var parts = new List<string>();
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            var text = current.Message.Trim().TrimEnd('.');
            var isFiller = current.InnerException is not null
                           && text.Contains("inner exception", StringComparison.OrdinalIgnoreCase);
            if (text.Length > 0 && !isFiller && !parts.Exists(p => p.Contains(text, StringComparison.Ordinal)))
            {
                parts.Add(text);
            }
        }

        return parts.Count == 0 ? ex.GetType().Name : string.Join(": ", parts) + ".";
    }

    private static string CertificateRejectedHint(string host) =>
        "It likely does not trust the Snoop root certificate or pins certificates. "
        + $"Snoop stops decrypting {host} after repeated failures; to skip it from the start, use --skip {host}.";

    /// <summary>Where requests inside a CONNECT tunnel go, and how many have arrived.</summary>
    private sealed record TunnelContext(string Scheme, string Host, int Port, TlsInfo? Tls)
    {
        public int RequestCount { get; set; }
    }

    private readonly record struct RequestTarget(Uri? Url, bool IsLocal, string? Error)
    {
        public static RequestTarget Local => new(null, true, null);

        public static RequestTarget For(Uri url) => new(url, false, null);

        public static RequestTarget Invalid(string error) => new(null, false, error);
    }
}
