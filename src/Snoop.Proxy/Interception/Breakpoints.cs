using Snoop.Core.Sessions;

namespace Snoop.Proxy.Interception;

/// <summary>Where an exchange is paused.</summary>
public enum BreakpointStage
{
    /// <summary>Before the request goes to the server.</summary>
    Request,

    /// <summary>Before the response goes to the app.</summary>
    Response,
}

/// <summary>What the user chose for a paused exchange.</summary>
public enum BreakpointDecision
{
    /// <summary>Carry on with the (possibly edited) message.</summary>
    Continue,

    /// <summary>Drop it: the app gets an error (request stage) or a closed connection (response stage).</summary>
    Abort,
}

/// <summary>
/// An exchange held at a breakpoint. Edit <see cref="Request"/> or <see cref="Response"/> in place,
/// then call <see cref="Resume"/> or <see cref="Abort"/>. Thread-safe; only the first decision counts.
/// </summary>
public sealed class PausedExchange
{
    private readonly TaskCompletionSource<BreakpointDecision> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PausedExchange(Session session, EditableRequest request)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Stage = BreakpointStage.Request;
    }

    public PausedExchange(Session session, EditableRequest request, EditableResponse response)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Response = response ?? throw new ArgumentNullException(nameof(response));
        Stage = BreakpointStage.Response;
    }

    public Session Session { get; }

    public BreakpointStage Stage { get; }

    /// <summary>The request. Editable at the request stage; for reference at the response stage.</summary>
    public EditableRequest Request { get; }

    /// <summary>The response at the response stage; null at the request stage.</summary>
    public EditableResponse? Response { get; }

    public DateTimeOffset PausedAt { get; } = DateTimeOffset.Now;

    public bool IsResolved => _decision.Task.IsCompleted;

    internal Task<BreakpointDecision> Decision => _decision.Task;

    /// <summary>Lets the exchange continue with whatever edits were made.</summary>
    public bool Resume() => _decision.TrySetResult(BreakpointDecision.Continue);

    /// <summary>Drops the exchange.</summary>
    public bool Abort() => _decision.TrySetResult(BreakpointDecision.Abort);
}

/// <summary>
/// Breakpoints that pause every request and/or response whose URL contains <see cref="Filter"/>,
/// and the list of exchanges currently paused (by these or by rules). Thread-safe.
/// </summary>
public sealed class BreakpointController
{
    private readonly Lock _gate = new();
    private readonly List<PausedExchange> _pending = [];
    private volatile bool _breakOnRequests;
    private volatile bool _breakOnResponses;
    private volatile string _filter = string.Empty;

    /// <summary>Raised when an exchange pauses. On a proxy thread.</summary>
    public event EventHandler<PausedExchange>? Paused;

    /// <summary>Raised when a paused exchange is resumed, aborted or cancelled. Any thread.</summary>
    public event EventHandler<PausedExchange>? Resolved;

    public bool BreakOnRequests
    {
        get => _breakOnRequests;
        set => _breakOnRequests = value;
    }

    public bool BreakOnResponses
    {
        get => _breakOnResponses;
        set => _breakOnResponses = value;
    }

    /// <summary>Only URLs containing this text (ignoring case) pause. Empty pauses everything.</summary>
    public string Filter
    {
        get => _filter;
        set => _filter = value?.Trim() ?? string.Empty;
    }

    /// <summary>Exchanges waiting for the user, oldest first.</summary>
    public IReadOnlyList<PausedExchange> Pending
    {
        get { lock (_gate) { return [.. _pending]; } }
    }

    public bool Matches(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var filter = _filter;
        return filter.Length == 0 || url.AbsoluteUri.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Resumes every paused exchange. Returns how many there were.</summary>
    public int ResumeAll()
    {
        var count = 0;
        foreach (var exchange in Pending)
        {
            if (exchange.Resume())
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Waits until <paramref name="exchange"/> is resumed or aborted. Cancellation (the proxy stopping) aborts it.</summary>
    public async Task<BreakpointDecision> PauseAsync(PausedExchange exchange, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        lock (_gate)
        {
            _pending.Add(exchange);
        }

        try
        {
            Raise(Paused, exchange);
            using (cancellationToken.Register(static state => ((PausedExchange)state!).Abort(), exchange))
            {
                return await exchange.Decision.ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(exchange);
            }

            Raise(Resolved, exchange);
        }
    }

    private void Raise(EventHandler<PausedExchange>? handler, PausedExchange exchange)
    {
        try
        {
            handler?.Invoke(this, exchange);
        }
#pragma warning disable CA1031 // A faulty listener must not leave traffic stuck.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
