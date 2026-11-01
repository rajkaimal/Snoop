namespace Snoop.Core.Sessions;

/// <summary>What kind of exchange a session records.</summary>
public enum SessionKind
{
    /// <summary>An ordinary HTTP request and response.</summary>
    Http,

    /// <summary>A CONNECT tunnel (HTTPS or other traffic through the proxy).</summary>
    Connect,

    /// <summary>A request that was upgraded to another protocol (usually WebSocket).</summary>
    Upgrade,
}

/// <summary>Where a session is in its life cycle.</summary>
public enum SessionState
{
    /// <summary>Request headers have arrived from the client.</summary>
    RequestReceived,

    /// <summary>The request has been sent to the server; waiting for its response.</summary>
    AwaitingResponse,

    /// <summary>Response headers have arrived; the body is streaming.</summary>
    ReceivingResponse,

    /// <summary>A tunnel or upgraded connection is open and relaying bytes.</summary>
    Tunneling,

    /// <summary>Finished normally.</summary>
    Completed,

    /// <summary>Ended with an error (see <see cref="Session.Error"/>).</summary>
    Failed,

    /// <summary>The client or server closed the connection before the exchange finished.</summary>
    Aborted,

    /// <summary>Held at a breakpoint, waiting for the user to resume it.</summary>
    Paused,
}

/// <summary>Extra markers shown in the UI.</summary>
[Flags]
public enum SessionFlags
{
    None = 0,

    /// <summary>The TLS traffic inside this tunnel was decrypted.</summary>
    Decrypted = 1,

    /// <summary>The request was served by Snoop itself (info page, certificate download).</summary>
    ServedLocally = 2,

    /// <summary>A rule or a breakpoint edit changed the request or response.</summary>
    Modified = 4,

    /// <summary>A rule answered the request; it never reached the server.</summary>
    AutoResponded = 8,

    /// <summary>The request was sent from Snoop's Composer.</summary>
    Composed = 16,

    /// <summary>The session stopped at a breakpoint.</summary>
    Breakpoint = 32,

    /// <summary>The session was opened from a snapshot or HAR file.</summary>
    Imported = 64,
}
