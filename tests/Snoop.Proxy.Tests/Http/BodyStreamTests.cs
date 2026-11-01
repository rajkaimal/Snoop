using System.Text;
using Snoop.Core.Bodies;
using Snoop.Proxy.Http;
using Snoop.Proxy.Tests.TestSupport;

namespace Snoop.Proxy.Tests.Http;

public class BodyStreamTests
{
    [Fact]
    public async Task ContentLength_reads_exactly_and_leaves_the_rest()
    {
        var source = Buffered("helloNEXT", fragmentSize: 2);
        var body = new ContentLengthReadStream(source, 5);

        Assert.Equal("hello", await ReadAllAsync(body));
        Assert.True(body.IsComplete);
        Assert.Equal("NEXT", await ReadAllAsync(source));
    }

    [Fact]
    public async Task ContentLength_throws_on_early_close()
    {
        var body = new ContentLengthReadStream(Buffered("abc"), 10);

        await Assert.ThrowsAsync<IOException>(() => ReadAllAsync(body));
    }

    [Fact]
    public async Task Chunked_decodes_with_extensions_and_trailers()
    {
        var wire = "4;name=value\r\nWiki\r\n5\r\npedia\r\nE\r\n in\r\n\r\nchunks.\r\n0\r\nX-Trailer: 1\r\n\r\nAFTER";
        var source = Buffered(wire, fragmentSize: 3);
        var body = new ChunkedReadStream(source);

        Assert.Equal("Wikipedia in\r\n\r\nchunks.", await ReadAllAsync(body));
        Assert.True(body.IsComplete);
        Assert.Equal("AFTER", await ReadAllAsync(source));
    }

    [Theory]
    [InlineData("zz\r\nabc\r\n0\r\n\r\n")]
    [InlineData("-1\r\n\r\n")]
    [InlineData("3\r\nabcXX0\r\n\r\n")] // missing CRLF after data
    public async Task Chunked_rejects_malformed_input(string wire)
    {
        var body = new ChunkedReadStream(Buffered(wire));

        await Assert.ThrowsAnyAsync<Exception>(() => ReadAllAsync(body));
    }

    [Fact]
    public async Task Chunked_writer_produces_valid_wire_format()
    {
        using var output = new MemoryStream();
        var writer = new ChunkedWriteStream(output);

        await writer.WriteAsync("Hello"u8.ToArray());
        await writer.WriteAsync(Array.Empty<byte>());
        await writer.WriteAsync(new byte[26]);
        await writer.CompleteAsync();

        var text = Encoding.Latin1.GetString(output.ToArray());
        Assert.StartsWith("5\r\nHello\r\n1A\r\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\r\n0\r\n\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chunked_writer_output_round_trips_through_reader()
    {
        using var output = new MemoryStream();
        var writer = new ChunkedWriteStream(output);
        var payload = Enumerable.Range(0, 70_000).Select(i => (byte)i).ToArray();
        await writer.WriteAsync(payload.AsMemory(0, 40_000));
        await writer.WriteAsync(payload.AsMemory(40_000));
        await writer.CompleteAsync();

        var reader = new ChunkedReadStream(new BufferedConnectionStream(new MemoryStream(output.ToArray())));
        using var decoded = new MemoryStream();
        await reader.CopyToAsync(decoded);

        Assert.Equal(payload, decoded.ToArray());
    }

    [Fact]
    public async Task Capturing_stream_records_what_is_read()
    {
        using var capture = new CapturedBody();
        var body = new CapturingReadStream(new ContentLengthReadStream(Buffered("payload"), 7), capture);

        Assert.Equal("payload", await ReadAllAsync(body));
        Assert.Equal("payload", Encoding.ASCII.GetString(capture.ToArray()));
    }

    [Fact]
    public async Task Drain_consumes_the_remaining_body()
    {
        var source = Buffered("0123456789rest");
        var body = new ContentLengthReadStream(source, 10);

        Assert.True(await body.DrainAsync(1024, CancellationToken.None));
        Assert.Equal("rest", await ReadAllAsync(source));
    }

    private static BufferedConnectionStream Buffered(string text, int fragmentSize = 1024) =>
        new(new ScriptedStream(text, fragmentSize));

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return Encoding.Latin1.GetString(copy.ToArray());
    }
}
