using Snoop.Core.Sessions;

namespace Snoop.Core.Tests.Sessions;

public class SessionTests
{
    [Fact]
    public void New_session_is_running_with_no_duration()
    {
        var session = SessionStoreTests.NewSession(1);

        Assert.Equal(SessionState.RequestReceived, session.State);
        Assert.False(session.IsFinished);
        Assert.Null(session.Timings.Duration);
    }

    [Fact]
    public void Complete_sets_state_and_duration()
    {
        var session = SessionStoreTests.NewSession(1);

        session.Complete();

        Assert.Equal(SessionState.Completed, session.State);
        Assert.True(session.IsFinished);
        Assert.NotNull(session.Timings.Duration);
    }

    [Fact]
    public void Fail_records_the_reason()
    {
        var session = SessionStoreTests.NewSession(1);

        session.Fail("connection refused");

        Assert.Equal(SessionState.Failed, session.State);
        Assert.Equal("connection refused", session.Error);
    }

    [Fact]
    public void Timing_marks_are_set_once_and_ordered()
    {
        var timings = SessionStoreTests.NewSession(1).Timings;

        timings.MarkRequestSent();
        var first = timings.RequestSent;
        Thread.Sleep(5);
        timings.MarkRequestSent();
        timings.MarkResponseHeaders();
        timings.MarkCompleted();

        Assert.Equal(first, timings.RequestSent);
        Assert.True(timings.ResponseHeadersReceived >= timings.RequestSent);
        Assert.True(timings.Completed >= timings.ResponseHeadersReceived);
    }
}
