using System.Text;
using Snoop.Core.Http;
using Snoop.Core.Sessions;
using Snoop.Presentation.Settings;
using Snoop.Presentation.Tests.TestSupport;
using Snoop.Presentation.ViewModels;
using Snoop.Proxy.Interception;
using Snoop.Proxy.Rules;

namespace Snoop.Presentation.Tests.ViewModels;

/// <summary>Snapshots, Composer, rules and breakpoints as seen from the main window.</summary>
public sealed class MainViewModelFeatureTests : IAsyncLifetime
{
    private readonly FakeDispatcher _dispatcher = new();
    private readonly FakeFiles _files = new();
    private readonly FakeOpenFiles _openFiles = new();
    private TestHost _test = null!;
    private MainViewModel _vm = null!;

    public Task InitializeAsync()
    {
        _test = new TestHost();
        var settings = new AppSettings { Port = 0, CaptureOnStartup = false, HideTunnels = false };
        _vm = new MainViewModel(_test.Host, _dispatcher, new FakeClipboard(), _files, settings, null, _openFiles, Path.Combine(_test.Directory, "rules.json"));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _vm.ShutdownAsync();
        _vm.Sessions.Dispose();
        await _test.DisposeAsync();
    }

    private SessionRowViewModel SelectFirst()
    {
        _dispatcher.Tick();
        _vm.Sessions.SelectedRow = _vm.Sessions.Rows[0];
        return _vm.Sessions.SelectedRow;
    }

    [Fact]
    public async Task Snapshot_save_then_load_adds_the_sessions_again()
    {
        _test.AddSession("https://example.com/saved", body: "{\"saved\":1}");
        _dispatcher.Tick();

        await _vm.SaveSnapshotCommand.ExecuteAsync();

        Assert.EndsWith(".snoop", _files.SuggestedName, StringComparison.Ordinal);
        Assert.Contains("Saved 1 session", _vm.Message, StringComparison.Ordinal);
        Directory.CreateDirectory(_test.Directory);
        var path = Path.Combine(_test.Directory, "capture.snoop");
        await File.WriteAllBytesAsync(path, _files.Saved!);
        _openFiles.Path = path;

        await _vm.LoadSnapshotCommand.ExecuteAsync();
        _dispatcher.Tick();

        Assert.Equal(".snoop", _openFiles.LastExtension);
        Assert.Contains("Added 1 session", _vm.Message, StringComparison.Ordinal);
        Assert.Equal(2, _vm.Sessions.TotalCount);
        var imported = Assert.Single(_vm.Sessions.Rows, r => r.Session.Flags.HasFlag(SessionFlags.Imported));
        Assert.Equal(Glyphs.Folder, imported.MarkGlyphs);
        Assert.Equal("from a file", imported.MarkText);
        Assert.Equal("{\"saved\":1}", Encoding.UTF8.GetString(imported.Session.Response!.Body!.ToArray()));
    }

