using Snoop.Core.Sessions;
using Snoop.Inspection.Formatting;
using static Snoop.Inspection.Tests.TestSessions;

namespace Snoop.Inspection.Tests.Formatting;

public class FormattingTests
{
    [Fact]
    public void Json_formatter_indents_and_keeps_unicode()
    {
        var formatted = JsonFormatter.TryFormat("{\"name\":\"Zoë\",\"n\":[1,2]}");

        Assert.NotNull(formatted);
        Assert.Contains("\"name\": \"Zoë\"", formatted, StringComparison.Ordinal);
        Assert.Null(JsonFormatter.TryFormat("not json"));
    }

    [Fact]
    public void Xml_formatter_rejects_html_and_keeps_declaration()
    {
        Assert.Null(XmlFormatter.TryFormat("<html><br></html>"));
        Assert.StartsWith("<?xml", XmlFormatter.TryFormat("<?xml version=\"1.0\" encoding=\"utf-8\"?><a/>"), StringComparison.Ordinal);
    }

    [Fact]
    public void Hex_dump_has_offsets_hex_and_ascii()
    {
        var dump = HexDump.Format("Hello, world! 0123"u8);
        var lines = dump.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.StartsWith("00000000  48 65 6C 6C 6F 2C 20 77  6F 72 6C 64 21 20 30 31  Hello, world! 01", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("00000010  32 33", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Hex_dump_stops_at_the_limit()
    {
        var dump = HexDump.Format(new byte[100], maxBytes: 32);

        Assert.Contains("68 more bytes not shown", dump, StringComparison.Ordinal);
    }

    [Fact]
    public void Raw_request_shows_start_line_headers_and_text_body()
    {
        var session = Create(
            method: "POST",
            url: "https://example.com/login?next=/home",
            requestHeaders: Headers(("Host", "example.com"), ("Content-Type", "application/x-www-form-urlencoded")),
            requestBody: Utf8("user=a&pass=b"));

        var raw = RawMessageFormatter.FormatRequest(session.Request);

        Assert.StartsWith("POST /login?next=/home HTTP/1.1\r\nHost: example.com\r\n", raw, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\nuser=a&pass=b", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Raw_response_describes_encoded_bodies_instead_of_printing_them()
    {
        var session = Create(responseHeaders: Headers(("Content-Encoding", "gzip")), responseBody: [0x1F, 0x8B, 0x08, 0x00]);

        var raw = RawMessageFormatter.FormatResponse(session.Response!);

        Assert.StartsWith("HTTP/1.1 200 OK\r\n", raw, StringComparison.Ordinal);
        Assert.Contains("[4 bytes of encoded data", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Raw_connect_request_uses_authority_form()
    {
        var session = Create(method: "CONNECT", url: "https://example.com:443/", kind: SessionKind.Connect, status: null);

        Assert.StartsWith("CONNECT example.com:443 HTTP/1.1", RawMessageFormatter.FormatRequest(session.Request), StringComparison.Ordinal);
    }
}
