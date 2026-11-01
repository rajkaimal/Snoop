using System.Net;
using Snoop.Core.Http;
using Snoop.Core.Sessions;

namespace Snoop.Core.Tests.Sessions;

public class SessionStoreTests
{
    [Fact]
    public void Started_session_is_added_and_announced()
    {
        using var store = new SessionStore();
        var changes = new List<(long, SessionChange)>();
        store.SessionChanged += (_, e) => changes.Add((e.Session.Id, e.Change));
        ISessionSink sink = store;
        var session = NewSession(1);

        sink.OnSessionStarted(session);
        sink.OnSessionUpdated(session);
        sink.OnSessionFinished(session);

        Assert.Equal(1, store.Count);
        Assert.Same(session, store.Find(1));
        Assert.Equal(new[] { (1L, SessionChange.Added), (1L, SessionChange.Updated), (1L, SessionChange.Finished) }, changes);
    }

    [Fact]
    public void Updates_for_unknown_sessions_are_ignored()
    {
        using var store = new SessionStore();
        var raised = 0;
        store.SessionChanged += (_, _) => raised++;

        ((ISessionSink)store).OnSessionUpdated(NewSession(99));

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Full_store_evicts_the_oldest_finished_session_first()
    {
        using var store = new SessionStore(capacity: 2);
        ISessionSink sink = store;
        var running = NewSession(1);
        var finished = NewSession(2);
        finished.Complete();
        sink.OnSessionStarted(running);
        sink.OnSessionStarted(finished);

        sink.OnSessionStarted(NewSession(3));

        Assert.Equal(new[] { 1L, 3L }, store.Snapshot().Select(s => s.Id));
    }

    [Fact]
    public void Full_store_with_nothing_finished_drops_the_oldest()
    {
        using var store = new SessionStore(capacity: 2);
        ISessionSink sink = store;
        sink.OnSessionStarted(NewSession(1));
        sink.OnSessionStarted(NewSession(2));

        sink.OnSessionStarted(NewSession(3));

        Assert.Equal(new[] { 2L, 3L }, store.Snapshot().Select(s => s.Id));
    }

    [Fact]
    public void Clear_removes_everything_and_announces_it()
    {
        using var store = new SessionStore();
        ISessionSink sink = store;
        sink.OnSessionStarted(NewSession(1));
        sink.OnSessionStarted(NewSession(2));
        var removed = 0;
        store.SessionChanged += (_, e) => removed += e.Change == SessionChange.Removed ? 1 : 0;

        store.Clear();

        Assert.Equal(0, store.Count);
        Assert.Equal(2, removed);
    }

    [Fact]
    public void Remove_returns_false_for_unknown_id()
    {
        using var store = new SessionStore();
        ((ISessionSink)store).OnSessionStarted(NewSession(1));

        Assert.True(store.Remove(1));
        Assert.False(store.Remove(1));
    }

    [Fact]
    public void A_throwing_listener_does_not_break_the_store()
    {
        using var store = new SessionStore();
        store.SessionChanged += (_, _) => throw new InvalidOperationException("boom");

        ((ISessionSink)store).OnSessionStarted(NewSession(1));

        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Adding_the_same_id_twice_keeps_one()
    {
        using var store = new SessionStore();
        var session = NewSession(7);

        ((ISessionSink)store).OnSessionStarted(session);
        ((ISessionSink)store).OnSessionStarted(session);

        Assert.Equal(1, store.Count);
    }

    internal static Session NewSession(long id) =>
        new(
            id,
            SessionKind.Http,
            new ConnectionEndpoints(new IPEndPoint(IPAddress.Loopback, 50000), new IPEndPoint(IPAddress.Loopback, 8866)),
            null,
            new RequestInfo("GET", new Uri("http://example.com/"), "HTTP/1.1", new HttpHeaderList()));
}
