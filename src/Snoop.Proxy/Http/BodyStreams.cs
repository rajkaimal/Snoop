using System.Globalization;
using System.Text;
using Snoop.Core.Bodies;

namespace Snoop.Proxy.Http;

/// <summary>Base for forward-only, read-only streams.</summary>
public abstract class ReadOnlyForwardStream : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>True once every byte of the body has been read.</summary>
    public abstract bool IsComplete { get; }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public abstract override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>Reads and discards the rest of the body, up to <paramref name="maxBytes"/>. Returns true if it reached the end.</summary>
    public async Task<bool> DrainAsync(long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        long drained = 0;
        while (!IsComplete)
        {
            if (drained > maxBytes)
            {
                return false;
            }

            var read = await ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            drained += read;
        }

        return IsComplete;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Reads exactly <c>length</c> bytes of a Content-Length body.</summary>
public sealed class ContentLengthReadStream(BufferedConnectionStream source, long length) : ReadOnlyForwardStream
{
    private long _remaining = length >= 0 ? length : throw new ArgumentOutOfRangeException(nameof(length));

    public override bool IsComplete => _remaining == 0;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remaining == 0 || buffer.IsEmpty)
        {
            return 0;
        }

        var toRead = (int)Math.Min(buffer.Length, _remaining);
        var read = await source.ReadAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            throw new IOException($"Connection closed with {_remaining} body bytes still expected.");
        }

        _remaining -= read;
        return read;
    }
}

/// <summary>Decodes a chunked transfer-coded body (RFC 9112 section 7.1). Trailers are read and dropped.</summary>
public sealed class ChunkedReadStream(BufferedConnectionStream source, int maxLineBytes = 8 * 1024) : ReadOnlyForwardStream
{
    private long _chunkRemaining;
    private bool _done;

    public override bool IsComplete => _done;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_done || buffer.IsEmpty)
        {
            return 0;
        }

        if (_chunkRemaining == 0)
        {
            _chunkRemaining = await ReadChunkSizeAsync(cancellationToken).ConfigureAwait(false);
            if (_chunkRemaining == 0)
            {
                await ReadTrailersAsync(cancellationToken).ConfigureAwait(false);
                _done = true;
                return 0;
            }
        }

        var toRead = (int)Math.Min(buffer.Length, _chunkRemaining);
        var read = await source.ReadAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            throw new IOException("Connection closed in the middle of a chunk.");
        }

        _chunkRemaining -= read;
        if (_chunkRemaining == 0)
        {
            var end = await source.ReadLineAsync(maxLineBytes, cancellationToken).ConfigureAwait(false);
            if (end is null || end.Length != 0)
            {
                throw new HttpParseException("Chunk data is not followed by CRLF.");
            }
        }

        return read;
    }

    private async ValueTask<long> ReadChunkSizeAsync(CancellationToken cancellationToken)
    {
        var line = await source.ReadLineAsync(maxLineBytes, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("Connection closed before the next chunk size.");

        // chunk-size [ chunk-ext ]
        var semicolon = line.IndexOf(';', StringComparison.Ordinal);
        var sizeText = (semicolon >= 0 ? line[..semicolon] : line).TrimEnd(' ', '\t');
        if (sizeText.Length is 0 or > 16
            || !long.TryParse(sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size)
            || size < 0)
        {
            throw new HttpParseException($"Invalid chunk size '{sizeText}'.");
        }

        return size;
    }

    private async ValueTask ReadTrailersAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 1000; i++)
        {
            var line = await source.ReadLineAsync(maxLineBytes, cancellationToken).ConfigureAwait(false);
            if (line is null || line.Length == 0)
            {
                return;
            }
        }

        throw new HttpParseException("Too many trailer fields.");
    }
}

/// <summary>Reads until the connection closes (a response with no length information).</summary>
public sealed class ReadToEndStream(Stream source) : ReadOnlyForwardStream
{
    private bool _done;

    public override bool IsComplete => _done;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_done || buffer.IsEmpty)
        {
            return 0;
        }

        var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _done = read == 0;
        return read;
    }
}

/// <summary>An empty body.</summary>
public sealed class EmptyBodyStream : ReadOnlyForwardStream
{
    public static EmptyBodyStream Instance { get; } = new();

    public override bool IsComplete => true;

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(0);
}

/// <summary>Passes reads through and copies every byte into a <see cref="CapturedBody"/>.</summary>
public sealed class CapturingReadStream(ReadOnlyForwardStream source, CapturedBody capture) : ReadOnlyForwardStream
{
    public override bool IsComplete => source.IsComplete;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            capture.Append(buffer.Span[..read]);
        }

        return read;
    }
}

/// <summary>Writes a chunked transfer-coded body. Call <see cref="CompleteAsync"/> to send the final chunk.</summary>
public sealed class ChunkedWriteStream(Stream destination) : Stream
{
    private static readonly byte[] Crlf = "\r\n"u8.ToArray();
    private static readonly byte[] LastChunk = "0\r\n\r\n"u8.ToArray();
    private bool _completed;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        if (buffer.IsEmpty)
        {
            return; // A zero-length chunk would end the body.
        }

        var size = Encoding.ASCII.GetBytes(buffer.Length.ToString("X", CultureInfo.InvariantCulture) + "\r\n");
        await destination.WriteAsync(size, cancellationToken).ConfigureAwait(false);
        await destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await destination.WriteAsync(Crlf, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        await destination.WriteAsync(LastChunk, cancellationToken).ConfigureAwait(false);
    }

    public override void Flush() => destination.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
