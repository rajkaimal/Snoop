using Snoop.Core.Formatting;

namespace Snoop.Core.Tests.Formatting;

public class ByteSizeTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(89047L, "86.96 KB")]
    [InlineData(150L * 1024, "150 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    [InlineData(-2048L, "-2 KB")]
    public void Formats_for_people(long bytes, string expected) => Assert.Equal(expected, ByteSize.Format(bytes));
}
