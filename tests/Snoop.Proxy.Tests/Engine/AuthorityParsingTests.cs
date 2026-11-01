using Snoop.Proxy.Engine;

namespace Snoop.Proxy.Tests.Engine;

public class AuthorityParsingTests
{
    [Theory]
    [InlineData("example.com:443", "example.com", 443)]
    [InlineData("127.0.0.1:8443", "127.0.0.1", 8443)]
    [InlineData("[::1]:443", "::1", 443)]
    [InlineData("my-host_1.local:1", "my-host_1.local", 1)]
    public void Parses_valid_authorities(string authority, string host, int port)
    {
        Assert.True(ClientConnection.TryParseAuthority(authority, out var parsedHost, out var parsedPort));
        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("example.com:")]
    [InlineData(":443")]
    [InlineData("example.com:0")]
    [InlineData("example.com:70000")]
    [InlineData("example.com:-1")]
    [InlineData("::1:443")]
    [InlineData("[::1]443")]
    [InlineData("[nothex]:443")]
    [InlineData("bad host:443")]
    public void Rejects_invalid_authorities(string authority)
    {
        Assert.False(ClientConnection.TryParseAuthority(authority, out _, out _));
    }
}
