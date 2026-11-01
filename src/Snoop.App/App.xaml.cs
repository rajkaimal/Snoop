using Microsoft.UI.Xaml;
using Snoop.App.Services;
using Snoop.Hosting;

namespace Snoop.App;

/// <summary>
/// Application entry. Owns the capture host and makes sure the system proxy is restored
/// however the app ends: normal close, unhandled exception, or process exit.
/// </summary>
public partial class App : Application
{
    /// <summary>Argument used by the logon safety net (HKCU RunOnce) after a hard kill.</summary>
    private const string RestoreProxyArgument = "restore-proxy";

    private readonly CaptureHost _host;
    private MainWindow? _window;

    public App()
    {
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
        if (Environment.GetCommandLineArgs().Skip(1).Any(a => string.Equals(a, RestoreProxyArgument, StringComparison.OrdinalIgnoreCase)))
        {
            _host.RecoverSystemProxy();
            Exit();
            return;
        }

        _window = new MainWindow(_host);
        _window.Activate();
    }
}
