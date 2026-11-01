using System.IO.Compression;
using System.Text;
using Snoop.Core.Sessions;
using Snoop.Core.Snapshots;
using Snoop.Inspection.Export;

namespace Snoop.Inspection.Tests.Export;

public sealed class HarImportAndTextTests
{
    private static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }

        return output.ToArray();
    }

    [Fact]
    public void Exported_har_imports_back()
    {
        using var original = TestSessions.Create(
            "PUT",
            "https://example.com/items/7?x=1",
            TestSessions.Headers(("Host", "example.com"), ("Content-Type", "application/json")),
            TestSessions.Utf8("{\"name\":\"seven\"}"),
            201,
            TestSessions.Headers(("Content-Type", "text/plain"), ("Content-Encoding", "gzip")),
            Gzip("created ok"));

        var har = HarExporter.WriteToString([original]);
        var result = HarFile.Load(new MemoryStream(Encoding.UTF8.GetBytes(har)), new SessionIdSource());

        var loaded = Assert.Single(result.Sessions);
        Assert.Equal(0, result.Skipped);
        Assert.Equal("PUT", loaded.Request.Method);
        Assert.Equal(original.Request.Url, loaded.Request.Url);
        Assert.Equal("{\"name\":\"seven\"}", Encoding.UTF8.GetString(loaded.Request.Body!.ToArray()));
        Assert.Equal(201, loaded.Response!.StatusCode);
        Assert.Equal("created ok", Encoding.UTF8.GetString(loaded.Response.Body!.ToArray()));
        Assert.False(loaded.Response.Headers.Contains("Content-Encoding"));
        Assert.Equal("chrome", loaded.ClientProcess!.Name);
        Assert.Equal(1234, loaded.ClientProcess.ProcessId);
        Assert.Equal(original.Timings.StartedAt, loaded.Timings.StartedAt);
    }

    [Fact]
    public void Text_dump_shows_request_and_decoded_response()
    {
        using var session = TestSessions.Create(
            "POST",
            "https://example.com/login",
            TestSessions.Headers(("Host", "example.com")),
            TestSessions.Utf8("user=ann"),
            200,
            TestSessions.Headers(("Content-Type", "text/html; charset=utf-8"), ("Content-Encoding", "gzip")),
            Gzip("<p>Welcome</p>"));

        var text = TextDumpExporter.WriteToString([session]);

        Assert.Contains($"#{session.Id}  POST https://example.com/login  -> 200", text, StringComparison.Ordinal);
        Assert.Contains("POST /login HTTP/1.1\r\nHost: example.com\r\n\r\nuser=ann", text, StringComparison.Ordinal);
        Assert.Contains("HTTP/1.1 200 OK\r\n", text, StringComparison.Ordinal);
        Assert.Contains("[decoded from gzip]", text, StringComparison.Ordinal);
        Assert.Contains("<p>Welcome</p>", text, StringComparison.Ordinal);
        Assert.Contains("chrome:1234", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_dump_summarises_binary_and_missing_responses()
    {
        using var image = TestSessions.Create(url: "https://example.com/a.png", responseHeaders: TestSessions.Headers(("Content-Type", "image/png")), responseBody: [0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 1]);
        using var pending = TestSessions.Create(url: "https://example.com/slow", status: null);

        var text = TextDumpExporter.WriteToString([pending, image]);

        Assert.Contains("[8 bytes of binary data]", text, StringComparison.Ordinal);
        Assert.Contains("(no response)", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("a.png", StringComparison.Ordinal) < text.IndexOf("/slow", StringComparison.Ordinal)); // ordered by id
    }
}
