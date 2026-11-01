using System.IO.Compression;
using System.Text;
using Snoop.Inspection.Bodies;
using static Snoop.Inspection.Tests.TestSessions;

namespace Snoop.Inspection.Tests.Bodies;

public class ContentDecoderTests
{
    private const string Text = "{\"hello\":\"world\",\"items\":[1,2,3]}";

    [Theory]
    [InlineData("gzip")]
    [InlineData("x-gzip")]
    [InlineData("br")]
    [InlineData("deflate")]
    public void Undoes_common_encodings(string encoding)
    {
        var decoded = ContentDecoder.Decode(Compress(Utf8(Text), encoding), Headers(("Content-Encoding", encoding)));

        Assert.Equal(Text, Encoding.UTF8.GetString(decoded.Bytes));
        Assert.True(decoded.WasDecoded);
        Assert.Null(decoded.Error);
    }

    [Fact]
    public void Accepts_raw_deflate_too()
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest))
        {
            deflate.Write(Utf8(Text));
        }

        var decoded = ContentDecoder.Decode(output.ToArray(), Headers(("Content-Encoding", "deflate")));

        Assert.Equal(Text, Encoding.UTF8.GetString(decoded.Bytes));
    }

    [Fact]
    public void Undoes_stacked_encodings_last_first()
    {
        var twice = Compress(Compress(Utf8(Text), "gzip"), "br");

        var decoded = ContentDecoder.Decode(twice, Headers(("Content-Encoding", "gzip, br")));

        Assert.Equal(Text, Encoding.UTF8.GetString(decoded.Bytes));
        Assert.Equal(new[] { "br", "gzip" }, decoded.AppliedEncodings);
    }

    [Fact]
    public void Unknown_encoding_is_reported_not_thrown()
    {
        var decoded = ContentDecoder.Decode([1, 2, 3], Headers(("Content-Encoding", "zstd")));

        Assert.False(decoded.WasDecoded);
        Assert.Contains("zstd", decoded.Error, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 1, 2, 3 }, decoded.Bytes);
    }

    [Fact]
    public void Corrupt_data_is_reported_not_thrown()
    {
        var decoded = ContentDecoder.Decode(Utf8("not gzip at all"), Headers(("Content-Encoding", "gzip")));

        Assert.NotNull(decoded.Error);
        Assert.Equal("not gzip at all", Encoding.UTF8.GetString(decoded.Bytes));
    }

    [Fact]
    public void Decompression_bombs_are_capped()
    {
        var zeros = Compress(new byte[5_000_000], "gzip");

        var decoded = ContentDecoder.Decode(zeros, Headers(("Content-Encoding", "gzip")), maxDecodedBytes: 1_000_000);

        Assert.NotNull(decoded.Error);
        Assert.False(decoded.WasDecoded);
    }

    [Fact]
    public void No_encoding_returns_the_same_bytes()
    {
        var bytes = Utf8("plain");

        var decoded = ContentDecoder.Decode(bytes, Headers(("Content-Encoding", "identity")));

        Assert.Same(bytes, decoded.Bytes);
    }

    internal static byte[] Compress(byte[] data, string encoding)
    {
        using var output = new MemoryStream();
        using (Stream compressor = encoding switch
               {
                   "br" => new BrotliStream(output, CompressionLevel.Fastest),
                   "deflate" => new ZLibStream(output, CompressionLevel.Fastest),
                   _ => new GZipStream(output, CompressionLevel.Fastest),
               })
        {
            compressor.Write(data);
        }

        return output.ToArray();
    }
}

public class ContentSnifferTests
{
    [Theory]
    [InlineData("application/json; charset=utf-8", ContentKind.Json)]
    [InlineData("application/vnd.api+json", ContentKind.Json)]
    [InlineData("text/html", ContentKind.Html)]
    [InlineData("application/xml", ContentKind.Xml)]
    [InlineData("application/atom+xml", ContentKind.Xml)]
    [InlineData("text/javascript", ContentKind.JavaScript)]
    [InlineData("text/css", ContentKind.Css)]
    [InlineData("image/webp", ContentKind.Image)]
    [InlineData("font/woff2", ContentKind.Font)]
    [InlineData("text/plain", ContentKind.Text)]
    [InlineData("application/x-www-form-urlencoded", ContentKind.Form)]
    [InlineData("application/octet-stream", ContentKind.Binary)]
    [InlineData(null, ContentKind.Binary)]
    public void Maps_content_types(string? contentType, ContentKind expected) =>
        Assert.Equal(expected, ContentSniffer.FromContentType(contentType));

