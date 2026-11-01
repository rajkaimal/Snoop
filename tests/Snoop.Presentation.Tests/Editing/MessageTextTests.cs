using System.IO.Compression;
using System.Text;
using Snoop.Core.Http;
using Snoop.Presentation.Editing;
using Snoop.Proxy.Interception;

namespace Snoop.Presentation.Tests.Editing;

public sealed class MessageTextTests
{
    private static EditableRequest Request() =>
        new("POST", new Uri("https://example.com/api?x=1"), new HttpHeaderList([new("Host", "example.com"), new("Content-Type", "application/json")]), Encoding.UTF8.GetBytes("{\"a\":1}"));

    [Fact]
    public void Request_head_round_trips_with_windows_line_endings()
    {
        var request = Request();
        var text = MessageText.FormatRequestHead(request).Replace("\n", "\r", StringComparison.Ordinal) + "\rX-New:  added ";

        Assert.True(MessageText.TryApplyRequestHead(text, request, out var error), error);

        Assert.Equal("POST", request.Method);
        Assert.Equal(new Uri("https://example.com/api?x=1"), request.Url);
        Assert.Equal("added", request.Headers.Get("X-New"));
        Assert.Equal(3, request.Headers.Count);
    }

    [Theory]
    [InlineData("", "request line is missing")]
    [InlineData("GET", "METHOD URL HTTP/1.1")]
    [InlineData("GET /relative HTTP/1.1", "absolute http")]
    [InlineData("G(T https://example.com/ HTTP/1.1", "not a valid method")]
    [InlineData("GET https://example.com/ HTTP/1.1\nno colon here", "Header line 2")]
    public void Bad_request_heads_are_explained_and_change_nothing(string text, string expected)
    {
        var request = Request();

        Assert.False(MessageText.TryApplyRequestHead(text, request, out var error));

        Assert.Contains(expected, error, StringComparison.Ordinal);
        Assert.Equal("POST", request.Method);
        Assert.Equal(2, request.Headers.Count);
    }

    [Fact]
    public void Response_head_round_trips_and_validates_status()
    {
        var response = new EditableResponse(200, "OK", new HttpHeaderList([new("Content-Type", "text/plain")]), []);

        Assert.Equal("HTTP/1.1 200 OK\nContent-Type: text/plain", MessageText.FormatResponseHead(response));
        Assert.True(MessageText.TryApplyResponseHead("HTTP/1.1 404 Not Here\nX-A: 1", response, out _));
        Assert.Equal(404, response.StatusCode);
        Assert.Equal("Not Here", response.ReasonPhrase);
        Assert.Equal("1", response.Headers.Get("X-A"));

        Assert.False(MessageText.TryApplyResponseHead("HTTP/1.1 999 Nope", response, out var error));
        Assert.Contains("100-599", error, StringComparison.Ordinal);
        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public void Unchanged_body_keeps_the_original_bytes_even_with_different_line_endings()
    {
        var original = Encoding.UTF8.GetBytes("line1\r\nline2");
        var headers = new HttpHeaderList([new("Content-Type", "text/plain")]);
        var shown = MessageText.DescribeBody(original, headers);

        var result = MessageText.ApplyBody(original, shown, "line1\rline2", headers);

        Assert.Same(original, result);
    }

    [Fact]
    public void Edited_body_keeps_the_original_line_ending_style()
    {
        var original = Encoding.UTF8.GetBytes("a\r\nb");
        var headers = new HttpHeaderList();
        var shown = MessageText.DescribeBody(original, headers);

        var result = MessageText.ApplyBody(original, shown, "a\rb\rc", headers);

        Assert.Equal("a\r\nb\r\nc", Encoding.UTF8.GetString(result!));
    }

    [Fact]
    public void Compressed_body_is_shown_decoded_and_sent_plain_when_edited()
    {
        var plain = Encoding.UTF8.GetBytes("{\"compressed\":true}");
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(plain);
        }

        var compressed = buffer.ToArray();
        var headers = new HttpHeaderList([new("Content-Type", "application/json"), new("Content-Encoding", "gzip")]);

        var shown = MessageText.DescribeBody(compressed, headers);
        Assert.True(shown.WasDecoded);
        Assert.Equal("{\"compressed\":true}", shown.Text);
        Assert.Contains("decompressed", shown.Note, StringComparison.Ordinal);

        Assert.Same(compressed, MessageText.ApplyBody(compressed, shown, shown.Text, headers));
        Assert.True(headers.Contains("Content-Encoding"));

        var edited = MessageText.ApplyBody(compressed, shown, "{\"compressed\":false}", headers);
        Assert.Equal("{\"compressed\":false}", Encoding.UTF8.GetString(edited!));
        Assert.False(headers.Contains("Content-Encoding"));
    }

    [Fact]
    public void Binary_body_is_not_editable_and_never_changes()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
        var headers = new HttpHeaderList([new("Content-Type", "image/png")]);

        var shown = MessageText.DescribeBody(png, headers);

        Assert.False(shown.IsEditable);
        Assert.Contains("Binary body", shown.Note, StringComparison.Ordinal);
        Assert.Same(png, MessageText.ApplyBody(png, shown, "typed over", headers));
    }

    [Fact]
    public void Headers_parse_skips_blank_lines()
    {
        Assert.True(MessageText.TryParseHeaders("\nAccept: */*\n\nX-Empty:\n", out var headers, out _));

        Assert.Equal(2, headers.Count);
        Assert.Equal(string.Empty, headers.Get("X-Empty"));
    }
}
