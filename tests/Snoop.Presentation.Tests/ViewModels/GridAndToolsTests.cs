using System.Text;
using Snoop.Core.Http;
using Snoop.Core.Sessions;
using Snoop.Inspection.Comparing;
using Snoop.Presentation.Settings;
using Snoop.Presentation.Tests.TestSupport;
using Snoop.Presentation.ViewModels;

namespace Snoop.Presentation.Tests.ViewModels;

/// <summary>Columns, sorting, WebSocket messages, timeline, compare and settings.</summary>
public sealed class GridAndToolsTests : IAsyncLifetime
{
    private readonly FakeDispatcher _dispatcher = new();
    private TestHost _test = null!;
    private MainViewModel _vm = null!;
    private string _settingsPath = null!;

    public Task InitializeAsync()
    {
        _test = new TestHost();
        Directory.CreateDirectory(_test.Directory);
        _settingsPath = Path.Combine(_test.Directory, "settings.json");
        var settings = new AppSettings { Port = 0, CaptureOnStartup = false, HideTunnels = false, UseSystemProxy = false };
        _vm = new MainViewModel(_test.Host, _dispatcher, new FakeClipboard(), new FakeFiles(), settings, _settingsPath, new FakeOpenFiles(), null);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _vm.ShutdownAsync();
        _vm.Sessions.Dispose();
        await _test.DisposeAsync();
    }

    private Session Add(string url, int status = 200, string body = "x")
    {
        var session = _test.AddSession(url, status: status, body: body);
        _dispatcher.Tick();
        return session;
    }

    // ------------------------------------------------------------ Columns and sorting

    [Fact]
    public void Columns_resize_hide_and_are_saved()
    {
        var columns = _vm.Sessions.Columns;

        columns.Resize(SessionColumn.Host, 50);
        columns.ProcessVisible = false;
        columns.Resize(SessionColumn.Size, -1000);
        columns.CommitResize();

        Assert.Equal(230, columns.HostWidth);
        Assert.Equal(0, columns.ProcessWidth);
        Assert.Equal(SessionColumnsViewModel.MinWidth, columns.SizeWidth);
        var saved = AppSettings.Load(_settingsPath);
        Assert.Contains(saved.Columns, c => c.Name == "Host" && c.Width == 230);
        Assert.Contains(saved.Columns, c => c.Name == "Process" && !c.Visible);

        columns.Reset();
        Assert.Equal(180, columns.HostWidth);
        Assert.True(columns.ProcessVisible);
    }

    [Fact]
    public void Id_column_cannot_be_hidden()
    {
        _vm.Sessions.Columns.IdVisible = false;

        Assert.True(_vm.Sessions.Columns.IdVisible);
    }

    [Fact]
    public void Sorting_cycles_ascending_descending_and_back_to_arrival_order()
    {
        Add("https://b.example/x", body: "1234567890");
        Add("https://c.example/x", body: "1");
        Add("https://a.example/x", body: "12345");

        _vm.Sessions.SortBy(SessionColumn.Host);
        Assert.Equal(new[] { "a.example", "b.example", "c.example" }, _vm.Sessions.Rows.Select(r => r.Host));
        Assert.Equal("Host ▲", _vm.Sessions.Columns.HostHeader);

        _vm.Sessions.SortBy(SessionColumn.Host);
        Assert.Equal(new[] { "c.example", "b.example", "a.example" }, _vm.Sessions.Rows.Select(r => r.Host));
        Assert.Equal("Host ▼", _vm.Sessions.Columns.HostHeader);

        _vm.Sessions.SortBy(SessionColumn.Host);
        Assert.Equal(new[] { "b.example", "c.example", "a.example" }, _vm.Sessions.Rows.Select(r => r.Host));
        Assert.Equal("Host", _vm.Sessions.Columns.HostHeader);

        _vm.Sessions.SortBy(SessionColumn.Size);
        Assert.Equal(new[] { "c.example", "a.example", "b.example" }, _vm.Sessions.Rows.Select(r => r.Host));
        Assert.Equal("Size", AppSettings.Load(_settingsPath).SortColumn);
    }