    [Theory]
    [InlineData("  {\"a\":1}", ContentKind.Json)]
    [InlineData("[1,2]", ContentKind.Json)]
    [InlineData("<?xml version=\"1.0\"?><a/>", ContentKind.Xml)]
    [InlineData("<!DOCTYPE html><html></html>", ContentKind.Html)]
    [InlineData("just words", ContentKind.Text)]
    public void Sniffs_untyped_bodies(string body, ContentKind expected) =>
        Assert.Equal(expected, ContentSniffer.Detect(null, Utf8(body)));

    [Fact]
    public void Sniffs_png_and_binary()
    {
        Assert.Equal(ContentKind.Image, ContentSniffer.Detect("application/octet-stream", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A]));
        Assert.Equal(ContentKind.Binary, ContentSniffer.Detect(null, [0x00, 0x01, 0x02, 0xFF]));
        Assert.Equal(ContentKind.Empty, ContentSniffer.Detect("application/json", []));
    }

    [Fact]
    public void Reads_media_type_and_charset()
    {
        Assert.Equal("text/html", ContentSniffer.MediaType("Text/HTML; Charset=\"ISO-8859-1\""));
        Assert.Equal("ISO-8859-1", ContentSniffer.Charset("Text/HTML; Charset=\"ISO-8859-1\""));
        Assert.Null(ContentSniffer.Charset("text/html"));
    }
}

public class BodyRendererTests
{
    [Fact]
    public void Gzipped_json_is_decoded_and_pretty_printed()
    {
        var body = Body(ContentDecoderTests.Compress(Utf8("{\"a\":1,\"b\":[true,null]}"), "gzip"));
        var headers = Headers(("Content-Type", "application/json"), ("Content-Encoding", "gzip"));

        var rendered = BodyRenderer.Render(body, headers);

        Assert.Equal(BodyViewMode.Json, rendered.Mode);
        Assert.Contains("\"a\": 1", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("\n", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("gzip decoded", rendered.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_json_falls_back_to_text_and_says_so()
    {
        var rendered = BodyRenderer.Render(Body(Utf8("{broken")), Headers(("Content-Type", "application/json")));

        Assert.Equal(BodyViewMode.Text, rendered.Mode);
        Assert.Equal("{broken", rendered.Text);
        Assert.Contains("not valid JSON", rendered.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Xml_is_indented()
    {
        var rendered = BodyRenderer.Render(Body(Utf8("<a><b>1</b></a>")), Headers(("Content-Type", "text/xml")));

        Assert.Equal(BodyViewMode.Xml, rendered.Mode);
        Assert.Contains("\n  <b>1</b>", rendered.Text.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Images_come_back_as_bytes()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

        var rendered = BodyRenderer.Render(Body(png), Headers(("Content-Type", "image/png")));

        Assert.Equal(BodyViewMode.Image, rendered.Mode);
        Assert.Equal(png, rendered.ImageBytes);
    }

    [Fact]
    public void Binary_defaults_to_hex_and_any_body_can_be_shown_as_hex()
    {
        var binary = BodyRenderer.Render(Body([0, 1, 2, 3]), Headers(("Content-Type", "application/octet-stream")));
        var forced = BodyRenderer.Render(Body(Utf8("hi")), Headers(("Content-Type", "text/plain")), BodyViewMode.Hex);

        Assert.Equal(BodyViewMode.Hex, binary.Mode);
        Assert.StartsWith("00000000  00 01 02 03", binary.Text, StringComparison.Ordinal);
        Assert.Contains("68 69", forced.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Charset_is_respected()
    {
        var latin1 = Encoding.Latin1.GetBytes("café");

        var rendered = BodyRenderer.Render(Body(latin1), Headers(("Content-Type", "text/plain; charset=iso-8859-1")));

        Assert.Equal("café", rendered.Text);
    }

    [Fact]
    public void Missing_body_renders_as_none()
    {
        Assert.Same(RenderedBody.None, BodyRenderer.Render(null, Headers()));
    }

    [Fact]
    public void Asking_for_an_image_view_of_text_falls_back_to_hex()
    {
        var rendered = BodyRenderer.Render(Body(Utf8("hello")), Headers(("Content-Type", "text/plain")), BodyViewMode.Image);

        Assert.Equal(BodyViewMode.Hex, rendered.Mode);
        Assert.Contains("not a displayable image", rendered.Summary, StringComparison.Ordinal);
    }
}
