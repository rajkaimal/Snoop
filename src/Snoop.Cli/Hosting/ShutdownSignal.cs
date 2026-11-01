using System.Runtime.InteropServices;

namespace Snoop.Cli.Hosting;

/// <summary>
/// Completes when the process is asked to stop: Ctrl+C, Ctrl+Break, closing the console
/// window, log off or shutdown (all mapped by .NET to POSIX-style signals on Windows).
/// </summary>
internal sealed class ShutdownSignal : IDisposable
{
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<PosixSignalRegistration> _registrations = [];
    private readonly Action? _onTerminating;

    /// <param name="onTerminating">
    /// Runs synchronously, inside the signal handler, when the console window is closed or the
    /// user logs off or shuts down. Windows may end the process as soon as the handler returns,
    /// so anything that must happen (restoring the system proxy) goes here and must be quick.
    /// </param>
    public ShutdownSignal(Action? onTerminating = null)
    {
        _onTerminating = onTerminating;
        foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGTERM, PosixSignal.SIGHUP })
        {
            _registrations.Add(PosixSignalRegistration.Create(signal, OnSignal));
        }
    }

    public Task Stopped => _stopped.Task;

    public void Dispose()
    {
        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }
    }

    private void OnSignal(PosixSignalContext context)
    {
        if (context.Signal is PosixSignal.SIGTERM or PosixSignal.SIGHUP)
        {
            _onTerminating?.Invoke();
        }

        // Ctrl+C / Ctrl+Break: keep the process alive so the normal, orderly shutdown runs.
        context.Cancel = true;
        _stopped.TrySetResult();
    }
}
