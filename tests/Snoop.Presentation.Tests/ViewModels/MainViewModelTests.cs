using System.Text;
using System.Text.Json;
using Snoop.Inspection.Bodies;
using Snoop.Presentation.Settings;
using Snoop.Presentation.Tests.TestSupport;
using Snoop.Presentation.ViewModels;

namespace Snoop.Presentation.Tests.ViewModels;

public sealed class MainViewModelTests : IAsyncLifetime
{
    private readonly FakeDispatcher _dispatcher = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly FakeFiles _files = new();
    private TestHost _test = null!;
    private MainViewModel _vm = null!;
    private string _settingsPath = null!;

    public Task InitializeAsync()
    {
        _test = new TestHost();
        _settingsPath = Path.Combine(_test.Directory, "settings.json");
        var settings = new AppSettings { Port = 0, CaptureOnStartup = false, HideTunnels = false };
        _vm = new MainViewModel(_test.Host, _dispatcher, _clipboard, _files, settings, _settingsPath);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _vm.ShutdownAsync();
        _vm.Sessions.Dispose();
        await _test.DisposeAsync();
    }

    [Fact]
    public async Task Toggle_capture_starts_and_stops_the_proxy_and_system_proxy()
    {
        await _vm.ToggleCaptureCommand.ExecuteAsync();

        Assert.True(_vm.IsCapturing);
        Assert.Equal("Stop capture", _vm.CaptureButtonText);
        Assert.True(_vm.IsSystemProxyOn);
        Assert.StartsWith("Listening on 127.0.0.1:", _vm.StatusText, StringComparison.Ordinal);

        await _vm.ToggleCaptureCommand.ExecuteAsync();

        Assert.False(_vm.IsCapturing);
        Assert.False(_vm.IsSystemProxyOn);
        Assert.Equal("Not capturing", _vm.StatusText);
    }

    [Fact]
    public async Task Initialize_starts_capture_when_configured()
    {
        var settings = new AppSettings { Port = 0, CaptureOnStartup = true, UseSystemProxy = false };
        var vm = new MainViewModel(_test.Host, _dispatcher, _clipboard, _files, settings);

        await vm.InitializeAsync();

        Assert.True(vm.IsCapturing);
        Assert.False(vm.IsSystemProxyOn);
        vm.Sessions.Dispose();
    }

    [Fact]
    public async Task System_proxy_toggle_needs_a_running_capture()
    {
        _vm.IsSystemProxyOn = true;

        Assert.False(_vm.IsSystemProxyOn);
        Assert.True(_vm.MessageIsError);

        await _vm.ToggleCaptureCommand.ExecuteAsync();
        _vm.IsSystemProxyOn = false;
        Assert.False(_test.Host.IsSystemProxyEnabled);
        _vm.IsSystemProxyOn = true;
        Assert.True(_test.Host.IsSystemProxyEnabled);
    }

