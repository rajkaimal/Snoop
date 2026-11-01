using System.Text;
using Snoop.Proxy.Http;
using Snoop.Proxy.Tests.TestSupport;

namespace Snoop.Proxy.Tests.Http;

public class BufferedConnectionStreamTests
{
    [Fact]
    public async Task Reads_a_head_delivered_one_byte_at_a_time()
    {
        var stream = new BufferedConnectionStream(new ScriptedStream("GET / HTTP/1.1\r\nHost: a\r\n\r\nBODY", fragmentSize: 1));

        var head = await stream.ReadHeadAsync(1024);

        Assert.Equal("GET / HTTP/1.1\r\nHost: a\r\n\r\n", Encoding.Latin1.GetString(head!));
        Assert.Equal("BODY", await ReadRestAsync(stream));
    }

    [Fact]
    public async Task Reads_consecutive_heads_from_one_buffer()
    {
        var stream = new BufferedConnectionStream(new ScriptedStream("GET /1 HTTP/1.1\r\n\r\nGET /2 HTTP/1.1\r\n\r\n", fragmentSize: 100));

        var first = HttpHeadParser.ParseRequest((await stream.ReadHeadAsync(1024))!);
        var second = HttpHeadParser.ParseRequest((await stream.ReadHeadAsync(1024))!);

        Assert.Equal("/1", first.Target);
        Assert.Equal("/2", second.Target);
        Assert.Null(await stream.ReadHeadAsync(1024));
    }

    [Fact]
    public async Task Skips_leading_blank_lines()
    {
        var stream = new BufferedConnectionStream(new ScriptedStream("\r\n\r\nGET / HTTP/1.1\r\n\r\n", fragmentSize: 3));

        var head = await stream.ReadHeadAsync(1024);

        Assert.StartsWith("GET", Encoding.Latin1.GetString(head!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Returns_null_on_clean_end_of_stream()
    {
        var stream = new BufferedConnectionStream(new ScriptedStream(string.Empty));

        Assert.Null(await stream.ReadHeadAsync(1024));
    }

    [Fact]
    public async Task Throws_when_stream_ends_mid_head()
    {
        var stream = new BufferedConnectionStream(new ScriptedStream("GET / HTTP/1.1\r\nHost:", fragmentSize: 4));

        await Assert.ThrowsAsync<HttpParseException>(async () => await stream.ReadHeadAsync(1024));
    }

    [Fact]
    public async Task Rejects_oversized_head_with_431()
    {
        var huge = "GET / HTTP/1.1\r\nX: " + new string('a', 5000) + "\r\n\r\n";
        var stream = new BufferedConnectionStream(new ScriptedStream(huge, fragmentSize: 512));

        var ex = await Assert.ThrowsAsync<HttpParseException>(async () => await stream.ReadHeadAsync(1024));

        Assert.Equal(431, ex.StatusCode);
    }

    [Fact]
    public async Task Peek_does_not_consume()
    {
        var stream = new BufferedConnectionStream(new ScriptedStream("\x16hello", fragmentSize: 2));

        Assert.Equal(0x16, await stream.PeekByteAsync());
        Assert.Equal(0x16, await stream.PeekByteAsync());
        Assert.Equal("\x16hello", await ReadRestAsync(stream));
    }

    [Fact]
    public async Task Peek_returns_minus_one_at_end()
    {
        var stream = new BufferedConnectionStream(new ScriptedStream(string.Empty));

        Assert.Equal(-1, await stream.PeekByteAsync());
    }

    [Fact]
    public async Task ReadLine_handles_crlf_and_lf()
    {
        var stream = new BufferedConnectionStream(new ScriptedStream("one\r\ntwo\nthree", fragmentSize: 2));

        Assert.Equal("one", await stream.ReadLineAsync(100));
        Assert.Equal("two", await stream.ReadLineAsync(100));
        await Assert.ThrowsAsync<HttpParseException>(async () => await stream.ReadLineAsync(100));
    }

    [Fact]
    public async Task Writes_pass_through()
    {
        var inner = new ScriptedStream(string.Empty);
        var stream = new BufferedConnectionStream(inner);

        await stream.WriteAsync("abc"u8.ToArray());

        Assert.Equal("abc", Encoding.ASCII.GetString(inner.Written.ToArray()));
    }

    private static async Task<string> ReadRestAsync(Stream stream)
    {
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return Encoding.Latin1.GetString(copy.ToArray());
    }
}
