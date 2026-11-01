using System.IO.Compression;
using System.Text;
using Snoop.Core.Http;
using Snoop.Core.Sessions;
using Snoop.Proxy.WebSockets;

namespace Snoop.Proxy.Tests.WebSockets;

public sealed class WebSocketFrameParserTests
{
    private readonly List<WebSocketMessage> _messages = [];
    private long _index;

    private WebSocketFrameParser Parser(bool fromClient = false, PerMessageInflater? inflater = null) =>
        new(fromClient, inflater, () => ++_index, _messages.Add);

    /// <summary>Builds one frame as a client (masked) or server (unmasked) would.</summary>
    private static byte[] Frame(byte opcode, byte[] payload, bool fin = true, bool mask = false, bool rsv1 = false)
    {
        using var frame = new MemoryStream();
        frame.WriteByte((byte)((fin ? 0x80 : 0) | (rsv1 ? 0x40 : 0) | opcode));
        var maskBit = mask ? 0x80 : 0;
        if (payload.Length < 126)
        {
            frame.WriteByte((byte)(maskBit | payload.Length));
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            frame.WriteByte((byte)(maskBit | 126));
            frame.WriteByte((byte)(payload.Length >> 8));
            frame.WriteByte((byte)payload.Length);
        }
        else
        {
            frame.WriteByte((byte)(maskBit | 127));
            var length = BitConverter.GetBytes((ulong)payload.Length);
            Array.Reverse(length);
            frame.Write(length);
        }

        var data = (byte[])payload.Clone();
        if (mask)
        {
            byte[] key = [0x12, 0x34, 0x56, 0x78];
            frame.Write(key);
            for (var i = 0; i < data.Length; i++)
            {
                data[i] ^= key[i % 4];
            }
        }

        frame.Write(data);
        return frame.ToArray();
    }

    [Fact]
    public void Masked_text_fed_one_byte_at_a_time()
    {
        var parser = Parser(fromClient: true);
        var bytes = Frame(1, Encoding.UTF8.GetBytes("héllo"), mask: true);

        foreach (var b in bytes)
        {
            parser.Feed([b]);
        }

        var message = Assert.Single(_messages);
        Assert.True(message.FromClient);
        Assert.Equal(WebSocketOpcode.Text, message.Opcode);
        Assert.Equal("héllo", Encoding.UTF8.GetString(message.Payload));
        Assert.Equal(1, message.Index);
    }

    [Fact]
    public void Fragments_are_joined_and_control_frames_in_between_come_first()
    {
        var parser = Parser();
        byte[] stream =
        [
            .. Frame(1, "Hel"u8.ToArray(), fin: false),
            .. Frame(9, "p"u8.ToArray()),
            .. Frame(0, "lo"u8.ToArray()),
            .. Frame(2, new byte[300]),
        ];

        parser.Feed(stream);

        Assert.Equal(new[] { WebSocketOpcode.Ping, WebSocketOpcode.Text, WebSocketOpcode.Binary }, _messages.Select(m => m.Opcode));
        Assert.Equal("Hello", Encoding.UTF8.GetString(_messages[1].Payload));
        Assert.Equal(5, _messages[1].Length);
        Assert.Equal(300, _messages[2].Payload.Length);
    }

    [Fact]
    public void Large_payload_is_counted_but_only_the_start_kept()
    {
        var parser = Parser();
        var payload = new byte[WebSocketFrameParser.MaxStoredPayload + 1000];

        parser.Feed(Frame(2, payload));

        var message = Assert.Single(_messages);
        Assert.True(message.IsTruncated);
        Assert.Equal(payload.Length, message.Length);
        Assert.Equal(WebSocketFrameParser.MaxStoredPayload, message.Payload.Length);
    }

    [Fact]
    public void Garbage_stops_the_parser_without_throwing()
    {
        var parser = Parser();

        parser.Feed([0x83, 0x00, 0x01, 0x02]); // opcode 3 is reserved

        Assert.True(parser.HasFailed);
        Assert.Empty(_messages);
        parser.Feed(Frame(1, "x"u8.ToArray()));
        Assert.Empty(_messages);
    }

    [Fact]
    public void Deflated_messages_with_context_takeover_are_decoded()
    {
        var inflater = PerMessageInflater.FromHandshake(new HttpHeaderList([new("Sec-WebSocket-Extensions", "permessage-deflate; client_max_window_bits")])).FromServer;
        Assert.NotNull(inflater);
        var parser = Parser(inflater: inflater);

        using var compressed = new MemoryStream();
        using var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true);
        foreach (var text in new[] { "{\"a\":\"repeat repeat repeat\"}", "{\"a\":\"repeat repeat repeat\"}" })
        {
            var start = compressed.Length;
            deflate.Write(Encoding.UTF8.GetBytes(text));
            deflate.Flush();
            var part = compressed.ToArray()[(int)start..^4]; // without the 00 00 FF FF tail, as on the wire
            parser.Feed(Frame(1, part, rsv1: true));
        }

        Assert.Equal(2, _messages.Count);
        Assert.All(_messages, m => Assert.True(m.WasCompressed));
        Assert.All(_messages, m => Assert.Equal("{\"a\":\"repeat repeat repeat\"}", Encoding.UTF8.GetString(m.Payload)));
        Assert.True(_messages[1].Length < 28, "the second message should be smaller thanks to the shared window");
    }

    [Fact]
    public void No_extension_means_no_inflater()
    {
        var (client, server) = PerMessageInflater.FromHandshake(new HttpHeaderList());

        Assert.Null(client);
        Assert.Null(server);
    }

    [Theory]
    [InlineData("gzip, deflate, br, zstd", "gzip, deflate, br")]
    [InlineData("zstd;q=1.0, gzip;q=0.5", "gzip;q=0.5")]
    [InlineData("zstd", null)]
    public void Zstd_is_removed_from_accept_encoding(string value, string? expected)
    {
        var headers = new HttpHeaderList([new("Accept-Encoding", value)]);

        Assert.True(Snoop.Proxy.Http.AcceptEncoding.RemoveZstd(headers));

        Assert.Equal(expected, headers.Get("Accept-Encoding"));
    }

    [Fact]
    public void Accept_encoding_without_zstd_is_untouched()
    {
        var headers = new HttpHeaderList([new("Accept-Encoding", "gzip, br")]);

        Assert.False(Snoop.Proxy.Http.AcceptEncoding.RemoveZstd(headers));
        Assert.Equal("gzip, br", headers.Get("Accept-Encoding"));
    }
}