    [Fact]
    public void Selecting_a_row_fills_the_inspector()
    {
        _test.AddSession("https://example.com/data?id=7", body: "{\"id\":7}");
        _dispatcher.Tick();

        _vm.Sessions.SelectedRow = _vm.Sessions.Rows[0];

        Assert.True(_vm.Inspector.HasSession);
        Assert.Equal("GET https://example.com/data?id=7", _vm.Inspector.Title);
        Assert.Equal("Params (1)", _vm.Inspector.Request.ParametersTitle);
        Assert.Equal("7", _vm.Inspector.Request.Parameters[0].Value);
        Assert.Equal(BodyViewMode.Json, _vm.Inspector.Response.RenderedBody.Mode);
        Assert.Contains("\"id\": 7", _vm.Inspector.Response.BodyText, StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 200 OK", _vm.Inspector.Response.RawText, StringComparison.Ordinal);
        Assert.Contains(_vm.Inspector.Overview, r => r.Name == "Process" && r.Value == "chrome:42");
    }

    [Fact]
    public void Body_mode_can_be_switched_to_hex()
    {
        _test.AddSession(body: "hi");
        _dispatcher.Tick();
        _vm.Sessions.SelectedRow = _vm.Sessions.Rows[0];

        _vm.Inspector.Response.BodyModeIndex = (int)BodyViewMode.Hex;

        Assert.Equal(BodyViewMode.Hex, _vm.Inspector.Response.BodyMode);
        Assert.StartsWith("00000000  68 69", _vm.Inspector.Response.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public void Copy_commands_put_text_on_the_clipboard()
    {
        _test.AddSession("https://example.com/x");
        _dispatcher.Tick();
        Assert.False(_vm.CopyUrlCommand.CanExecute(null));

        _vm.Sessions.SelectedRow = _vm.Sessions.Rows[0];

        Assert.True(_vm.CopyUrlCommand.CanExecute(null));
        _vm.CopyUrlCommand.Execute(null);
        Assert.Equal("https://example.com/x", _clipboard.Text);
        _vm.CopyCurlCommand.Execute(null);
        Assert.StartsWith("curl 'https://example.com/x'", _clipboard.Text, StringComparison.Ordinal);
        _vm.CopyPowerShellCommand.Execute(null);
        Assert.Contains("Invoke-WebRequest", _clipboard.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Filters_combine()
    {
        _test.AddSession("https://a.example/ok");
        _test.AddSession("https://a.example/broken", status: 500);
        _test.AddSession("https://b.example/broken", status: 500);
        _dispatcher.Tick();

        _vm.OnlyErrors = true;
        Assert.Equal("2 of 3 sessions", _vm.CountText);

        _vm.SearchText = "a.example";
        Assert.Equal("1 of 3 sessions", _vm.CountText);

        _vm.OnlyErrors = false;
        _vm.SearchText = string.Empty;
        Assert.Equal("3 sessions", _vm.CountText);
    }

    [Fact]
    public async Task Export_har_writes_the_visible_sessions()
    {
        _test.AddSession("https://example.com/1");
        _test.AddSession("https://example.com/2");
        _dispatcher.Tick();

        await _vm.ExportHarCommand.ExecuteAsync();

        Assert.NotNull(_files.Saved);
        Assert.EndsWith(".har", _files.SuggestedName, StringComparison.Ordinal);
        using var har = JsonDocument.Parse(Encoding.UTF8.GetString(_files.Saved!));
        Assert.Equal(2, har.RootElement.GetProperty("log").GetProperty("entries").GetArrayLength());
        Assert.False(_vm.MessageIsError);
    }

    [Fact]
    public async Task Cancelled_export_writes_nothing()
    {
        _test.AddSession();
        _dispatcher.Tick();
        _files.Cancel = true;

        await _vm.ExportHarCommand.ExecuteAsync();

        Assert.Null(_files.Saved);
        Assert.False(_vm.HasMessage);
    }

    [Fact]
    public void Trust_root_updates_the_prompt()
    {
        Assert.True(_vm.ShowTrustPrompt);

        _vm.TrustRootCommand.Execute(null);

        Assert.True(_vm.IsRootTrusted);
        Assert.False(_vm.ShowTrustPrompt);
        Assert.True(_test.Trust.Trusted);
    }

    [Fact]
    public async Task Skip_host_stops_decrypting_the_selected_host()
    {
        await _vm.ToggleCaptureCommand.ExecuteAsync();
        _test.AddSession("https://pinned.example/api");
        _dispatcher.Tick();
        _vm.Sessions.SelectedRow = _vm.Sessions.Rows[0];

        _vm.SkipHostCommand.Execute(null);

        Assert.True(_test.Host.SkippedHosts.ContainsKey("pinned.example"));
        Assert.Contains("pinned.example", _vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_changes_are_saved()
    {
        _vm.HideTunnels = true;
        _vm.AutoScroll = false;

        var saved = AppSettings.Load(_settingsPath);
        Assert.True(saved.HideTunnels);
        Assert.False(saved.AutoScroll);
    }

    [Fact]
    public async Task Turning_decryption_off_while_capturing_restarts_without_it()
    {
        await _vm.ToggleCaptureCommand.ExecuteAsync();
        Assert.True(_test.Host.IsDecrypting);

        _vm.DecryptHttps = false;
        for (var i = 0; i < 100 && !(_test.Host.IsRunning && !_test.Host.IsDecrypting); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(_test.Host.IsRunning);
        Assert.False(_test.Host.IsDecrypting);
    }

    [Fact]
    public async Task Shutdown_restores_the_system_proxy()
    {
        await _vm.ToggleCaptureCommand.ExecuteAsync();
        Assert.True(_test.Host.IsSystemProxyEnabled);

        await _vm.ShutdownAsync();

        Assert.False(_test.Host.IsSystemProxyEnabled);
        Assert.False(_test.Host.IsRunning);
    }
}
