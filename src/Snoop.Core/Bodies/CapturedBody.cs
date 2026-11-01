namespace Snoop.Core.Bodies;

/// <summary>
/// A recorded message body. Written once by the proxy while bytes flow through,
/// then read any number of times. Bytes appended after <see cref="Complete"/> are ignored. Small bodies live in memory; large ones spill
/// to a temp file. Bytes past <see cref="BodyCaptureOptions.MaxCapturedBytes"/> are counted
/// but not stored.
/// </summary>
public sealed class CapturedBody : IDisposable
{
    private readonly BodyCaptureOptions _options;
    private readonly Lock _gate = new();
    private MemoryStream? _memory = new();
    private FileStream? _file;
    private string? _filePath;
    private long _storedBytes;
    private long _totalBytes;
    private bool _completed;
    private bool _disposed;

    public CapturedBody(BodyCaptureOptions? options = null) => _options = options ?? BodyCaptureOptions.Default;

    /// <summary>A completed body holding <paramref name="bytes"/> (bodies made by rules, edits, snapshots).</summary>
    /// <param name="bytes">The stored bytes.</param>
    /// <param name="totalBytes">The original size when <paramref name="bytes"/> is only the start of it; null for the same size.</param>
    /// <param name="options">Where large bodies spill to.</param>
    public static CapturedBody FromBytes(ReadOnlySpan<byte> bytes, long? totalBytes = null, BodyCaptureOptions? options = null)
    {
        var body = new CapturedBody(options);
        body.Append(bytes);
        lock (body._gate)
        {
            if (totalBytes is { } total && total > body._totalBytes)
            {
                body._totalBytes = total;
            }
        }

        body.Complete();
        return body;
    }

    /// <summary>Total bytes seen, including any that were not stored.</summary>
    public long TotalBytes
    {
        get { lock (_gate) { return _totalBytes; } }
    }

    /// <summary>Bytes actually stored and readable through <see cref="OpenRead"/>.</summary>
    public long StoredBytes
    {
        get { lock (_gate) { return _storedBytes; } }
    }

    /// <summary>True when the body was larger than the capture limit.</summary>
    public bool IsTruncated
    {
        get { lock (_gate) { return _totalBytes > _storedBytes; } }
    }

    public bool IsCompleted
    {
        get { lock (_gate) { return _completed; } }
    }

    /// <summary>True when the stored bytes live in a temp file rather than memory.</summary>
    public bool IsSpilledToDisk
    {
        get { lock (_gate) { return _filePath is not null; } }
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            if (_completed || _disposed)
            {
                // Late bytes after the exchange ended (e.g. a request body still draining) are ignored.
                return;
            }

            _totalBytes += data.Length;
            var room = _options.MaxCapturedBytes - _storedBytes;
            if (room <= 0)
            {
                return;
            }

            var slice = data.Length > room ? data[..(int)room] : data;
            if (_memory is not null && _memory.Length + slice.Length > _options.MemoryThresholdBytes)
            {
                SpillToDisk();
            }

            if (_file is not null)
            {
                _file.Write(slice);
            }
            else
            {
                _memory!.Write(slice);
            }

            _storedBytes += slice.Length;
        }
    }

    /// <summary>Marks the body as finished. No more bytes may be appended.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (_completed || _disposed)
            {
                return;
            }

            _completed = true;
            _file?.Flush();
        }
    }

    /// <summary>Opens an independent read-only view of the stored bytes.</summary>
    public Stream OpenRead()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_filePath is not null)
            {
                _file!.Flush();
                return new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }

            return new MemoryStream(_memory!.GetBuffer(), 0, (int)_memory.Length, writable: false);
        }
    }

    /// <summary>Copies up to <paramref name="maxBytes"/> stored bytes into an array.</summary>
    public byte[] ToArray(int maxBytes = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        using var stream = OpenRead();
        var length = (int)Math.Min(maxBytes, stream.Length);
        var result = new byte[length];
        stream.ReadExactly(result);
        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _memory?.Dispose();
            _memory = null;
            _file?.Dispose();
            _file = null;
            if (_filePath is not null)
            {
                TryDelete(_filePath);
            }
        }
    }

    private void SpillToDisk()
    {
        Directory.CreateDirectory(_options.TempDirectory);
        _filePath = Path.Combine(_options.TempDirectory, $"body-{Guid.NewGuid():N}.bin");
        _file = new FileStream(_filePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
        _memory!.Position = 0;
        _memory.CopyTo(_file);
        _memory.Dispose();
        _memory = null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Another reader still has it open; the temp sweep at startup removes it later.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
