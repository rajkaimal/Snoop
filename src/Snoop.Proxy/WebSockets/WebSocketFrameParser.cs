using System.Buffers.Binary;
using Snoop.Core.Sessions;

namespace Snoop.Proxy.WebSockets;

/// <summary>
/// Watches one direction of a WebSocket connection and turns the bytes into messages, without
/// changing them (the relay forwards the original bytes). Joins fragments, unmasks client
/// frames and undoes permessage-deflate when it can. Bytes may arrive in any chunking.
/// Not thread-safe: one parser per direction.
/// </summary>
internal sealed class WebSocketFrameParser
{
    /// <summary>Most payload bytes kept per message; the rest is counted.</summary>
    internal const int MaxStoredPayload = 256 * 1024;

    private readonly bool _fromClient;
    private readonly PerMessageInflater? _inflater;
    private readonly Action<WebSocketMessage> _onMessage;
    private readonly Func<long> _nextIndex;
    private readonly byte[] _header = new byte[14];
    private readonly byte[] _mask = new byte[4];
    private int _headerLength;
    private bool _inPayload;
    private bool _failed;

    // Current frame.
    private bool _fin;
    private bool _rsv1;
    private WebSocketOpcode _opcode;
    private bool _masked;
    private long _payloadLength;
    private long _payloadRead;
    private MemoryStream _framePayload = new();

    // Current data message (may span several frames).
    private WebSocketOpcode _messageOpcode;
    private bool _messageCompressed;
    private long _messageLength;
    private MemoryStream? _messagePayload;
    private bool _messageTruncated;

    public WebSocketFrameParser(bool fromClient, PerMessageInflater? inflater, Func<long> nextIndex, Action<WebSocketMessage> onMessage)
    {
        _fromClient = fromClient;
        _inflater = inflater;
        _nextIndex = nextIndex;
        _onMessage = onMessage;
    }

    /// <summary>True after the stream stopped looking like WebSocket frames; further bytes are ignored.</summary>
    public bool HasFailed => _failed;

    public void Feed(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty && !_failed)
        {
            if (!_inPayload)
            {
                var needed = HeaderLengthNeeded();
                var take = Math.Min(needed - _headerLength, data.Length);
                data[..take].CopyTo(_header.AsSpan(_headerLength));
                _headerLength += take;
                data = data[take..];
                if (_headerLength == HeaderLengthNeeded())
                {
                    StartFrame();
                }

                continue;
            }

            var remaining = _payloadLength - _payloadRead;
            var chunk = data[..(int)Math.Min(remaining, data.Length)];
            if (_framePayload.Length < MaxStoredPayload)
            {
                var room = (int)Math.Min(chunk.Length, MaxStoredPayload - _framePayload.Length);
                var bytes = chunk[..room].ToArray();
                if (_masked)
                {
                    for (var i = 0; i < bytes.Length; i++)
                    {
                        bytes[i] ^= _mask[(int)((_payloadRead + i) & 3)];
                    }
                }

                _framePayload.Write(bytes);
            }

            _payloadRead += chunk.Length;
            data = data[chunk.Length..];
            if (_payloadRead == _payloadLength)
            {
                EndFrame();
            }
        }
    }

    /// <summary>Bytes needed for the header seen so far (2, then more once the length and mask flags are known).</summary>
    private int HeaderLengthNeeded()
    {
        if (_headerLength < 2)
        {
            return 2;
        }

        var length7 = _header[1] & 0x7F;
        var extended = length7 == 126 ? 2 : length7 == 127 ? 8 : 0;
        var mask = (_header[1] & 0x80) != 0 ? 4 : 0;
        return 2 + extended + mask;
    }

    private void StartFrame()
    {
        _fin = (_header[0] & 0x80) != 0;
        _rsv1 = (_header[0] & 0x40) != 0;
        _opcode = (WebSocketOpcode)(_header[0] & 0x0F);
        _masked = (_header[1] & 0x80) != 0;
        var length7 = _header[1] & 0x7F;
        var offset = 2;
        if (length7 == 126)
        {
            _payloadLength = BinaryPrimitives.ReadUInt16BigEndian(_header.AsSpan(2));
            offset = 4;
        }
        else if (length7 == 127)
        {
            var length = BinaryPrimitives.ReadUInt64BigEndian(_header.AsSpan(2));
            if (length > long.MaxValue)
            {
                _failed = true;
                return;
            }

            _payloadLength = (long)length;
            offset = 10;
        }
        else
        {
            _payloadLength = length7;
        }

        if (_masked)
        {
            _header.AsSpan(offset, 4).CopyTo(_mask);
        }

        var known = _opcode is WebSocketOpcode.Continuation or WebSocketOpcode.Text or WebSocketOpcode.Binary
            or WebSocketOpcode.Close or WebSocketOpcode.Ping or WebSocketOpcode.Pong;
        var isControl = (byte)_opcode >= 8;
        if (!known || (isControl && (!_fin || _payloadLength > 125)) || (_opcode == WebSocketOpcode.Continuation && _messagePayload is null))
        {
            // Not WebSocket framing we understand (or a protocol error): stop watching, keep relaying.
            _failed = true;
            return;
        }

        _inPayload = true;
        _payloadRead = 0;
        _framePayload = new MemoryStream();
        if (_payloadLength == 0)
        {
            EndFrame();
        }
    }

    private void EndFrame()
    {
        _inPayload = false;
        _headerLength = 0;
        var payload = _framePayload.ToArray();
        var truncated = _payloadLength > payload.Length;

        if ((byte)_opcode >= 8)
        {
            Emit(_opcode, _payloadLength, payload, truncated, compressed: false);
            return;
        }

        if (_opcode != WebSocketOpcode.Continuation)
        {
            _messageOpcode = _opcode;
            _messageCompressed = _rsv1 && _inflater is not null;
            _messageLength = 0;
            _messagePayload = new MemoryStream();
            _messageTruncated = false;
        }

        _messageLength += _payloadLength;
        var room = (int)Math.Max(0, MaxStoredPayload - _messagePayload!.Length);
        _messagePayload.Write(payload, 0, Math.Min(room, payload.Length));
        _messageTruncated |= truncated || payload.Length > room;

        if (_fin)
        {
            var bytes = _messagePayload.ToArray();
            _messagePayload = null;
            Emit(_messageOpcode, _messageLength, bytes, _messageTruncated, _messageCompressed);
        }
    }

    private void Emit(WebSocketOpcode opcode, long length, byte[] payload, bool truncated, bool compressed)
    {
        string? note = null;
        if (compressed)
        {
            if (truncated)
            {
                _inflater!.Disable();
                note = "Compressed message too large to decode; payload shown compressed.";
            }
            else if (_inflater!.TryInflate(payload, out var inflated, out var error))
            {
                payload = inflated.Length > MaxStoredPayload ? inflated[..MaxStoredPayload] : inflated;
                truncated = inflated.Length > MaxStoredPayload;
            }
            else
            {
                note = error;
            }
        }

        _onMessage(new WebSocketMessage(_nextIndex(), DateTimeOffset.Now, _fromClient, opcode, length, payload, truncated, compressed, note));
    }
}