    [Fact]
    public void New_and_changed_sessions_keep_the_sort_order()
    {
        _vm.Sessions.SortBy(SessionColumn.Result);
        Add("https://example.com/ok", status: 200);
        Add("https://example.com/missing", status: 404);
        var running = TestHost.NewSession("https://example.com/slow");
        _test.Sink.OnSessionStarted(running);
        _dispatcher.Tick();
        Add("https://example.com/redirect", status: 302);

        Assert.Equal(new[] { "/ok", "/redirect", "/missing", "/slow" }, _vm.Sessions.Rows.Select(r => r.Path));

        TestHost.Respond(running, 201, "text/plain", "done");
        running.Complete();
        _test.Sink.OnSessionFinished(running);
        _dispatcher.Tick();

        Assert.Equal(new[] { "/ok", "/slow", "/redirect", "/missing" }, _vm.Sessions.Rows.Select(r => r.Path));
    }

    [Fact]
    public void Saved_sort_and_columns_are_restored()
    {
        new AppSettings
        {
            SortColumn = "Time",
            SortDescending = true,
            Columns = [new ColumnSetting("Method", 99, false)],
        }.Save(_settingsPath);

        var vm = new MainViewModel(_test.Host, _dispatcher, new FakeClipboard(), new FakeFiles(), AppSettings.Load(_settingsPath));

        Assert.Equal(new SessionSort(SessionColumn.Time, true), vm.Sessions.Sort);
        Assert.False(vm.Sessions.Columns.MethodVisible);
        Assert.Equal(0, vm.Sessions.Columns.MethodWidth);
        vm.Sessions.Dispose();
    }

    // ------------------------------------------------------------ Saved filters

    [Fact]
    public void Filters_can_be_saved_applied_and_deleted()
    {
        _vm.SearchText = "chrome /api";
        _vm.OnlyErrors = true;
        _vm.NewFilterName = "api errors";

        _vm.SaveFilterCommand.Execute(null);
        _vm.SearchText = string.Empty;
        _vm.OnlyErrors = false;
        _vm.ApplySavedFilter(Assert.Single(_vm.SavedFilters));

        Assert.Equal("chrome /api", _vm.SearchText);
        Assert.True(_vm.OnlyErrors);
        Assert.Equal("api errors", Assert.Single(AppSettings.Load(_settingsPath).SavedFilters).Name);

        _vm.DeleteSavedFilter(_vm.SavedFilters[0]);
        Assert.Empty(_vm.SavedFilters);
        Assert.Empty(AppSettings.Load(_settingsPath).SavedFilters);
    }

    [Fact]
    public void Saving_a_filter_with_the_same_name_replaces_it()
    {
        _vm.NewFilterName = "mine";
        _vm.SaveFilterCommand.Execute(null);
        _vm.SearchText = "new";
        _vm.NewFilterName = "MINE";
        _vm.SaveFilterCommand.Execute(null);

        Assert.Equal("new", Assert.Single(_vm.SavedFilters).SearchText);
    }

    // ------------------------------------------------------------ WebSocket

