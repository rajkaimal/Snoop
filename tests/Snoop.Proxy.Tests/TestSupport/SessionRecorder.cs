using System.Collections.Concurrent;
using Snoop.Core.Sessions;

namespace Snoop.Proxy.Tests.TestSupport;

/// <summary>Collects sessions from the proxy and lets tests wait for one to finish.</summary>
internal sealed class SessionRecorder : ISessionSink
{
    private readonly ConcurrentDictionary<long, Session> _sessions = new();
    private readonly List<(Func<Session, bool> Match, TaskCompletionSource<Session> Done)> _waiters = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<Session> Sessions => [.. _sessions.Values.OrderBy(s => s.Id)];

    public void OnSessionStarted(Session session) => _sessions[session.Id] = session;

    public void OnSessionUpdated(Session session)
    {
    }

    public void OnSessionFinished(Session session)
    {
        lock (_gate)
        {
            foreach (var waiter in _waiters.Where(w => w.Match(session)).ToList())
            {
                waiter.Done.TrySetResult(session);
                _waiters.Remove(waiter);
            }
        }
    }

    /// <summary>Waits for a finished session that matches, including ones that finished already.</summary>
    public async Task<Session> WaitForFinishedAsync(Func<Session, bool> match, int timeoutMs = 10_000)
    {
        TaskCompletionSource<Session> done;
        lock (_gate)
        {
            var existing = _sessions.Values.OrderBy(s => s.Id).FirstOrDefault(s => s.IsFinished && match(s));
            if (existing is not null)
            {
                return existing;
            }

            done = new TaskCompletionSource<Session>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((match, done));
        }

        var finished = await Task.WhenAny(done.Task, Task.Delay(timeoutMs));
        if (finished != done.Task)
        {
            var seen = string.Join(", ", Sessions.Select(s => s.ToString()));
            throw new TimeoutException($"No matching session finished within {timeoutMs} ms. Seen: {seen}");
        }

        return await done.Task;
    }

    public Task<Session> WaitForFinishedAsync(string pathAndQuery, int timeoutMs = 10_000) =>
        WaitForFinishedAsync(s => s.Kind != SessionKind.Connect && s.Request.Url.PathAndQuery == pathAndQuery, timeoutMs);
}
