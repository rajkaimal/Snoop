using Snoop.Core.Diagnostics;

namespace Snoop.Core.Sessions;

/// <summary>What happened to a session, as reported by <see cref="SessionStore.SessionChanged"/>.</summary>
public enum SessionChange
{
    Added,
    Updated,
    Finished,
    Removed,
}

/// <summary>Event data for <see cref="SessionStore.SessionChanged"/>.</summary>
public sealed class SessionChangedEventArgs(Session session, SessionChange change) : EventArgs
{
    public Session Session { get; } = session;

    public SessionChange Change { get; } = change;
}

/// <summary>
/// Thread-safe, in-memory record of every session in the current capture.
/// Receives sessions from the proxy (it is an <see cref="ISessionSink"/>) and
/// tells listeners such as the UI what changed. When the store is full the
/// oldest finished sessions are dropped and their bodies released.
/// </summary>
public sealed class SessionStore : ISessionSink, IDisposable
{
    public const int DefaultCapacity = 20_000;

    private readonly Lock _gate = new();
    private readonly LinkedList<Session> _order = new();
    private readonly Dictionary<long, LinkedListNode<Session>> _byId = [];
    private readonly ISnoopLogger _logger;

    public SessionStore(int capacity = DefaultCapacity, ISnoopLogger? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Raised after every change. Handlers run on the proxy's threads and must not block.</summary>
    public event EventHandler<SessionChangedEventArgs>? SessionChanged;

    public int Capacity { get; }

    public int Count
    {
        get { lock (_gate) { return _order.Count; } }
    }

    public Session? Find(long id)
    {
        lock (_gate)
        {
            return _byId.TryGetValue(id, out var node) ? node.Value : null;
        }
    }

    /// <summary>A point-in-time copy of all sessions, oldest first.</summary>
    public IReadOnlyList<Session> Snapshot()
    {
        lock (_gate)
        {
            return [.. _order];
        }
    }

    /// <summary>
    /// Adds finished sessions from elsewhere (a snapshot file). Their numbers must be unique in this
    /// store; sessions whose number is already used are skipped. Returns how many were added.
    /// </summary>
    public int Import(IEnumerable<Session> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ISessionSink sink = this;
        var added = 0;
        foreach (var session in sessions)
        {
            if (Find(session.Id) is not null)
            {
                session.Dispose();
                continue;
            }

            sink.OnSessionStarted(session);
            sink.OnSessionFinished(session);
            added++;
        }

        return added;
    }

    public bool Remove(long id)
    {
        Session? removed;
        lock (_gate)
        {
            if (!_byId.Remove(id, out var node))
            {
                return false;
            }

            _order.Remove(node);
            removed = node.Value;
        }

        removed.Dispose();
        Raise(removed, SessionChange.Removed);
        return true;
    }

    /// <summary>Removes every session and releases their bodies.</summary>
    public void Clear()
    {
        List<Session> removed;
        lock (_gate)
        {
            removed = [.. _order];
            _order.Clear();
            _byId.Clear();
        }

        foreach (var session in removed)
        {
            session.Dispose();
            Raise(session, SessionChange.Removed);
        }
    }

    void ISessionSink.OnSessionStarted(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        List<Session>? evicted = null;
        lock (_gate)
        {
            if (_byId.ContainsKey(session.Id))
            {
                return;
            }

            _byId[session.Id] = _order.AddLast(session);
            while (_order.Count > Capacity)
            {
                var oldest = FindOldestEvictable();
                if (oldest is null)
                {
                    break;
                }

                _order.Remove(oldest);
                _byId.Remove(oldest.Value.Id);
                (evicted ??= []).Add(oldest.Value);
            }
        }

        Raise(session, SessionChange.Added);
        if (evicted is not null)
        {
            foreach (var old in evicted)
            {
                old.Dispose();
                Raise(old, SessionChange.Removed);
            }
        }
    }

    void ISessionSink.OnSessionUpdated(Session session) => RaiseIfKnown(session, SessionChange.Updated);

    void ISessionSink.OnSessionFinished(Session session) => RaiseIfKnown(session, SessionChange.Finished);

    public void Dispose() => Clear();

    private LinkedListNode<Session>? FindOldestEvictable()
    {
        // Prefer the oldest finished session; never drop the session just added.
        for (var node = _order.First; node is not null && node != _order.Last; node = node.Next)
        {
            if (node.Value.IsFinished)
            {
                return node;
            }
        }

        return _order.First != _order.Last ? _order.First : null;
    }

    private void RaiseIfKnown(Session session, SessionChange change)
    {
        bool known;
        lock (_gate)
        {
            known = _byId.ContainsKey(session.Id);
        }

        if (known)
        {
            Raise(session, change);
        }
    }

    private void Raise(Session session, SessionChange change)
    {
        var handler = SessionChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, new SessionChangedEventArgs(session, change));
        }
#pragma warning disable CA1031 // A faulty listener must never break capture.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.Error($"Session listener failed for session #{session.Id}.", ex);
        }
    }
}
