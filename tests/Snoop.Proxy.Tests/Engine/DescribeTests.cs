using System.Security.Authentication;
using Snoop.Proxy.Engine;

namespace Snoop.Proxy.Tests.Engine;

public class DescribeTests
{
    [Fact]
    public void Drops_the_see_inner_exception_filler()
    {
        var ex = new AuthenticationException(
            "Authentication failed, see inner exception.",
            new IOException("The remote party sent a fatal alert"));

        Assert.Equal("The remote party sent a fatal alert.", ClientConnection.Describe(ex));
    }

    [Fact]
    public void Joins_distinct_messages_and_skips_repeats()
    {
        var ex = new HttpRequestException(
            "Connection refused (localhost:1)",
            new IOException("Connection refused"));

        Assert.Equal("Connection refused (localhost:1).", ClientConnection.Describe(ex));
    }

    [Fact]
    public void Keeps_a_lone_message()
    {
        Assert.Equal("Something broke.", ClientConnection.Describe(new IOException("Something broke.")));
    }

    [Fact]
    public void Timeouts_read_as_timeouts()
    {
        Assert.Equal("The operation timed out.", ClientConnection.Describe(new OperationCanceledException()));
    }
}
