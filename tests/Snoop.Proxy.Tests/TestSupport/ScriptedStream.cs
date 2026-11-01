using System.Text;

namespace Snoop.Proxy.Tests.TestSupport;

/// <summary>
/// A read-only stream that hands out its data in small fragments, to prove parsers
/// cope with messages split across many reads. Records anything written to it.
/// </summary>
internal sealed class ScriptedStream(byte[] data, int fragmentSize = 1) : Stream
{
    private int _position;

    public ScriptedStream(string text, int fragmentSize = 1)
        : this(Encoding.Latin1.GetBytes(text), fragmentSize)
    {
    }

    public MemoryStream Written { get; } = new();

    public int ReadCalls { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => data.Length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ReadCalls++;
        var count = Math.Min(Math.Min(buffer.Length, fragmentSize), data.Length - _position);
        data.AsSpan(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
