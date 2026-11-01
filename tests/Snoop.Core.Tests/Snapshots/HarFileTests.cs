using System.Text;
using Snoop.Core.Sessions;
using Snoop.Core.Snapshots;

namespace Snoop.Core.Tests.Snapshots;

public sealed class HarFileTests
{
    private const string ChromeHar = """
        {
          "log": {
            "version": "1.2",
            "creator": { "name": "WebInspector", "version": "537.36" },
            "entries": [
              {
                "startedDateTime": "2026-10-31T18:00:00.000Z",
                "time": 120.5,
                "request": {
                  "method": "post",
                  "url": "https://api.example.com/v1/login?next=%2Fhome",
                  "httpVersion": "h2",
                  "headers": [
                    { "name": ":authority", "value": "api.example.com" },
                    { "name": "content-type", "value": "application/json" }
                  ],
                  "postData": { "mimeType": "application/json", "text": "{\"user\":\"ann\"}" }
                },
                "response": {
                  "status": 200,
                  "statusText": "",
                  "httpVersion": "h2",
                  "headers": [
                    { "name": "content-type", "value": "application/json" },
                    { "name": "content-encoding", "value": "br" }
                  ],
                  "content": { "size": 11, "mimeType": "application/json", "text": "{\"ok\":true}" }
                },
                "timings": { "blocked": 1, "dns": -1, "connect": -1, "ssl": -1, "send": 2, "wait": 100, "receive": 17.5 },
                "_process": "chrome:4242"
              },
              {
                "startedDateTime": "2026-10-31T18:00:01.000Z",
                "time": 0,
                "request": { "method": "GET", "url": "https://img.example.com/a.png", "httpVersion": "http/1.1", "headers": [] },
                "response": {
                  "status": 200, "statusText": "OK", "httpVersion": "http/1.1", "headers": [],
                  "content": { "size": 4, "mimeType": "image/png", "text": "iVBORw==", "encoding": "base64" }
                },
                "timings": { "send": 0, "wait": 0, "receive": 0 }
              },
              {
                "startedDateTime": "2026-10-31T18:00:02.000Z",
                "time": 5,
                "request": { "method": "GET", "url": "https://down.example.com/", "httpVersion": "", "headers": [] },
                "response": { "status": 0, "statusText": "", "httpVersion": "", "headers": [], "content": { "size": 0 } },
                "timings": { "send": 0, "wait": 0, "receive": 0 },
                "_error": "net::ERR_NAME_NOT_RESOLVED"
              },
              { "request": { "method": "GET", "url": "not a url" } },
              {
                "startedDateTime": "2026-10-31T18:00:03.000Z",
                "time": 0,
                "request": { "method": "GET", "url": "wss://ws.example.com/socket", "httpVersion": "HTTP/1.1", "headers": [] },
                "response": { "status": 101, "statusText": "Switching Protocols", "httpVersion": "HTTP/1.1", "headers": [], "content": { "size": 0 } },
                "timings": {},
                "_webSocketMessages": [
                  { "type": "send", "time": 1793469603.5, "opcode": 1, "data": "hello" },
                  { "type": "receive", "time": 1793469603.6, "opcode": 2, "data": "AAE=" }
                ]
              }
            ]
          }
        }
        """;

    private static HarImportResult Load(string json, SessionIdSource? ids = null) =>
        HarFile.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), ids ?? new SessionIdSource());

    [Fact]
    public void Reads_a_browser_har()
    {
        var result = Load(ChromeHar);
        Assert.Equal(4, result.Sessions.Count);
        Assert.Equal(1, result.Skipped);

        var login = result.Sessions[0];
        Assert.Equal("POST", login.Request.Method);
        Assert.Equal("https://api.example.com/v1/login?next=%2Fhome", login.Request.Url.AbsoluteUri);
        Assert.Equal("HTTP/2", login.Request.HttpVersion);
        Assert.False(login.Request.Headers.Contains(":authority"));
        Assert.Equal("{\"user\":\"ann\"}", Encoding.UTF8.GetString(login.Request.Body!.ToArray()));
        Assert.Equal(200, login.Response!.StatusCode);
        Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(login.Response.Body!.ToArray()));
        Assert.False(login.Response.Headers.Contains("Content-Encoding")); // the HAR body is already decoded
        Assert.Equal(SessionState.Completed, login.State);
        Assert.True(login.Flags.HasFlag(SessionFlags.Imported));
        Assert.Equal("chrome", login.ClientProcess!.Name);
        Assert.Equal(4242, login.ClientProcess.ProcessId);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 18, 0, 0, TimeSpan.Zero), login.Timings.StartedAt);
        Assert.Equal(120.5, Math.Round(login.Timings.Duration!.Value.TotalMilliseconds, 3));
        Assert.Equal(3, Math.Round(login.Timings.RequestSent!.Value.TotalMilliseconds, 3));
        Assert.Equal(103, Math.Round(login.Timings.ResponseHeadersReceived!.Value.TotalMilliseconds, 3));
    }

    [Fact]
    public void Decodes_base64_bodies_and_failed_entries()
    {
        var result = Load(ChromeHar);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], result.Sessions[1].Response!.Body!.ToArray());
        Assert.Equal("HTTP/1.1", result.Sessions[1].Request.HttpVersion);

        var failed = result.Sessions[2];
        Assert.Null(failed.Response);
        Assert.Equal(SessionState.Failed, failed.State);
        Assert.Equal("net::ERR_NAME_NOT_RESOLVED", failed.Error);
    }

    [Fact]
    public void Reads_websocket_messages()
    {
        var socket = Load(ChromeHar).Sessions[3];
        Assert.Equal(SessionKind.Upgrade, socket.Kind);
        var messages = socket.WebSocket!.Snapshot();
        Assert.Equal(2, messages.Count);
        Assert.True(messages[0].FromClient);
        Assert.Equal("hello", Encoding.UTF8.GetString(messages[0].Payload));
        Assert.False(messages[1].FromClient);
        Assert.Equal(WebSocketOpcode.Binary, messages[1].Opcode);
        Assert.Equal([0, 1], messages[1].Payload);
    }

    [Fact]
    public void Numbers_continue_from_the_id_source()
    {
        var ids = new SessionIdSource();
        ids.Next();
        ids.Next();
        Assert.Equal(3, Load(ChromeHar, ids).Sessions[0].Id);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"log\":{}}")]
    public void Rejects_files_that_are_not_har(string text) =>
        Assert.Throws<InvalidDataException>(() => Load(text));

    [Fact]
    public void Form_params_become_a_body()
    {
        const string har = """
            {"log":{"entries":[{"request":{"method":"POST","url":"http://x.test/f","headers":[],
              "postData":{"mimeType":"application/x-www-form-urlencoded","params":[{"name":"a b","value":"1&2"}]}},
              "response":{"status":204,"headers":[],"content":{"size":0}}}]}}
            """;
        var session = Assert.Single(Load(har).Sessions);
        Assert.Equal("a%20b=1%262", Encoding.UTF8.GetString(session.Request.Body!.ToArray()));
        Assert.Null(session.Response!.Body);
    }
}
