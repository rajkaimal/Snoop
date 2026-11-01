using Snoop.Core.Http;
using Snoop.Proxy.Http;

namespace Snoop.Proxy.Tests.Http;

public class MessageFramingTests
{
    [Fact]
    public void Request_without_length_has_no_body()
    {
        var framing = MessageFraming.ForRequest(Headers());

        Assert.False(framing.HasBody);
    }

    [Fact]
    public void Request_with_content_length()
    {
        var framing = MessageFraming.ForRequest(Headers(("Content-Length", "42")));

        Assert.Equal(BodyFraming.ContentLength, framing.Framing);
        Assert.Equal(42, framing.Length);
    }

    [Fact]
    public void Request_with_zero_length_has_no_body()
    {
        Assert.False(MessageFraming.ForRequest(Headers(("Content-Length", "0"))).HasBody);
    }

    [Fact]
    public void Request_with_chunked_encoding()
    {
        var framing = MessageFraming.ForRequest(Headers(("Transfer-Encoding", "gzip, chunked")));

        Assert.Equal(BodyFraming.Chunked, framing.Framing);
    }

    [Fact]
    public void Repeated_identical_content_length_is_accepted()
    {
        var framing = MessageFraming.ForRequest(Headers(("Content-Length", "5, 5"), ("Content-Length", "5")));

        Assert.Equal(5, framing.Length);
    }

    [Theory]
    [InlineData("Content-Length", "5", "Transfer-Encoding", "chunked")] // smuggling vector
    [InlineData("Content-Length", "5", "Content-Length", "6")]
    [InlineData("Content-Length", "-1", null, null)]
    [InlineData("Content-Length", "abc", null, null)]
    [InlineData("Content-Length", "", null, null)]
    public void Ambiguous_request_framing_is_rejected(string name1, string value1, string? name2, string? value2)
    {
        var headers = Headers((name1, value1));
        if (name2 is not null)
        {
            headers.Add(name2, value2!);
        }

        Assert.Throws<HttpParseException>(() => MessageFraming.ForRequest(headers));
    }

    [Fact]
    public void Request_transfer_encoding_not_ending_in_chunked_is_not_implemented()
    {
        var ex = Assert.Throws<HttpParseException>(() => MessageFraming.ForRequest(Headers(("Transfer-Encoding", "gzip"))));

        Assert.Equal(501, ex.StatusCode);
    }

    [Theory]
    [InlineData("HEAD", 200)]
    [InlineData("GET", 204)]
    [InlineData("GET", 304)]
    [InlineData("GET", 101)]
    public void Some_responses_never_have_a_body(string method, int status)
    {
        Assert.False(MessageFraming.ForResponse(method, status, Headers(("Content-Length", "10"))).HasBody);
    }

    [Fact]
    public void Response_without_length_reads_until_close()
    {
        Assert.Equal(BodyFraming.UntilClose, MessageFraming.ForResponse("GET", 200, Headers()).Framing);
    }

    [Fact]
    public void Response_with_chunked_encoding()
    {
        Assert.Equal(BodyFraming.Chunked, MessageFraming.ForResponse("GET", 200, Headers(("Transfer-Encoding", "chunked"))).Framing);
    }

    private static HttpHeaderList Headers(params (string Name, string Value)[] fields)
    {
        var list = new HttpHeaderList();
        foreach (var (name, value) in fields)
        {
            list.Add(name, value);
        }

        return list;
    }
}