    [Fact]
    public void WebSocket_tab_lists_messages_and_adds_new_ones()
    {
        var session = TestHost.NewSession("https://chat.example/ws", SessionKind.Upgrade);
        session.WebSocket = new WebSocketLog();
        session.WebSocket.Add(new WebSocketMessage(1, DateTimeOffset.Now, true, WebSocketOpcode.Text, 7, Encoding.UTF8.GetBytes("{\"a\":1}"), false, false, null));
        _test.Sink.OnSessionStarted(session);
        _dispatcher.Tick();
        _vm.Sessions.SelectedRow = _vm.Sessions.Rows[0];

        var row = Assert.Single(_vm.WebSocket.Messages);
        Assert.Equal("↑", row.Direction);
        Assert.Equal("{\"a\":1}", row.Preview);
        Assert.Contains("1 sent, 0 received", _vm.WebSocket.Summary, StringComparison.Ordinal);

        session.WebSocket.Add(new WebSocketMessage(2, DateTimeOffset.Now, false, WebSocketOpcode.Close, 4, [0x03, 0xE8, (byte)'o', (byte)'k'], false, false, null));
        _test.Sink.OnSessionUpdated(session);
        _dispatcher.Tick();

        Assert.Equal(2, _vm.WebSocket.Messages.Count);
        Assert.Equal("Close 1000: ok", _vm.WebSocket.Messages[1].Preview);

        _vm.WebSocket.SelectedIndex = 0;
        Assert.Contains("\"a\": 1", _vm.WebSocket.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public void WebSocket_tab_explains_ordinary_sessions()
    {
        Add("https://example.com/");
        _vm.Sessions.SelectedRow = _vm.Sessions.Rows[0];

        Assert.Empty(_vm.WebSocket.Messages);
        Assert.Contains("not a WebSocket", _vm.WebSocket.Summary, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ Timeline

    [Fact]
    public void Timeline_bars_add_up_and_leave_out_tunnels()
    {
        Add("https://example.com/a");
        Add("https://example.com/b");
        _test.AddSession("https://example.com:443/", status: null, kind: SessionKind.Connect);
        _dispatcher.Tick();

        _vm.SelectedTabIndex = MainViewModel.TimelineTab;

        Assert.Equal(2, _vm.Timeline.Rows.Count);
        Assert.All(_vm.Timeline.Rows, r => Assert.InRange(r.Offset + r.Send + r.Wait + r.Receive + r.After, 0.999, 1.001));
        Assert.Contains("2 request(s)", _vm.Timeline.RangeText, StringComparison.Ordinal);
        Assert.StartsWith("GET example.com/a", _vm.Timeline.Rows[0].Label, StringComparison.Ordinal);
    }

    [Fact]
    public void Timeline_row_click_selects_the_session()
    {
        var session = Add("https://example.com/pick");
        _vm.SelectedTabIndex = MainViewModel.TimelineTab;

        _vm.SelectSession(session.Id);

        Assert.Same(session, _vm.Sessions.SelectedRow!.Session);
        Assert.Equal(MainViewModel.InspectorsTab, _vm.SelectedTabIndex);
    }

    // ------------------------------------------------------------ Compare

    [Fact]
    public void Compare_shows_only_what_differs()
    {
        Add("https://example.com/api", body: "{\"user\":\"ada\",\"id\":1}");
        Add("https://example.com/api", body: "{\"user\":\"bob\",\"id\":1}");
        _vm.Sessions.SelectedRow = _vm.Sessions.Rows[0];
        _vm.MarkForCompareCommand.Execute(null);
        Assert.False(_vm.CompareWithMarkedCommand.CanExecute(null)); // same session
        _vm.Sessions.SelectedRow = _vm.Sessions.Rows[1];

        _vm.CompareWithMarkedCommand.Execute(null);

        Assert.Equal(MainViewModel.CompareTab, _vm.SelectedTabIndex);
        Assert.Contains("2 line(s) differ", _vm.Compare.Title, StringComparison.Ordinal);
        Assert.Contains(_vm.Compare.Lines, l => l.Kind == DiffKind.Removed && l.Text.Contains("ada", StringComparison.Ordinal));
        Assert.Contains(_vm.Compare.Lines, l => l.Kind == DiffKind.Added && l.Text.Contains("bob", StringComparison.Ordinal));

        _vm.Compare.OnlyDifferences = true;
        Assert.Equal(2, _vm.Compare.Lines.Count);
    }

    [Fact]
    public void Compare_with_a_removed_session_explains_why()
    {
        Add("https://example.com/one");
        Add("https://example.com/two");
        _vm.Sessions.SelectedRow = _vm.Sessions.Rows[0];
        _vm.MarkForCompareCommand.Execute(null);
        _vm.Sessions.Remove([_vm.Sessions.Rows[0]]);
        _vm.Sessions.SelectedRow = _vm.Sessions.Rows[0];

        _vm.CompareWithMarkedCommand.Execute(null);

        Assert.True(_vm.MessageIsError);
        Assert.Contains("was removed", _vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Line_diff_matches_common_lines()
    {
        var diff = LineDiff.Compare("a\nb\nc\nd", "a\nc\nX\nd");

        Assert.Equal(
            new[] { "  a", "- b", "  c", "+ X", "  d" },
            diff.Select(l => (l.Kind switch { DiffKind.Added => "+ ", DiffKind.Removed => "- ", _ => "  " }) + l.Text));
        Assert.Equal(2, LineDiff.CountChanges(diff));
        Assert.Empty(LineDiff.Compare(string.Empty, string.Empty));
    }

    // ------------------------------------------------------------ Settings

    [Fact]
    public void Settings_validate_and_apply()
    {
        _vm.OpenSettingsCommand.Execute(null);
        var page = _vm.Settings;
        Assert.Equal("0", page.PortText);

        page.PortText = "99999";
        Assert.True(page.IsDirty);
        Assert.Null(page.TryBuild());
        Assert.Contains("1 to 65535", page.ErrorText, StringComparison.Ordinal);

        page.PortText = "9123";
        page.SkipHostsText = "bad host!";
        Assert.Null(page.TryBuild());

        page.SkipHostsText = "*.bank.example\r\nintranet.local, other.example";
        page.AvoidZstd = false;
        Assert.Equal("*.bank.example\nintranet.local, other.example", page.SkipHostsText);
        var built = page.TryBuild()!;
        Assert.Equal(9123, built.Port);
        Assert.Equal(new[] { "*.bank.example", "intranet.local", "other.example" }, built.DoNotDecryptHosts);
        Assert.False(built.AvoidZstd);
        Assert.True(SettingsViewModel.NeedsRestart(new AppSettings(), built));
        Assert.False(SettingsViewModel.NeedsRestart(built, built with { CaptureOnStartup = !built.CaptureOnStartup }));
    }

    [Fact]
    public async Task Applying_settings_saves_them_and_restarts_capture()
    {
        await _vm.ToggleCaptureCommand.ExecuteAsync();
        var firstPort = _test.Host.Endpoint!.Port;
        _vm.OpenSettingsCommand.Execute(null);
        _vm.Settings.DecryptHttps = false;

        await _vm.ApplySettingsCommand.ExecuteAsync();

        Assert.False(_vm.DecryptHttps);
        Assert.False(_test.Host.IsDecrypting);
        Assert.True(_test.Host.IsRunning);
        Assert.False(_vm.Settings.IsDirty);
        Assert.False(AppSettings.Load(_settingsPath).DecryptHttps);
        Assert.Contains("restarted", _vm.Message, StringComparison.Ordinal);
        Assert.NotEqual(0, firstPort);
    }

    [Fact]
    public void Revert_discards_edits()
    {
        _vm.Settings.PortText = "1234";

        _vm.RevertSettingsCommand.Execute(null);

        Assert.Equal("0", _vm.Settings.PortText);
        Assert.False(_vm.Settings.IsDirty);
    }

    [Fact]
    public void Capture_settings_carry_the_new_options()
    {
        var capture = new AppSettings { AllowRemote = true, AvoidZstd = false }.ToCaptureSettings();

        Assert.True(capture.AllowRemote);
        Assert.False(capture.AvoidZstd);
    }

    [Fact]
    public void Settings_open_as_a_request_to_the_window_with_fresh_values()
    {
        var raised = 0;
        _vm.SettingsRequested += (_, _) => raised++;
        _vm.Settings.PortText = "1234";
        _vm.RevertSettingsCommand.Execute(null);

        _vm.OpenSettingsCommand.Execute(null);

        Assert.Equal(1, raised);
        Assert.Equal("0", _vm.Settings.PortText);
    }

    [Fact]
    public void Exactly_one_tab_shows_at_a_time()
    {
        for (var tab = MainViewModel.InspectorsTab; tab <= MainViewModel.CompareTab; tab++)
        {
            _vm.SelectedTabIndex = tab;
            var shown = new[] { _vm.ShowInspectTab, _vm.ShowOverviewTab, _vm.ShowWebSocketTab, _vm.ShowComposerTab, _vm.ShowRulesTab, _vm.ShowBreakpointsTab, _vm.ShowTimelineTab, _vm.ShowCompareTab };
            Assert.Equal(1, shown.Count(x => x));
            Assert.True(shown[tab]);
        }

        _vm.SelectedTabIndex = 99;
        Assert.True(_vm.ShowInspectTab);
    }

    [Fact]
    public void WebSocket_tab_is_left_when_another_kind_of_session_is_selected()
    {
        var socket = TestHost.NewSession("https://chat.example/ws", SessionKind.Upgrade);
        socket.WebSocket = new WebSocketLog();
        _test.Sink.OnSessionStarted(socket);
        Add("https://example.com/plain");
        var socketRow = _vm.Sessions.Rows.First(r => r.Session == socket);
        _vm.Sessions.SelectedRow = socketRow;
        Assert.True(_vm.IsWebSocketSelected);
        _vm.SelectedTabIndex = MainViewModel.WebSocketTab;

        _vm.Sessions.SelectedRow = _vm.Sessions.Rows.First(r => r.Session != socket);

        Assert.False(_vm.IsWebSocketSelected);
        Assert.Equal(MainViewModel.InspectorsTab, _vm.SelectedTabIndex);
    }

    [Fact]
    public async Task Empty_list_explains_what_to_do()
    {
        Assert.True(_vm.IsGridEmpty);
        Assert.Equal("Not capturing", _vm.EmptyGridTitle);

        await _vm.ToggleCaptureCommand.ExecuteAsync();
        Assert.Equal("Waiting for traffic", _vm.EmptyGridTitle);

        Add("https://example.com/");
        Assert.False(_vm.IsGridEmpty);

        _vm.SearchText = "nothing-matches-this";
        Assert.True(_vm.IsGridEmpty);
        Assert.Equal("No matching sessions", _vm.EmptyGridTitle);
    }

    [Fact]
    public void Tool_rail_and_inspect_views_map_onto_one_selection()
    {
        Assert.Equal(0, _vm.SelectedToolIndex);
        Assert.False(_vm.IsToolMode);

        _vm.InspectViewIndex = 1;
        Assert.Equal(MainViewModel.OverviewTab, _vm.SelectedTabIndex);

        _vm.SelectedToolIndex = 2;
        Assert.Equal(MainViewModel.RulesTab, _vm.SelectedTabIndex);
        Assert.True(_vm.IsToolMode);
        Assert.Equal("Rules", _vm.CardTitle);
        Assert.Equal(1, _vm.InspectViewIndex); // remembered while a tool is open

        _vm.SelectedToolIndex = 0;
        Assert.Equal(MainViewModel.OverviewTab, _vm.SelectedTabIndex); // back to the view you left

        _vm.SelectedTabIndex = MainViewModel.CompareTab;
        Assert.Equal(5, _vm.SelectedToolIndex);
        Assert.Equal("Compare", _vm.CardTitle);
    }

    [Fact]
    public void WebSocket_view_needs_a_websocket_session()
    {
        _vm.InspectViewIndex = 2;

        Assert.Equal(MainViewModel.InspectorsTab, _vm.SelectedTabIndex);
    }

    [Fact]
    public void Double_click_returns_to_inspect()
    {
        _vm.SelectedToolIndex = 4;

        _vm.ShowInspector();

        Assert.False(_vm.IsToolMode);
        Assert.Equal(0, _vm.SelectedToolIndex);
    }

    [Fact]
    public void Rail_badges_count_rules_on_and_paused_requests()
    {
        Assert.Equal(0, _vm.RulesBadge);

        _vm.Rules.Add(new Snoop.Proxy.Rules.TrafficRule { Pattern = "x", Actions = [new Snoop.Proxy.Rules.RuleAction(Snoop.Proxy.Rules.RuleActionKind.Block)] });

        Assert.Equal(1, _vm.RulesBadge);
        Assert.Equal(0, _vm.BreakpointsBadge);
    }
}
