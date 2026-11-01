using System.Text;
using Snoop.Core.Bodies;

namespace Snoop.Core.Tests.Bodies;

public sealed class CapturedBodyTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "snoop-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Small_body_stays_in_memory()
    {
        using var body = new CapturedBody(Options(threshold: 1024));

        body.Append("hello"u8);
        body.Append(" world"u8);
        body.Complete();

        Assert.False(body.IsSpilledToDisk);
        Assert.Equal("hello world", Encoding.ASCII.GetString(body.ToArray()));
        Assert.Equal(11, body.TotalBytes);
        Assert.False(body.IsTruncated);
        Assert.True(body.IsCompleted);
    }

    [Fact]
    public void Large_body_spills_to_disk_and_reads_back_intact()
    {
        var data = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();
        using var body = new CapturedBody(Options(threshold: 1000));

        for (var offset = 0; offset < data.Length; offset += 700)
        {
            body.Append(data.AsSpan(offset, Math.Min(700, data.Length - offset)));
        }

        body.Complete();

        Assert.True(body.IsSpilledToDisk);
        Assert.Equal(data, body.ToArray());
        using var stream = body.OpenRead();
        Assert.Equal(data.Length, stream.Length);
    }

    [Fact]
    public void Bytes_past_the_limit_are_counted_but_not_stored()
    {
        using var body = new CapturedBody(Options(threshold: 1024, max: 10));

        body.Append("0123456789ABCDEF"u8);

        Assert.Equal(16, body.TotalBytes);
        Assert.Equal(10, body.StoredBytes);
        Assert.True(body.IsTruncated);
        Assert.Equal("0123456789", Encoding.ASCII.GetString(body.ToArray()));
    }

    [Fact]
    public void Appends_after_complete_are_ignored()
    {
        using var body = new CapturedBody(Options(threshold: 1024));
        body.Append("abc"u8);
        body.Complete();

        body.Append("def"u8);

        Assert.Equal(3, body.TotalBytes);
    }

    [Fact]
    public void Dispose_deletes_the_spill_file()
    {
        var body = new CapturedBody(Options(threshold: 4));
        body.Append("more than four bytes"u8);
        body.Complete();
        Assert.Single(Directory.GetFiles(_tempDirectory));

        body.Dispose();

        Assert.Empty(Directory.GetFiles(_tempDirectory));
    }

    [Fact]
    public void ToArray_respects_max_bytes()
    {
        using var body = new CapturedBody(Options(threshold: 1024));
        body.Append("abcdef"u8);

        Assert.Equal("abc", Encoding.ASCII.GetString(body.ToArray(3)));
    }

    [Fact]
    public void Empty_body_reads_as_empty()
    {
        using var body = new CapturedBody(Options(threshold: 1024));
        body.Complete();

        Assert.Empty(body.ToArray());
        Assert.Equal(0, body.TotalBytes);
    }

    private BodyCaptureOptions Options(int threshold, long max = 1024 * 1024) =>
        new() { MemoryThresholdBytes = threshold, MaxCapturedBytes = max, TempDirectory = _tempDirectory };
}
