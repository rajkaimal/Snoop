using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Snoop.Proxy.Tests.TestSupport;

/// <summary>
/// A real Kestrel server on a random loopback port, standing in for "the internet".
/// Optional TLS with HTTP/1.1 and HTTP/2.
/// </summary>
internal sealed class TestServer : IAsyncDisposable
{
    public const string HelloText = "hello from the test server";

    private readonly WebApplication _app;

    private TestServer(WebApplication app, Uri baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress { get; }

    public int Port => BaseAddress.Port;

    public Uri Url(string pathAndQuery) => new(BaseAddress, pathAndQuery);

    public static async Task<TestServer> StartAsync(X509Certificate2? certificate = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, 0, listen =>
            {
                if (certificate is not null)
                {
                    listen.Protocols = HttpProtocols.Http1AndHttp2;
                    listen.UseHttps(certificate);
                }
                else
                {
                    listen.Protocols = HttpProtocols.Http1;
                }
            });
        });

        var app = builder.Build();
        app.UseWebSockets();
        MapEndpoints(app);
        await app.StartAsync();

        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var uri = new Uri(address.Replace("127.0.0.1", "localhost", StringComparison.Ordinal));
        return new TestServer(app, uri);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/hello", () => Results.Text(HelloText));

        app.MapGet("/headers", (HttpRequest request) =>
            Results.Text(string.Join("\n", request.Headers.Select(h => $"{h.Key}: {h.Value}"))));

        app.MapPost("/echo", async (HttpRequest request, HttpResponse response) =>
        {
            response.ContentType = request.ContentType ?? "application/octet-stream";
            await request.Body.CopyToAsync(response.Body);
        });

        // No Content-Length: the server streams the body in pieces.
        app.MapGet("/stream", async (HttpResponse response) =>
        {
            response.ContentType = "text/plain";
            for (var i = 0; i < 5; i++)
            {
                await response.WriteAsync($"part{i};");
                await response.Body.FlushAsync();
            }
        });

        app.MapGet("/large", () => Results.Bytes(LargePayload, "application/octet-stream"));

        app.MapGet("/status/{code:int}", (int code) => Results.StatusCode(code));

        app.MapGet("/redirect", () => Results.Redirect("/hello"));

        app.Map("/ws", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { DangerousEnableCompression = true });
            var buffer = new byte[4096];
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    return;
                }

                await socket.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, CancellationToken.None);
            }
        });
    }

    public static readonly byte[] LargePayload = Enumerable.Range(0, 1_000_000).Select(i => (byte)(i * 7)).ToArray();
}
