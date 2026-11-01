using System.Net;
using Snoop.Cli.Output;
using Snoop.Core.Http;
using Snoop.Core.Sessions;

namespace Snoop.Cli.Tests.Output;

public class SessionPrinterTests
{
    [Fact]
    public void Formats_a_completed_request()
    {
        var session = NewSession(SessionKind.Http, "GET", "https://example.com/api?x=1");
        session.Response = new ResponseInfo(200, "OK", "HTTP/2", new HttpHeaderList());
        session.Complete();

        var line = SessionPrinter.Format(session);

        Assert.StartsWith("#42", line, StringComparison.Ordinal);
        Assert.Contains("200", line, StringComparison.Ordinal);
        Assert.Contains("GET", line, StringComparison.Ordinal);
        Assert.Contains("https://example.com/api?x=1", line, StringComparison.Ordinal);
        Assert.Contains("chrome:1234", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_session_shows_the_error_on_a_second_line()
    {
        var session = NewSession(SessionKind.Http, "GET", "http://down.example/");
        session.Fail("Connection refused");

        var line = SessionPrinter.Format(session);

        Assert.Contains("ERR", line, StringComparison.Ordinal);
        Assert.Contains(Environment.NewLine, line, StringComparison.Ordinal);
        Assert.EndsWith("Connection refused", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Tunnel_shows_authority_and_byte_counts()
    {
        var session = NewSession(SessionKind.Connect, "CONNECT", "https://example.com:443/");
        session.BytesSent = 2048;
        session.BytesReceived = 4096;
        session.Complete();

        var line = SessionPrinter.Format(session);

        Assert.Contains("example.com:443", line, StringComparison.Ordinal);
        Assert.Contains("2 KB up / 4 KB down", line, StringComparison.Ordinal);
    }

    private static Session NewSession(SessionKind kind, string method, string url) =>
        new(
            42,
            kind,
            new ConnectionEndpoints(new IPEndPoint(IPAddress.Loopback, 50000), new IPEndPoint(IPAddress.Loopback, 8866)),
            new ClientProcessInfo(1234, "chrome"),
            new RequestInfo(method, new Uri(url), "HTTP/1.1", new HttpHeaderList()));
}
