using System.Net;
using System.Text;
using Snoop.Core.Bodies;
using Snoop.Core.Http;
using Snoop.Core.Sessions;

namespace Snoop.Inspection.Tests;

/// <summary>Builds sessions for tests.</summary>
internal static class TestSessions
{
    private static long _nextId;

    public static Session Create(
        string method = "GET",
        string url = "https://example.com/api/items?page=2",
        HttpHeaderList? requestHeaders = null,
        byte[]? requestBody = null,
        int? status = 200,
        HttpHeaderList? responseHeaders = null,
        byte[]? responseBody = null,
        SessionKind kind = SessionKind.Http,
        string? process = "chrome")
    {
        var session = new Session(
            Interlocked.Increment(ref _nextId),
            kind,
            new ConnectionEndpoints(new IPEndPoint(IPAddress.Loopback, 50000), new IPEndPoint(IPAddress.Loopback, 8866)),
            process is null ? null : new ClientProcessInfo(1234, process),
            new RequestInfo(method, new Uri(url), "HTTP/1.1", requestHeaders ?? Headers(("Host", new Uri(url).Authority))) { Body = Body(requestBody) });

        if (status is not null)
        {
            session.Response = new ResponseInfo(status.Value, "OK", "HTTP/1.1", responseHeaders ?? new HttpHeaderList()) { Body = Body(responseBody) };
        }

        session.Timings.MarkRequestSent();
        session.Timings.MarkResponseHeaders();
        session.Complete();
        return session;
    }

    public static HttpHeaderList Headers(params (string Name, string Value)[] fields)
    {
        var list = new HttpHeaderList();
        foreach (var (name, value) in fields)
        {
            list.Add(name, value);
        }

        return list;
    }

    public static CapturedBody? Body(byte[]? bytes)
    {
        if (bytes is null)
        {
            return null;
        }

        var body = new CapturedBody();
        body.Append(bytes);
        body.Complete();
        return body;
    }

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
