using Snoop.Core.Sessions;
using Snoop.Inspection.Filtering;
using Snoop.Presentation.Tests.TestSupport;
using Snoop.Presentation.ViewModels;

namespace Snoop.Presentation.Tests.ViewModels;

public sealed class SessionListViewModelTests : IAsyncLifetime
{
    private readonly FakeDispatcher _dispatcher = new();
    private TestHost _test = null!;
    private SessionListViewModel _list = null!;

    public Task InitializeAsync()
    {
        _test = new TestHost();
        _list = new SessionListViewModel(_test.Host.Sessions, _dispatcher);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _list.Dispose();
        await _test.DisposeAsync();
    }

    [Fact]
    public void New_sessions_appear_on_the_next_batch()
    {
        _test.AddSession();
        _test.AddSession();
        Assert.Empty(_list.Rows);

        _dispatcher.Tick();

        Assert.Equal(2, _list.Rows.Count);
        Assert.Equal(2, _list.TotalCount);
    }

    [Fact]
    public void Existing_sessions_are_loaded_at_construction()
    {
        _test.AddSession();
        using var list = new SessionListViewModel(_test.Host.Sessions, _dispatcher);

        Assert.Single(list.Rows);
    }

    [Fact]
    public void Rows_refresh_when_a_session_progresses()
    {
        var session = TestHost.NewSession();
        _test.Sink.OnSessionStarted(session);
        _dispatcher.Tick();
        var row = Assert.Single(_list.Rows);
        Assert.Equal("···", row.Result);
        var changes = new List<string?>();
        row.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        TestHost.Respond(session, 404, "text/html", "<p>missing</p>");
        session.Complete();
        _test.Sink.OnSessionFinished(session);
        _dispatcher.Tick();

        Assert.Equal("404", row.Result);
        Assert.Equal(StatusCategory.ClientError, row.Category);
        Assert.Equal("HTML", row.TypeBadge);
        Assert.Contains(string.Empty, changes);
    }

    [Fact]
    public void Filter_hides_and_shows_rows_in_order()
    {
        var a = _test.AddSession("https://a.example/1");
        _test.AddSession("https://b.example/2");
        var c = _test.AddSession("https://a.example/3");
        _dispatcher.Tick();

        _list.Filter = new SessionFilter { SearchText = "a.example" };
        Assert.Equal(new[] { a.Id, c.Id }, _list.Rows.Select(r => r.Id));
        Assert.Equal(2, _list.VisibleCount);
        Assert.Equal(3, _list.TotalCount);

        _list.Filter = SessionFilter.None;
        Assert.Equal(3, _list.Rows.Count);
    }

    [Fact]
    public void A_row_that_starts_matching_is_inserted_in_id_order()
    {
        var first = _test.AddSession(status: 500);
        var pending = TestHost.NewSession();
        _test.Sink.OnSessionStarted(pending);
        var last = _test.AddSession(status: 503);
        _list.Filter = new SessionFilter { OnlyErrors = true };
        _dispatcher.Tick();
        Assert.Equal(new[] { first.Id, last.Id }, _list.Rows.Select(r => r.Id));

        TestHost.Respond(pending, 502, "text/plain", "bad gateway");
        pending.Complete();
        _test.Sink.OnSessionFinished(pending);
        _dispatcher.Tick();

        Assert.Equal(new[] { first.Id, pending.Id, last.Id }, _list.Rows.Select(r => r.Id));
    }

    [Fact]
    public void Clear_empties_the_grid_and_selection()
    {
        _test.AddSession();
        _dispatcher.Tick();
        _list.SelectedRow = _list.Rows[0];

        _list.Clear();

        Assert.Empty(_list.Rows);
        Assert.Null(_list.SelectedRow);
        Assert.Equal(0, _test.Host.Sessions.Count);
    }

    [Fact]
    public void Remove_deletes_selected_rows()
    {
        var keep = _test.AddSession();
        _test.AddSession();
        _dispatcher.Tick();

        _list.Remove([_list.Rows[1]]);

        Assert.Equal(new[] { keep.Id }, _list.Rows.Select(r => r.Id));
    }

    [Fact]
    public void Auto_scroll_requests_the_newest_row()
    {
        SessionRowViewModel? scrolledTo = null;
        _list.ScrollRequested += (_, row) => scrolledTo = row;
        _test.AddSession();
        var newest = _test.AddSession();

        _dispatcher.Tick();
        Assert.Equal(newest.Id, scrolledTo?.Id);

        _list.AutoScroll = false;
        scrolledTo = null;
        _test.AddSession();
        _dispatcher.Tick();
        Assert.Null(scrolledTo);
    }

    [Fact]
    public void Selected_session_updates_are_announced()
    {
        var session = TestHost.NewSession();
        _test.Sink.OnSessionStarted(session);
        _dispatcher.Tick();
        _list.SelectedRow = _list.Rows[0];
        var announced = 0;
        _list.SelectedSessionUpdated += (_, _) => announced++;

        session.Complete();
        _test.Sink.OnSessionFinished(session);
        _dispatcher.Tick();

        Assert.Equal(1, announced);
    }

    [Fact]
    public void Row_values_describe_the_session()
    {
        var session = _test.AddSession("https://api.example.com:8443/v1/items?x=1");
        var tunnel = _test.AddSession("https://example.com:443/", status: null, kind: SessionKind.Connect);
        _dispatcher.Tick();

        var row = _list.Rows.Single(r => r.Id == session.Id);
        Assert.Equal("200", row.Result);
        Assert.Equal("api.example.com:8443", row.Host);
        Assert.Equal("/v1/items?x=1", row.Path);
        Assert.Equal("JSON", row.TypeBadge);
        Assert.Equal("application/json", row.ContentType);
        Assert.Equal("7 B", row.Size);
        Assert.Equal("chrome:42", row.Process);
        Assert.Equal(StatusCategory.Success, row.Category);

        var tunnelRow = _list.Rows.Single(r => r.Id == tunnel.Id);
        Assert.Equal("example.com:443", tunnelRow.Path);
        Assert.Equal(StatusCategory.Tunnel, tunnelRow.Category);
        Assert.Equal("Tunnel", tunnelRow.Protocol);
    }
}
