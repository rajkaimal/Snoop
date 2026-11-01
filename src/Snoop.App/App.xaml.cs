using Microsoft.UI.Xaml;
using Snoop.App.Services;
using Snoop.Hosting;
using Snoop.Presentation.Settings;

namespace Snoop.App;

/// <summary>
/// Application entry. Owns the capture host and makes sure the system proxy is restored
/// however the app ends: normal close, unhandled exception, or process exit.
/// </summary>
public partial class App : Application
{
    private readonly CaptureHost _host;
    private readonly LaunchOptions _options;
    private MainWindow? _window;

    public App()
    {
        _options = LaunchOptions.Parse(Environment.GetCommandLineArgs().Skip(1));

        // UI tests: keep every file Snoop writes in a throwaway folder, never the user's own. This must
        // happen before anything reads SnoopPaths (the capture host below does).
        if (_options.TestMode && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Snoop.Core.SnoopPaths.DataDirectoryVariable)))
        {
            Environment.SetEnvironmentVariable(
                Snoop.Core.SnoopPaths.DataDirectoryVariable,
                Path.Combine(Path.GetTempPath(), "Snoop-test-" + Guid.NewGuid().ToString("N")));
        }

        InitializeComponent();
        _host = new CaptureHost(new DebugLogger());

        UnhandledException += (_, _) => _host.RestoreSystemProxyNow();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _host.RestoreSystemProxyNow();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _host.RestoreSystemProxyNow();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Started by Windows at logon after Snoop was killed: put the proxy setting back and quit,
        // without opening a window or starting a new capture.
        if (_options.RestoreProxy)
        {
            _host.RecoverSystemProxy();
            Exit();
            return;
        }

        _window = new MainWindow(_host, _options);
        _window.Activate();
    }
}
