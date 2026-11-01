using System.Net.Sockets;
using Snoop.Cli.CommandLine;
using Snoop.Cli.Hosting;
using Snoop.Cli.Output;
using Snoop.Core.Diagnostics;
using Snoop.Hosting;
using Snoop.Hosting.Rules;
using Snoop.Proxy.Rules;

namespace Snoop.Cli.Commands;

/// <summary>Runs the proxy until Ctrl+C, printing each session; optionally acts as the system proxy.</summary>
internal sealed class RunCommand(CliOptions options, ISnoopLogger logger, ConsoleWriter writer)
{
    public async Task<int> ExecuteAsync()
    {
        await using var host = new CaptureHost(logger);
        host.RecoverSystemProxy();

        var printer = new SessionPrinter(writer, options.ShowTunnels);
        printer.Attach(host.Sessions);
        LoadRules(host);

        var settings = new CaptureSettings
        {
            Port = options.Port,
            DecryptHttps = options.Decrypt,
            CaptureLoopback = options.CaptureLoopback,
            AllowRemote = options.AllowRemote,
            DoNotDecryptHosts = options.DoNotDecrypt,
            UseBuiltInSkips = options.UseDefaultSkips,
            AutoSkip = options.AutoSkip,
            AvoidZstd = options.AvoidZstd,
        };

        try
        {
            await host.StartAsync(settings).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            logger.Error($"Could not listen on port {options.Port} or the ports after it.", ex);
            return ExitCodes.PortUnavailable;
        }

        // Closing the console window may end the process as soon as the handler returns,
        // so the system proxy is restored inside the handler itself.
        using var shutdown = new ShutdownSignal(onTerminating: host.RestoreSystemProxyNow);
        void OnCrash(object sender, UnhandledExceptionEventArgs e) => host.RestoreSystemProxyNow();
        AppDomain.CurrentDomain.UnhandledException += OnCrash;
        try
        {
            if (options.Decrypt && host.IsRootTrustSupported && !host.IsRootTrusted)
            {
                logger.Warn("The Snoop root certificate is not trusted yet, so HTTPS sites will show certificate errors. Run: snoop trust-root");
            }

            PrintBanner(host);
            if (options.SetSystemProxy)
            {
                EnableSystemProxy(host);
            }

            await shutdown.Stopped.ConfigureAwait(false);
            writer.WriteLine("Stopping...");
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= OnCrash;
            host.RestoreSystemProxyNow();
            printer.Detach(host.Sessions);
            await host.StopAsync().ConfigureAwait(false);
        }

        writer.WriteLine($"Captured {host.Sessions.Count} session(s).");
        return ExitCodes.Success;
    }

    /// <summary>
    /// Applies the rules saved by the desktop app. Breakpoint actions are left out: nothing here could resume
    /// a paused request.
    /// </summary>
    private void LoadRules(CaptureHost host)
    {
        try
        {
            var rules = RulesFile.Load()
                .Select(r => r with { Actions = [.. r.Actions.Where(a => a.Kind is not (RuleActionKind.BreakOnRequest or RuleActionKind.BreakOnResponse))] })
                .ToList();
            host.Rules.Replace(rules);
            if (host.Rules.EnabledCount > 0)
            {
                writer.WriteLine($"{host.Rules.EnabledCount} rule(s) from {RulesFile.DefaultPath} are on.", ConsoleColor.Yellow);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            logger.Warn($"Rules were not loaded: {ex.Message}");
        }
    }

    private void EnableSystemProxy(CaptureHost host)
    {
        if (!host.IsSystemProxySupported)
        {
            logger.Warn("Setting the system proxy is only supported on Windows.");
            return;
        }

        host.SetSystemProxy(true);
        writer.WriteLine($"System proxy is now {host.Endpoint}. It is restored when Snoop stops.", ConsoleColor.Green);
    }

    private void PrintBanner(CaptureHost host)
    {
        writer.WriteLine($"Snoop is listening on {host.Endpoint}.", ConsoleColor.Green);
        writer.WriteLine(host.IsDecrypting ? "HTTPS decryption is on." : "HTTPS decryption is off.");
        if (!options.SetSystemProxy)
        {
            writer.WriteLine($"Point an app at http://{host.Endpoint} as its proxy, or restart with --system-proxy.");
        }

        writer.WriteLine("Press Ctrl+C to stop.");
        writer.WriteLine(string.Empty);
    }
}
