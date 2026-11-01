using System.Net;
using System.Security.Authentication;
using Snoop.Core.Bodies;
using Snoop.Core.Http;

namespace Snoop.Core.Sessions;

/// <summary>A captured request: what the client sent, before any changes.</summary>
public sealed record RequestInfo(
    string Method,
    Uri Url,
    string HttpVersion,
    HttpHeaderList Headers)
{
    /// <summary>The recorded body, or null when the request had none.</summary>
    public CapturedBody? Body { get; init; }
}

/// <summary>A captured response: what the server returned.</summary>
public sealed record ResponseInfo(
    int StatusCode,
    string ReasonPhrase,
    string HttpVersion,
    HttpHeaderList Headers)
{
    /// <summary>The recorded body, or null when the response had none.</summary>
    public CapturedBody? Body { get; init; }
}

/// <summary>TLS details of a tunnel.</summary>
public sealed record TlsInfo(
    string? ServerName,
    SslProtocols Protocol,
    string? CipherSuite,
    bool Decrypted);

/// <summary>The local process that opened the connection.</summary>
public sealed record ClientProcessInfo(int ProcessId, string Name)
{
    public override string ToString() => $"{Name}:{ProcessId}";
}

/// <summary>The TCP endpoints of the client connection.</summary>
public sealed record ConnectionEndpoints(IPEndPoint Client, IPEndPoint Proxy);