    [Fact]
    public async Task Loading_a_bad_file_reports_it()
    {
        Directory.CreateDirectory(_test.Directory);
        var path = Path.Combine(_test.Directory, "bad.snoop");
        await File.WriteAllTextAsync(path, "not a zip");
        _openFiles.Path = path;

        await _vm.LoadSnapshotCommand.ExecuteAsync();

        Assert.True(_vm.MessageIsError);
        Assert.Contains("not a Snoop snapshot", _vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Edit_in_composer_loads_the_request_and_shows_the_tab()
    {
        _test.AddSession("https://example.com/edit-me");
        SelectFirst();

        _vm.EditInComposerCommand.Execute(null);

        Assert.Equal(MainViewModel.ComposerTab, _vm.SelectedTabIndex);
        Assert.Equal("https://example.com/edit-me", _vm.Composer.Url);
    }

    [Fact]
    public void Create_rule_from_a_session_adds_it_and_shows_the_rules_tab()
    {
        _test.AddSession("https://example.com/mock-me", body: "{\"m\":1}");
        SelectFirst();

        _vm.CreateRuleCommand.Execute(null);

        Assert.Equal(MainViewModel.RulesTab, _vm.SelectedTabIndex);
        Assert.Single(_vm.Rules.Items);
        Assert.Single(_test.Host.Rules.Match("GET", new Uri("https://example.com/mock-me")));
        Assert.Equal("Rules (1 on)", _vm.Rules.HeaderText);
    }

    [Fact]
    public void Tunnels_cannot_be_composed_or_turned_into_rules()
    {
        _test.AddSession("https://example.com:443/", kind: SessionKind.Connect, status: null);
        SelectFirst();

        Assert.False(_vm.EditInComposerCommand.CanExecute(null));
        Assert.False(_vm.CreateRuleCommand.CanExecute(null));
    }

    [Fact]
    public async Task Pause_switches_to_the_breakpoints_tab()
    {
        var exchange = new PausedExchange(TestHost.NewSession(), new EditableRequest("GET", new Uri("https://example.com/"), new HttpHeaderList(), null));

        var pausing = _test.Host.Breakpoints.PauseAsync(exchange, CancellationToken.None);

        Assert.Equal(MainViewModel.BreakpointsTab, _vm.SelectedTabIndex);
        Assert.Single(_vm.Breakpoints.Pending);
        exchange.Resume();
        await pausing;
    }

    [Fact]
    public async Task Status_mentions_rules_and_breakpoints_while_capturing()
    {
        await _vm.ToggleCaptureCommand.ExecuteAsync();
        _vm.Rules.Add(new TrafficRule { Pattern = "x", Actions = [new RuleAction(RuleActionKind.Block)] });
        _vm.Breakpoints.BreakOnRequests = true;

        Assert.Contains("1 rule on", _vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("breakpoints on", _vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialize_loads_saved_rules()
    {
        Snoop.Hosting.Rules.RulesFile.Save([new TrafficRule { Name = "kept", Pattern = "x", Actions = [new RuleAction(RuleActionKind.Block)] }], Path.Combine(_test.Directory, "rules.json"));

        await _vm.InitializeAsync();

        Assert.Equal("kept", Assert.Single(_vm.Rules.Items).Name);
        Assert.Equal(1, _test.Host.Rules.EnabledCount);
    }

    [Fact]
    public void Overview_explains_changes()
    {
        var session = _test.AddSession("https://example.com/changed");
        session.OriginalRequest = new RequestInfo("GET", new Uri("https://example.com/original"), "HTTP/1.1", new HttpHeaderList([new("X-Old", "1")]));
        session.ModifiedBy = "rule 'mock'";
        session.Flags |= SessionFlags.AutoResponded | SessionFlags.Composed;

        var overview = InspectorViewModel.BuildOverview(session);

        Assert.Contains(overview, r => r.Name == "Changed by" && r.Value == "rule 'mock'");
        Assert.Contains(overview, r => r.Name == "Original request" && r.Value == "GET https://example.com/original");
        Assert.Contains(overview, r => r.Name == "Origin" && r.Value.Contains("answered by a rule", StringComparison.Ordinal) && r.Value.Contains("Composer", StringComparison.Ordinal));
        var row = new SessionRowViewModel(session);
        Assert.Equal(session.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), row.IdText);
        Assert.Equal(Glyphs.Lightning + Glyphs.Send, row.MarkGlyphs);
        Assert.Equal("answered by a rule, sent from the Composer", row.MarkText);
    }

    [Fact]
    public void Dropped_request_opens_in_the_composer_but_tunnels_do_not()
    {
        _test.AddSession("https://example.com/replay-me");
        _test.AddSession("https://example.com:443/", kind: SessionKind.Connect, status: null);
        _dispatcher.Tick();
        var request = _vm.Sessions.Rows.First(r => r.Session.Kind == SessionKind.Http);
        var tunnel = _vm.Sessions.Rows.First(r => r.Session.Kind == SessionKind.Connect);

        Assert.False(MainViewModel.CanOpenInComposer(tunnel));
        Assert.False(_vm.OpenInComposer(tunnel));
        Assert.False(_vm.OpenInComposer(null));
        Assert.True(_vm.OpenInComposer(request));

        Assert.Equal(MainViewModel.ComposerTab, _vm.SelectedTabIndex);
        Assert.Equal("https://example.com/replay-me", _vm.Composer.Url);
    }
}
