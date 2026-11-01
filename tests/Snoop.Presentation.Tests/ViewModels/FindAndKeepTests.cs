using System.Text;
using Snoop.Core.Sessions;
using Snoop.Presentation.Settings;
using Snoop.Presentation.Tests.TestSupport;
using Snoop.Presentation.ViewModels;

namespace Snoop.Presentation.Tests.ViewModels;

/// <summary>HAR import, exporting selected sessions, text export, search navigation and host/app filters.</summary>
public sealed class FindAndKeepTests : IAsyncLifetime
{
    private readonly FakeDispatcher _dispatcher = new();
    private readonly FakeFiles _files = new();
    private readonly FakeOpenFiles _openFiles = new();
    private TestHost _test = null!;
    private MainViewModel _vm = null!;

    public Task InitializeAsync()
    {
        _test = new TestHost();
        Directory.CreateDirectory(_test.Directory);
        var settings = new AppSettings { Port = 0, CaptureOnStartup = false, HideTunnels = false };
        _vm = new MainViewModel(_test.Host, _dispatcher, new FakeClipboard(), _files, settings, null, _openFiles, null);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _vm.ShutdownAsync();
        _vm.Sessions.Dispose();
        await _test.DisposeAsync();
    }

    private void AddThree()
    {
        _test.AddSession("https://a.example/one", body: "{\"first\":1}");
        _test.AddSession("https://b.example/two", body: "{\"second\":2}");
        _test.AddSession("https://a.example/three", body: "{\"third\":3}");
        _dispatcher.Tick();
    }

    [Fact]
    public async Task Opens_a_har_file()
    {
        var path = Path.Combine(_test.Directory, "browser.har");
        await File.WriteAllTextAsync(path, """
            {"log":{"version":"1.2","entries":[
              {"startedDateTime":"2026-10-31T18:00:00Z","time":10,
               "request":{"method":"GET","url":"https://har.example/x","httpVersion":"h2","headers":[]},
               "response":{"status":200,"statusText":"","httpVersion":"h2","headers":[],"content":{"size":2,"mimeType":"text/plain","text":"hi"}},
               "timings":{"send":1,"wait":5,"receive":4}},
              {"request":{"url":"::bad::"}}]}}
            """);
        _openFiles.Path = path;

        await _vm.LoadSnapshotCommand.ExecuteAsync();
        _dispatcher.Tick();

        Assert.False(_vm.MessageIsError);
        Assert.Contains("Added 1 session", _vm.Message, StringComparison.Ordinal);
        Assert.Contains("1 entries could not be read", _vm.Message, StringComparison.Ordinal);
        var row = Assert.Single(_vm.Sessions.Rows);
        Assert.Equal("har.example", row.Host);
        Assert.True(row.Session.Flags.HasFlag(SessionFlags.Imported));
    }

