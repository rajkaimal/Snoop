using System.Text.Json;
using Snoop.Core.Sessions;
using Snoop.Inspection.Export;
using static Snoop.Inspection.Tests.TestSessions;

namespace Snoop.Inspection.Tests.Export;

public class CommandExporterTests
{
    [Fact]
    public void Curl_includes_method_headers_and_body_and_skips_framing_headers()
    {
        var session = Create(
            method: "POST",
            url: "https://api.example.com/items",
            requestHeaders: Headers(("Host", "api.example.com"), ("Content-Type", "application/json"), ("Content-Length", "11"), ("Accept-Encoding", "gzip"), ("X-Note", "it's")),
            requestBody: Utf8("{\"a\":\"b'c\"}"));

        var curl = CommandExporter.ToCurl(session.Request);

        Assert.StartsWith("curl -X POST 'https://api.example.com/items'", curl, StringComparison.Ordinal);
        Assert.Contains("-H 'Content-Type: application/json'", curl, StringComparison.Ordinal);
        Assert.Contains("-H 'X-Note: it'\\''s'", curl, StringComparison.Ordinal);
        Assert.Contains("--compressed", curl, StringComparison.Ordinal);
        Assert.Contains("--data-raw '{\"a\":\"b'\\''c\"}'", curl, StringComparison.Ordinal);
        Assert.DoesNotContain("Content-Length", curl, StringComparison.Ordinal);
        Assert.DoesNotContain("Host:", curl, StringComparison.Ordinal);
    }

    [Fact]
    public void Curl_for_get_has_no_method_flag()
    {
        Assert.StartsWith("curl 'https://example.com/api/items?page=2'", CommandExporter.ToCurl(Create().Request), StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShell_moves_content_type_to_its_own_parameter()
    {
        var session = Create(
            method: "PUT",
            requestHeaders: Headers(("Content-Type", "text/plain"), ("X-Name", "O'Brien")),
            requestBody: Utf8("hi"));

        var command = CommandExporter.ToPowerShell(session.Request);

        Assert.Contains("'X-Name' = 'O''Brien'", command, StringComparison.Ordinal);
        Assert.Contains("-Method PUT", command, StringComparison.Ordinal);
        Assert.Contains("-ContentType 'text/plain'", command, StringComparison.Ordinal);
        Assert.Contains("-Body 'hi'", command, StringComparison.Ordinal);
    }
}

public class HarExporterTests
{
    [Fact]
    public void Writes_valid_har_with_requests_and_responses()
    {
        var get = Create(responseHeaders: Headers(("Content-Type", "application/json")), responseBody: Utf8("{\"ok\":true}"));
        var post = Create(method: "POST", requestHeaders: Headers(("Content-Type", "text/plain")), requestBody: Utf8("ping"), status: 201);
        var tunnel = Create(method: "CONNECT", url: "https://example.com:443/", kind: SessionKind.Connect);

        using var har = JsonDocument.Parse(HarExporter.WriteToString([get, post, tunnel]));
        var log = har.RootElement.GetProperty("log");
        var entries = log.GetProperty("entries");

        Assert.Equal("1.2", log.GetProperty("version").GetString());
        Assert.Equal(2, entries.GetArrayLength());
        var first = entries[0];
        Assert.Equal("GET", first.GetProperty("request").GetProperty("method").GetString());
        Assert.Equal("2", first.GetProperty("request").GetProperty("queryString")[0].GetProperty("value").GetString());
        Assert.Equal("{\"ok\":true}", first.GetProperty("response").GetProperty("content").GetProperty("text").GetString());
        Assert.Equal("chrome:1234", first.GetProperty("_process").GetString());
        Assert.Equal("ping", entries[1].GetProperty("request").GetProperty("postData").GetProperty("text").GetString());
        Assert.Equal(201, entries[1].GetProperty("response").GetProperty("status").GetInt32());
    }

    [Fact]
    public void Compressed_bodies_are_stored_decoded()
    {
        var gzipped = Snoop.Inspection.Tests.Bodies.ContentDecoderTests.Compress(Utf8("hello"), "gzip");
        var session = Create(responseHeaders: Headers(("Content-Type", "text/plain"), ("Content-Encoding", "gzip")), responseBody: gzipped);

        using var har = JsonDocument.Parse(HarExporter.WriteToString([session]));
        var content = har.RootElement.GetProperty("log").GetProperty("entries")[0].GetProperty("response").GetProperty("content");

        Assert.Equal("hello", content.GetProperty("text").GetString());
        Assert.Equal(5, content.GetProperty("size").GetInt32());
    }

    [Fact]
    public void Binary_bodies_are_base64()
    {
        var session = Create(responseHeaders: Headers(("Content-Type", "image/png")), responseBody: [0x89, 0x50, 0x4E, 0x47]);

        using var har = JsonDocument.Parse(HarExporter.WriteToString([session]));
        var content = har.RootElement.GetProperty("log").GetProperty("entries")[0].GetProperty("response").GetProperty("content");

        Assert.Equal("base64", content.GetProperty("encoding").GetString());
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, Convert.FromBase64String(content.GetProperty("text").GetString()!));
    }

    [Fact]
    public void Redaction_hides_secret_headers_and_query_values()
    {
        var session = Create(
            url: "https://example.com/cb?code=abc123&state=ok&access_token=xyz",
            requestHeaders: Headers(("Authorization", "Bearer secret"), ("Cookie", "sid=1"), ("Accept", "*/*")));

        var text = HarExporter.WriteToString([session], new HarExportOptions { RedactSecrets = true });

        Assert.DoesNotContain("Bearer secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", text, StringComparison.Ordinal);
        Assert.DoesNotContain("xyz", text, StringComparison.Ordinal);
        Assert.Contains("state=ok", text, StringComparison.Ordinal);
        Assert.Contains("*/*", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_sessions_are_exported_with_their_error()
    {
        var session = Create(status: null);
        session.Fail("Connection refused");

        using var har = JsonDocument.Parse(HarExporter.WriteToString([session]));
        var entry = har.RootElement.GetProperty("log").GetProperty("entries")[0];

        Assert.Equal(0, entry.GetProperty("response").GetProperty("status").GetInt32());
        Assert.Equal("Connection refused", entry.GetProperty("_error").GetString());
    }
}
