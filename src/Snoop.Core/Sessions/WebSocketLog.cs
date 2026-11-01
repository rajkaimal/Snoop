namespace Snoop.Core.Sessions;

/// <summary>WebSocket frame types (RFC 6455 section 5.2).</summary>
public enum WebSocketOpcode : byte
{
    Continuation = 0,
    Text = 1,
    Binary = 2,
    Close = 8,
    Ping = 9,
    Pong = 10,
}

/// <summary>One WebSocket message (all its fragments joined), or one control frame.</summary>
/// <param name="Index">Position in the connection, from 1.</param>
/// <param name="Time">When its last frame arrived.</param>
/// <param name="FromClient">True for app to server, false for server to app.</param>
/// <param name="Opcode">Text, Binary, Close, Ping or Pong.</param>
/// <param name="Length">Payload size as sent (compressed size when <paramref name="WasCompressed"/>).</param>
/// <param name="Payload">The payload, decompressed when possible; only the start of it when <paramref name="IsTruncated"/>.</param>
/// <param name="IsTruncated">The payload was larger than Snoop keeps.</param>
/// <param name="WasCompressed">The message used permessage-deflate.</param>
/// <param name="Note">Why the payload could not be shown as sent, or null.</param>
public sealed record WebSocketMessage(
    long Index,
    DateTimeOffset Time,
    bool FromClient,
    WebSocketOpcode Opcode,
    long Length,
    byte[] Payload,
    bool IsTruncated,
    bool WasCompressed,
    string? Note);

/// <summary>
/// The messages of one WebSocket connection, in order. Thread-safe and append-only. After
/// <see cref="MaxMessages"/> messages, further ones are counted but not kept.
/// </summary>
public sealed class WebSocketLog
{
    public const int MaxMessages = 10_000;

    private readonly Lock _gate = new();
    private readonly List<WebSocketMessage> _messages = [];
    private long _dropped;
    private long _next;

    public int Count
    {
        get { lock (_gate) { return _messages.Count; } }
    }

    /// <summary>Messages seen but not kept because the log was full.</summary>
    public long DroppedCount
    {
        get { lock (_gate) { return _dropped; } }
    }

    /// <summary>The next message number (used by the proxy and by snapshot import).</summary>
    public long NextIndex() => Interlocked.Increment(ref _next);

    public void Add(WebSocketMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            if (_messages.Count >= MaxMessages)
            {
                _dropped++;
                return;
            }

            _messages.Add(message);
        }
    }

    /// <summary>Messages from position <paramref name="start"/> on (for incremental display).</summary>
    public IReadOnlyList<WebSocketMessage> Snapshot(int start = 0)
    {
        lock (_gate)
        {
            return start >= _messages.Count ? [] : _messages.GetRange(start, _messages.Count - start);
        }
    }

    internal void RestoreDropped(long dropped)
    {
        lock (_gate)
        {
            _dropped = dropped;
        }
    }
}