    [Fact]
    public async Task Exports_only_the_selected_sessions()
    {
        AddThree();
        _vm.Sessions.SelectedRows = [_vm.Sessions.Rows[2], _vm.Sessions.Rows[0]];
        _vm.Sessions.SelectedRow = _vm.Sessions.Rows[0];

        Assert.Contains("2 selected", _vm.CountText, StringComparison.Ordinal);
        Assert.Equal("Remove 2 sessions (Del)", _vm.RemoveText);

        await _vm.ExportSelectedHarCommand.ExecuteAsync();
        var har = Encoding.UTF8.GetString(_files.Saved!);
        Assert.Contains("a.example/one", har, StringComparison.Ordinal);
        Assert.Contains("a.example/three", har, StringComparison.Ordinal);
        Assert.DoesNotContain("b.example", har, StringComparison.Ordinal);

        await _vm.ExportSelectedTextCommand.ExecuteAsync();
        var text = Encoding.UTF8.GetString(_files.Saved!);
        Assert.Contains("{\"third\":3}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("second", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("/one", StringComparison.Ordinal) < text.IndexOf("/three", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exports_every_shown_session_as_text()
    {
        AddThree();
        await _vm.ExportTextCommand.ExecuteAsync();

        Assert.Equal(".txt", Path.GetExtension(_files.SuggestedName));
        var text = Encoding.UTF8.GetString(_files.Saved!);
        Assert.Contains("second", text, StringComparison.Ordinal);
        Assert.Contains("Exported 3 session(s) as text", _vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_takes_every_selected_session()
    {
        AddThree();
        _vm.Sessions.SelectedRows = [_vm.Sessions.Rows[0], _vm.Sessions.Rows[1]];
        _vm.Sessions.SelectedRow = _vm.Sessions.Rows[0];

        _vm.RemoveSelectedCommand.Execute(null);

        Assert.Equal("a.example", Assert.Single(_vm.Sessions.Rows).Host);
        Assert.Equal(0, _vm.Sessions.SelectedCount);
        Assert.Null(_vm.Sessions.SelectedRow);
    }

    [Fact]
    public void Enter_steps_through_the_matches_and_wraps()
    {
        AddThree();
        _vm.SearchText = "a.example";
        Assert.Equal(2, _vm.Sessions.VisibleCount);

        Assert.Equal("/one", _vm.Sessions.SelectAdjacent(1)!.Path);
        Assert.Equal("/three", _vm.Sessions.SelectAdjacent(1)!.Path);
        Assert.Equal("/one", _vm.Sessions.SelectAdjacent(1)!.Path);
        Assert.Equal("/three", _vm.Sessions.SelectAdjacent(-1)!.Path);
        Assert.Same(_vm.Sessions.SelectedRow, Assert.Single(_vm.Sessions.SelectedRows));
        Assert.Equal("a.example", _vm.Sessions.Columns.HighlightText);
    }

    [Fact]
    public void Search_finds_text_in_bodies_unless_turned_off()
    {
        AddThree();
        _vm.SearchText = "second";
        Assert.Equal("b.example", Assert.Single(_vm.Sessions.Rows).Host);

        _vm.SearchBodies = false;
        Assert.Empty(_vm.Sessions.Rows);
    }

    [Fact]
    public void Only_this_host_and_only_this_app()
    {
        AddThree();
        Assert.False(_vm.IsOnlyHost);
        _vm.IsOnlyHost = true; // nothing selected yet
        Assert.False(_vm.IsOnlyHost);
        Assert.Contains("Select a session first", _vm.Message, StringComparison.Ordinal);

        _vm.Sessions.SelectedRow = _vm.Sessions.Rows[0];
        _vm.ShowOnlyHostCommand.Execute(null);
        Assert.True(_vm.HasScope);
        Assert.Equal("a.example", _vm.ScopeText);
        Assert.Equal("Only a.example", _vm.OnlyHostMenuText);
        Assert.Equal(2, _vm.Sessions.VisibleCount);

        _vm.Sessions.SelectedRow = _vm.Sessions.Rows[0];
        _vm.IsOnlyProcess = true;
        Assert.Equal("a.example · chrome", _vm.ScopeText);

        _vm.ClearScopeCommand.Execute(null);
        Assert.False(_vm.HasScope);
        Assert.Equal(3, _vm.Sessions.VisibleCount);
    }

    [Fact]
    public void Saved_filters_keep_the_host()
    {
        AddThree();
        _vm.Sessions.SelectedRow = _vm.Sessions.Rows[1];
        _vm.IsOnlyHost = true;
        _vm.SaveFilterCommand.Execute(null);
        _vm.ClearScopeCommand.Execute(null);

        var saved = Assert.Single(_vm.SavedFilters);
        Assert.Equal("b.example", saved.Name);
        _vm.ApplySavedFilter(saved);

        Assert.Equal("b.example", _vm.ScopeText);
        Assert.Equal(1, _vm.Sessions.VisibleCount);
    }
}
