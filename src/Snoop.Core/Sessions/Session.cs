namespace Snoop.Core.Sessions;

/// <summary>
/// One recorded exchange: an HTTP request and response, a CONNECT tunnel, or an
/// upgraded connection. The proxy engine fills it in as the exchange progresses;
/// everyone else reads it.
/// </summary>
public sealed class Session : IDisposable
{
    private volatile SessionState _state;

    internal Session(long id, SessionKind kind, ConnectionEndpoints endpoints, ClientProcessInfo? process, RequestInfo request)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(request);
        Id = id;
        Kind = kind;
        Endpoints = endpoints;
        ClientProcess = process;
        Request = request;
        _state = SessionState.RequestReceived;
    }

    /// <summary>Sequence number, unique within one capture host (see <see cref="SessionIdSource"/>).</summary>
    public long Id { get; }

    public SessionKind Kind { get; internal set; }

    public SessionState State
    {
        get => _state;
        internal set => _state = value;
    }

    public ConnectionEndpoints Endpoints { get; }

    public ClientProcessInfo? ClientProcess { get; }

    /// <summary>The request as sent to the server (after any rule or breakpoint edits).</summary>
    public RequestInfo Request { get; internal set; }

    /// <summary>The request as the client sent it, when a rule or breakpoint changed it; otherwise null.</summary>
    public RequestInfo? OriginalRequest { get; internal set; }

    public ResponseInfo? Response { get; internal set; }

    public TlsInfo? Tls { get; internal set; }

    public SessionFlags Flags { get; internal set; }

    public SessionTimings Timings { get; private set; } = new();

    /// <summary>Human-readable reason when <see cref="State"/> is Failed or Aborted.</summary>
    public string? Error { get; internal set; }

    /// <summary>An informational hint about a session that otherwise looks normal.</summary>
    public string? Note { get; internal set; }

    /// <summary>What changed this session, e.g. "Rule 'Mock login'; edited at a breakpoint". Null when nothing did.</summary>
    public string? ModifiedBy { get; internal set; }

    /// <summary>Set on requests sent from the Composer, so the Composer can find its own session.</summary>
    public string? ComposerId { get; internal set; }

    /// <summary>Messages of an upgraded WebSocket connection, or null for other sessions.</summary>
    public WebSocketLog? WebSocket { get; internal set; }

    /// <summary>For tunnels and upgraded connections: bytes relayed client to server.</summary>
    public long BytesSent { get; internal set; }

    /// <summary>For tunnels and upgraded connections: bytes relayed server to client.</summary>
    public long BytesReceived { get; internal set; }

    public bool IsFinished => State is SessionState.Completed or SessionState.Failed or SessionState.Aborted;

    /// <summary>Replaces the timings with saved ones (snapshot import).</summary>
    internal void RestoreTimings(SessionTimings timings) => Timings = timings;

    /// <summary>Records that <paramref name="what"/> changed this session.</summary>
    internal void AddModifiedBy(string what)
    {
        Flags |= SessionFlags.Modified;
        if (ModifiedBy is null)
        {
            ModifiedBy = what;
        }
        else if (!ModifiedBy.Split("; ").Contains(what, StringComparer.Ordinal))
        {
            ModifiedBy = $"{ModifiedBy}; {what}";
        }
    }

    internal void Complete()
    {
        State = SessionState.Completed;
        Timings.MarkCompleted();
    }

    internal void Fail(string error, SessionState state = SessionState.Failed)
    {
        Error = error;
        State = state;
        Timings.MarkCompleted();
    }

    /// <summary>Releases recorded bodies (and their temp files).</summary>
    public void Dispose()
    {
        Request.Body?.Dispose();
        OriginalRequest?.Body?.Dispose();
        Response?.Body?.Dispose();
    }

    public override string ToString() => $"#{Id} {Request.Method} {Request.Url} [{State}]";
}
