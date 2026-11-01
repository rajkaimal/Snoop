using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Snoop.Core.Sessions;
using Snoop.Proxy.Tests.TestSupport;

namespace Snoop.Proxy.Tests.Integration;

/// <summary>End-to-end: a real client talks through a real Snoop proxy to a real Kestrel server.</summary>
public sealed class ProxyIntegrationTests : IAsyncLifetime
{
    private TestServer _http = null!;
    private TestServer _https = null!;
    private ProxyHarness _harness = null!;

    public async Task InitializeAsync()
    {
        _http = await TestServer.StartAsync();
        _https = await TestServer.StartAsync(TestCertificates.ServerCertificate());
        _harness = ProxyHarness.Start(upstreamRoot: TestCertificates.ServerRoot);
    }

    public async Task DisposeAsync()
    {
        await _harness.DisposeAsync();
        await _http.DisposeAsync();
        await _https.DisposeAsync();
    }

    [Fact]
    public async Task Http_get_is_forwarded_and_recorded()
    {
        using var client = _harness.CreateClient();

        var text = await client.GetStringAsync(_http.Url("/hello"));

        Assert.Equal(TestServer.HelloText, text);
        var session = await _harness.Recorder.WaitForFinishedAsync("/hello");
        Assert.Equal(SessionState.Completed, session.State);
        Assert.Equal(SessionKind.Http, session.Kind);
        Assert.Equal("GET", session.Request.Method);
        Assert.Equal(200, session.Response!.StatusCode);
        Assert.Equal(TestServer.HelloText, Encoding.UTF8.GetString(session.Response.Body!.ToArray()));
        Assert.NotNull(session.Timings.Duration);
    }

