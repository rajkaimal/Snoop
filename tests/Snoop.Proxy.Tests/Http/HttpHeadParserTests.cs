using System.Text;
using Snoop.Proxy.Http;

namespace Snoop.Proxy.Tests.Http;

public class HttpHeadParserTests
{
    [Fact]
    public void Parses_absolute_form_request()
    {
        var head = HttpHeadParser.ParseRequest(Bytes("GET http://example.com/a?b=1 HTTP/1.1\r\nHost: example.com\r\nAccept:  */*  \r\n\r\n"));

        Assert.Equal("GET", head.Method);
        Assert.Equal("http://example.com/a?b=1", head.Target);
        Assert.Equal("HTTP/1.1", head.Version);
        Assert.Equal("example.com", head.Headers.Get("host"));
        Assert.Equal("*/*", head.Headers.Get("Accept"));
    }

    [Fact]
    public void Parses_connect_authority_form()
    {
        var head = HttpHeadParser.ParseRequest(Bytes("CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n"));

        Assert.Equal("CONNECT", head.Method);
        Assert.Equal("example.com:443", head.Target);
    }

    [Fact]
    public void Accepts_bare_lf_line_endings()
    {
        var head = HttpHeadParser.ParseRequest(Bytes("GET / HTTP/1.0\nHost: a\n\n"));

        Assert.Equal("HTTP/1.0", head.Version);
        Assert.Equal("a", head.Headers.Get("Host"));
    }

    [Fact]
    public void Keeps_empty_header_values_and_colons_in_values()
    {
        var head = HttpHeadParser.ParseRequest(Bytes("GET / HTTP/1.1\r\nX-Empty:\r\nX-Url: http://a:1/b\r\n\r\n"));

        Assert.Equal(string.Empty, head.Headers.Get("X-Empty"));
        Assert.Equal("http://a:1/b", head.Headers.Get("X-Url"));
    }

    [Theory]
    [InlineData("GET / HTTP/1.1\r\nHost : a\r\n\r\n")] // whitespace before colon (smuggling vector)
    [InlineData("GET / HTTP/1.1\r\nHost: a\r\n folded\r\n\r\n")] // obsolete line folding
    [InlineData("GET / HTTP/1.1\r\nNoColon\r\n\r\n")]
    [InlineData("GET /\r\n\r\n")] // missing version
    [InlineData("GET  / HTTP/1.1\r\n\r\n")] // double space
    [InlineData("G(T / HTTP/1.1\r\n\r\n")] // bad method token
    [InlineData("GET /a b HTTP/1.1\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nBad\rName: x\r\n\r\n")] // bare CR
    public void Rejects_malformed_requests(string text)
    {
        var ex = Assert.Throws<HttpParseException>(() => HttpHeadParser.ParseRequest(Bytes(text)));
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public void Rejects_unsupported_version_with_505()
    {
        var ex = Assert.Throws<HttpParseException>(() => HttpHeadParser.ParseRequest(Bytes("GET / HTTP/2.0\r\n\r\n")));

        Assert.Equal(505, ex.StatusCode);
    }

    [Fact]
    public void Parses_response_with_reason()
    {
        var head = HttpHeadParser.ParseResponse(Bytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n"));

        Assert.Equal(404, head.StatusCode);
        Assert.Equal("Not Found", head.ReasonPhrase);
        Assert.Equal("0", head.Headers.Get("content-length"));
    }

    [Theory]
    [InlineData("HTTP/1.1 101\r\n\r\n", 101, "")]
    [InlineData("HTTP/1.1 200 \r\n\r\n", 200, "")]
    [InlineData("HTTP/1.0 302 Moved Temporarily\r\n\r\n", 302, "Moved Temporarily")]
    public void Parses_response_status_lines(string text, int status, string reason)
    {
        var head = HttpHeadParser.ParseResponse(Bytes(text));

        Assert.Equal(status, head.StatusCode);
        Assert.Equal(reason, head.ReasonPhrase);
    }

    [Theory]
    [InlineData("HTTP/1.1 20 OK\r\n\r\n")]
    [InlineData("HTTP/1.1 abc OK\r\n\r\n")]
    [InlineData("HTTP/1.1 099 Low\r\n\r\n")]
    [InlineData("HTTP/1.1\r\n\r\n")]
    public void Rejects_bad_status_lines(string text) =>
        Assert.Throws<HttpParseException>(() => HttpHeadParser.ParseResponse(Bytes(text)));

    [Fact]
    public void Writer_output_round_trips_through_parser()
    {
        var headers = new Snoop.Core.Http.HttpHeaderList([new("Host", "a"), new("X-Two", "1"), new("X-Two", "2")]);
        var bytes = HttpHeadWriter.SerializeRequest("POST", "/x", "HTTP/1.1", headers);

        var head = HttpHeadParser.ParseRequest(bytes);

        Assert.Equal("POST", head.Method);
        Assert.Equal(new[] { "1", "2" }, head.Headers.GetAll("X-Two"));
    }

    [Fact]
    public void Writer_strips_line_breaks_from_values()
    {
        var headers = new Snoop.Core.Http.HttpHeaderList([new("X-Evil", "a\r\nInjected: yes")]);

        var text = Encoding.Latin1.GetString(HttpHeadWriter.SerializeResponse("HTTP/1.1", 200, "OK", headers));

        Assert.DoesNotContain("\r\nInjected", text);
    }

    private static byte[] Bytes(string text) => Encoding.Latin1.GetBytes(text);
}
