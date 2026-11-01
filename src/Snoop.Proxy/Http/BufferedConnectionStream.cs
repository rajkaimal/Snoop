namespace Snoop.Proxy.Http;

/// <summary>
/// A read-buffered wrapper around a connection stream. Lets the proxy read a whole
/// message head, read CRLF-terminated lines (chunk sizes), and peek at the first byte
/// (to spot a TLS handshake) without losing any bytes for whoever reads next.
/// Writes pass straight through to the inner stream.
/// </summary>
public sealed class BufferedConnectionStream : Stream
{
    private const int DefaultBufferSize = 16 * 1024;

    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private byte[] _buffer;
    private int _start;
    private int _end;

    public BufferedConnectionStream(Stream inner, bool leaveOpen = false, int bufferSize = DefaultBufferSize)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 128);
        _inner = inner;
        _leaveOpen = leaveOpen;
        _buffer = new byte[bufferSize];
    }

    public Stream InnerStream => _inner;

    /// <summary>Bytes read from the inner stream but not yet consumed.</summary>
    public int BufferedCount => _end - _start;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Returns the next byte without consuming it, or -1 at end of stream.</summary>
    public async ValueTask<int> PeekByteAsync(CancellationToken cancellationToken = default)
    {
        if (BufferedCount == 0 && !await FillAsync(_buffer.Length, cancellationToken).ConfigureAwait(false))
        {
            return -1;
        }

        return _buffer[_start];
    }

    /// <summary>
    /// Reads a complete message head, up to and including the blank line that ends it.
    /// Leading empty lines are skipped (RFC 9112 section 2.2).
    /// Returns null if the stream ends cleanly before any byte of a new head arrives.
    /// </summary>
    /// <exception cref="HttpParseException">The head is larger than <paramref name="maxBytes"/> (431) or truncated.</exception>
    public async ValueTask<byte[]?> ReadHeadAsync(int maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 16);
        var scanFrom = 0;
        while (true)
        {
            SkipLeadingNewlines();
            var span = _buffer.AsSpan(_start, BufferedCount);
            var terminatorEnd = FindHeadEnd(span, scanFrom);
            if (terminatorEnd >= 0)
            {
                if (terminatorEnd > maxBytes)
                {
                    throw new HttpParseException("Message head is too large.", 431);
                }

                var head = span[..terminatorEnd].ToArray();
                _start += terminatorEnd;
                return head;
            }

            if (BufferedCount >= maxBytes)
            {
                throw new HttpParseException("Message head is too large.", 431);
            }

            // Re-scan a few bytes of overlap in case the terminator spans two reads.
            scanFrom = Math.Max(0, BufferedCount - 3);
            if (!await FillAsync(maxBytes + 4, cancellationToken).ConfigureAwait(false))
            {
                if (BufferedCount == 0)
                {
                    return null;
                }

                throw new HttpParseException("Connection closed in the middle of a message head.");
            }
        }
    }

    /// <summary>
    /// Reads one line terminated by LF (an optional preceding CR is dropped).
    /// Returns null at end of stream with nothing buffered.
    /// </summary>
    public async ValueTask<string?> ReadLineAsync(int maxBytes, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var span = _buffer.AsSpan(_start, BufferedCount);
            var newline = span.IndexOf((byte)'\n');
            if (newline >= 0)
            {
                var lineLength = newline > 0 && span[newline - 1] == '\r' ? newline - 1 : newline;
                var line = System.Text.Encoding.Latin1.GetString(span[..lineLength]);
                _start += newline + 1;
                return line;
            }

            if (BufferedCount >= maxBytes)
            {
                throw new HttpParseException("Line is too long.");
            }

            if (!await FillAsync(maxBytes + 2, cancellationToken).ConfigureAwait(false))
            {
                if (BufferedCount == 0)
                {
                    return null;
                }

                throw new HttpParseException("Connection closed in the middle of a line.");
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        if (BufferedCount > 0)
        {
            return CopyFromBuffer(buffer);
        }

        return _inner.Read(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return ValueTask.FromResult(0);
        }

        if (BufferedCount > 0)
        {
            return ValueTask.FromResult(CopyFromBuffer(buffer.Span));
        }

        return _inner.ReadAsync(buffer, cancellationToken);
    }

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(buffer, cancellationToken);

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_leaveOpen)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>Index just past the blank line ending the head, or -1.</summary>
    private static int FindHeadEnd(ReadOnlySpan<byte> span, int from)
    {
        for (var i = Math.Max(from, 0); i < span.Length; i++)
        {
            if (span[i] != '\n')
            {
                continue;
            }

            // LF LF  or  LF CR LF
            if (i + 1 < span.Length && span[i + 1] == '\n')
            {
                return i + 2;
            }

            if (i + 2 < span.Length && span[i + 1] == '\r' && span[i + 2] == '\n')
            {
                return i + 3;
            }
        }

        return -1;
    }

    private void SkipLeadingNewlines()
    {
        while (BufferedCount > 0 && _buffer[_start] is (byte)'\r' or (byte)'\n')
        {
            _start++;
        }
    }

    private int CopyFromBuffer(Span<byte> destination)
    {
        var count = Math.Min(destination.Length, BufferedCount);
        _buffer.AsSpan(_start, count).CopyTo(destination);
        _start += count;
        return count;
    }

    /// <summary>Reads more bytes from the inner stream. Returns false at end of stream.</summary>
    private async ValueTask<bool> FillAsync(int maxCapacity, CancellationToken cancellationToken)
    {
        if (_start > 0 && _end == _buffer.Length)
        {
            // Compact: move unread bytes to the front.
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, BufferedCount);
            _end -= _start;
            _start = 0;
        }
        else if (_start == _end)
        {
            _start = _end = 0;
        }

        if (_end == _buffer.Length)
        {
            if (_buffer.Length >= maxCapacity)
            {
                return true; // Full; caller enforces its own limit.
            }

            Array.Resize(ref _buffer, Math.Min(_buffer.Length * 2, Math.Max(maxCapacity, _buffer.Length + 1)));
        }

        var read = await _inner.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return false;
        }

        _end += read;
        return true;
    }
}
