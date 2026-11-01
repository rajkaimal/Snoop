using Snoop.Core.Http;
using Snoop.Proxy.Http;

namespace Snoop.Proxy.Tests.Http;

public class HopByHopHeadersTests
{
    [Fact]
    public void Strips_standard_and_connection_listed_fields()
    {
        var headers = new HttpHeaderList(
        [
            new("Host", "a"),
            new("Connection", "keep-alive, X-Secret"),
            new("Keep-Alive", "timeout=5"),
            new("Proxy-Connection", "keep-alive"),
            new("Proxy-Authorization", "Basic xyz"),
            new("Transfer-Encoding", "chunked"),
            new("X-Secret", "hop"),
            new("Accept", "*/*"),
        ]);

        var stripped = HopByHopHeaders.Strip(headers);

        Assert.Equal(new[] { "Host", "Accept" }, stripped.Select(h => h.Name));
    }

    [Fact]
    public void Does_not_modify_the_original()
    {
        var headers = new HttpHeaderList([new("Connection", "close"), new("A", "1")]);

        HopByHopHeaders.Strip(headers);

        Assert.Equal(2, headers.Count);
    }
}