    [Fact]
    public async Task Post_body_reaches_the_server_and_is_recorded()
    {
        using var client = _harness.CreateClient();
        using var content = new StringContent("{\"name\":\"snoop\"}", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(_http.Url("/echo"), content);

        Assert.Equal("{\"name\":\"snoop\"}", await response.Content.ReadAsStringAsync());
        var session = await _harness.Recorder.WaitForFinishedAsync("/echo");
        Assert.Equal("{\"name\":\"snoop\"}", Encoding.UTF8.GetString(session.Request.Body!.ToArray()));
        Assert.Equal("application/json; charset=utf-8", session.Request.Headers.Get("Content-Type"));
    }

    [Fact]
    public async Task Chunked_request_body_is_forwarded()
    {
        using var client = _harness.CreateClient();
        using var content = new StreamContent(new MemoryStream(Encoding.ASCII.GetBytes("chunk-me-please")));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        using var request = new HttpRequestMessage(HttpMethod.Post, _http.Url("/echo")) { Content = content };
        request.Headers.TransferEncodingChunked = true;

        using var response = await client.SendAsync(request);

        Assert.Equal("chunk-me-please", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Streamed_response_without_length_arrives_complete()
    {
        using var client = _harness.CreateClient();

        var text = await client.GetStringAsync(_http.Url("/stream"));

        Assert.Equal("part0;part1;part2;part3;part4;", text);
        var session = await _harness.Recorder.WaitForFinishedAsync("/stream");
        Assert.Equal(text.Length, session.Response!.Body!.TotalBytes);
    }

    [Fact]
    public async Task Large_response_is_byte_for_byte_identical()
    {
        using var client = _harness.CreateClient();

        var bytes = await client.GetByteArrayAsync(_http.Url("/large"));

        Assert.Equal(TestServer.LargePayload, bytes);
        var session = await _harness.Recorder.WaitForFinishedAsync("/large");
        Assert.Equal(TestServer.LargePayload.Length, session.Response!.Body!.TotalBytes);
    }

    [Fact]
    public async Task Https_is_decrypted_and_forwarded_over_http2()
    {
        var client = _harness.CreateClient(serverRoot: _harness.Authority.RootCertificate);

        var text = await client.GetStringAsync(_https.Url("/hello"));
        client.Dispose(); // Closes the pooled connection, which ends the tunnel.

        Assert.Equal(TestServer.HelloText, text);
        var session = await _harness.Recorder.WaitForFinishedAsync("/hello");
        Assert.Equal("https", session.Request.Url.Scheme);
        Assert.True(session.Flags.HasFlag(SessionFlags.Decrypted));
        Assert.NotNull(session.Tls);
        Assert.Equal("HTTP/2", session.Response!.HttpVersion);

        var tunnel = await _harness.Recorder.WaitForFinishedAsync(s => s.Kind == SessionKind.Connect);
        Assert.True(tunnel.Flags.HasFlag(SessionFlags.Decrypted));
    }

    [Fact]
    public async Task Https_client_rejects_snoop_when_it_does_not_trust_the_root()
    {
        // The client trusts only the real server's CA, so Snoop's certificate must be refused.
        using var client = _harness.CreateClient(serverRoot: TestCertificates.ServerRoot);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(_https.Url("/hello")));

        // TLS 1.2 clients abort inside the handshake (Failed); TLS 1.3 clients close right after it (a note).
        var tunnel = await _harness.Recorder.WaitForFinishedAsync(s => s.Kind == SessionKind.Connect);
        var explanation = tunnel.State == SessionState.Failed ? tunnel.Error : tunnel.Note;
        Assert.Contains("Snoop root certificate", explanation, StringComparison.Ordinal);
        Assert.DoesNotContain(_harness.Recorder.Sessions, s => s.Kind == SessionKind.Http);
    }

    [Fact]
    public async Task Keep_alive_connection_serves_several_requests()
    {
        using var client = _harness.CreateClient();

        await client.GetStringAsync(_http.Url("/hello"));
        await client.GetStringAsync(_http.Url("/headers"));

        var first = await _harness.Recorder.WaitForFinishedAsync("/hello");
        var second = await _harness.Recorder.WaitForFinishedAsync("/headers");
        Assert.Equal(first.Endpoints.Client.Port, second.Endpoints.Client.Port);
    }

    [Fact]
    public async Task Proxy_only_headers_are_not_forwarded()
    {
        using var client = _harness.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, _http.Url("/headers"));
        request.Headers.TryAddWithoutValidation("Proxy-Authorization", "Basic c2VjcmV0");
        request.Headers.TryAddWithoutValidation("X-Custom", "kept");

        using var response = await client.SendAsync(request);
        var echoed = await response.Content.ReadAsStringAsync();

        Assert.Contains("X-Custom: kept", echoed, StringComparison.Ordinal);
        Assert.DoesNotContain("Proxy-Authorization", echoed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("traceparent", echoed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Redirects_are_passed_to_the_client_not_followed()
    {
        using var client = _harness.CreateClient();

        using var response = await client.GetAsync(_http.Url("/redirect"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/hello", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task No_content_response_has_no_body()
    {
        using var client = _harness.CreateClient();

        using var response = await client.GetAsync(_http.Url("/status/204"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var session = await _harness.Recorder.WaitForFinishedAsync("/status/204");
        Assert.Null(session.Response!.Body);
    }

    [Fact]
    public async Task Unreachable_server_gives_502_and_a_failed_session()
    {
        using var client = _harness.CreateClient();
        var deadPort = GetFreePort();

        using var response = await client.GetAsync(new Uri($"http://localhost:{deadPort}/nothing"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("true", response.Headers.GetValues("X-Snoop-Error").Single());
        var session = await _harness.Recorder.WaitForFinishedAsync("/nothing");
        Assert.Equal(SessionState.Failed, session.State);
        Assert.False(string.IsNullOrEmpty(session.Error));
    }

    [Fact]
    public async Task WebSocket_is_relayed_through_a_tunnel()
    {
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = new FixedProxy(_harness.ProxyUri);
        var wsUri = new Uri($"ws://localhost:{_http.Port}/ws");

        await socket.ConnectAsync(wsUri, CancellationToken.None);
        await socket.SendAsync("ping"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[64];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);

        Assert.Equal("ping", Encoding.ASCII.GetString(buffer, 0, result.Count));
        var session = await _harness.Recorder.WaitForFinishedAsync(s => s.Kind == SessionKind.Upgrade);
        Assert.Equal(101, session.Response!.StatusCode);
        Assert.True(session.BytesSent > 0);
        Assert.True(session.BytesReceived > 0);
        var messages = session.WebSocket!.Snapshot();
        Assert.Contains(messages, m => m.FromClient && m.Opcode == WebSocketOpcode.Text && Encoding.UTF8.GetString(m.Payload) == "ping");
        Assert.Contains(messages, m => !m.FromClient && m.Opcode == WebSocketOpcode.Text && Encoding.UTF8.GetString(m.Payload) == "ping");
        Assert.Contains(messages, m => m.Opcode == WebSocketOpcode.Close);
    }

    [Fact]
    public async Task Compressed_websocket_messages_are_recorded_decoded()
    {
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = new FixedProxy(_harness.ProxyUri);
        socket.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();

        await socket.ConnectAsync(new Uri($"ws://localhost:{_http.Port}/ws"), CancellationToken.None);
        var buffer = new byte[4096];
        foreach (var text in new[] { "{\"n\":1,\"text\":\"hello hello hello\"}", "{\"n\":2,\"text\":\"hello hello hello\"}" })
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
            await socket.ReceiveAsync(buffer, CancellationToken.None);
        }

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);

        var session = await _harness.Recorder.WaitForFinishedAsync(s => s.Kind == SessionKind.Upgrade);
        Assert.Contains("permessage-deflate", session.Response!.Headers.Get("Sec-WebSocket-Extensions"), StringComparison.Ordinal);
        var texts = session.WebSocket!.Snapshot().Where(m => m.Opcode == WebSocketOpcode.Text).ToList();
        Assert.Equal(4, texts.Count);
        Assert.All(texts, m => Assert.True(m.WasCompressed));
        Assert.All(texts, m => Assert.Null(m.Note));
        Assert.Equal("{\"n\":2,\"text\":\"hello hello hello\"}", Encoding.UTF8.GetString(texts[^1].Payload));
    }

    [Fact]
    public async Task Proxy_serves_its_own_page_and_root_certificate()
    {
        using var direct = new HttpClient();

        var page = await direct.GetStringAsync(new Uri(_harness.ProxyUri, "/"));
        var certificate = await direct.GetByteArrayAsync(new Uri(_harness.ProxyUri, "/snoop-root.cer"));

        Assert.Contains("Snoop is running", page, StringComparison.Ordinal);
        Assert.Equal(_harness.Authority.ExportRootPublicCertificate(), certificate);
        Assert.Empty(_harness.Recorder.Sessions);
    }

    [Fact]
    public async Task Malformed_request_gets_400()
    {
        var reply = await SendRawAsync("GET http://localhost/ HTTP/1.1\r\nHost : localhost\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 400", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_with_both_lengths_is_rejected_as_smuggling()
    {
        var reply = await SendRawAsync(
            $"POST http://localhost:{_http.Port}/echo HTTP/1.1\r\nHost: localhost\r\nContent-Length: 4\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 400", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http10_client_gets_a_close_delimited_response()
    {
        var reply = await SendRawAsync($"GET http://localhost:{_http.Port}/stream HTTP/1.0\r\n\r\n");

        Assert.StartsWith("HTTP/1.0 200", reply, StringComparison.Ordinal);
        Assert.Contains("Connection: close", reply, StringComparison.Ordinal);
        Assert.EndsWith("part4;", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_closes_the_listener()
    {
        await _harness.Proxy.StopAsync();

        Assert.False(_harness.Proxy.IsRunning);
        using var tcp = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => tcp.ConnectAsync(_harness.Proxy.Endpoint!.Address, _harness.Proxy.Endpoint.Port));
    }

    private async Task<string> SendRawAsync(string request)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(_harness.Proxy.Endpoint!.Address, _harness.Proxy.Endpoint.Port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream, Encoding.Latin1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await reader.ReadToEndAsync(timeout.Token);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class FixedProxy(Uri proxy) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => proxy;

        public bool IsBypassed(Uri host) => false;
    }
}
