using System.Net;
using Snoop.Core;
using Snoop.Core.Diagnostics;
using Snoop.Core.Sessions;
using Snoop.Core.Snapshots;
using Snoop.Hosting.Composer;
using Snoop.Proxy;
using Snoop.Proxy.Interception;
using Snoop.Proxy.Rules;
using Snoop.Proxy.Tls;

namespace Snoop.Hosting;

/// <summary>
/// Runs a capture: loads the root certificate authority, starts and stops the proxy,
/// switches the system proxy on and off (always restoring it), and keeps every session in
/// one <see cref="SessionStore"/> that survives restarts. Used by both the CLI and the app.
/// Thread-safe.
/// </summary>
public sealed class CaptureHost : IAsyncDisposable
{
    private readonly CaptureHostServices _services;
    private readonly ISnoopLogger _logger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Lock _gate = new();
    private CertificateAuthority? _authority;
    private ProxyServer? _proxy;
    private CaptureSettings _settings = new();

    public CaptureHost(ISnoopLogger? logger = null, CaptureHostServices? services = null, int sessionCapacity = SessionStore.DefaultCapacity)
    {
        _logger = logger ?? NullLogger.Instance;
        _services = services ?? CaptureHostServices.ForCurrentPlatform(_logger);
        Sessions = new SessionStore(sessionCapacity, _logger);
    }

    /// <summary>Raised whenever running state, system proxy or certificate trust changes. Any thread.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Every session captured, across restarts, until cleared.</summary>
    public SessionStore Sessions { get; }

    /// <summary>Numbers sessions across restarts and snapshot imports, so they never repeat.</summary>
    public SessionIdSource SessionIds { get; } = new();

    /// <summary>Rules and breakpoints. They apply to every capture run and can be changed at any time.</summary>
    public TrafficInterceptor Interceptor { get; } = new();

    public RuleSet Rules => Interceptor.Rules;

    public BreakpointController Breakpoints => Interceptor.Breakpoints;

    public bool IsRunning
    {
        get { lock (_gate) { return _proxy is not null; } }
    }

    public IPEndPoint? Endpoint
    {
        get { lock (_gate) { return _proxy?.Endpoint; } }
    }

    /// <summary>The settings of the current (or last) capture.</summary>
    public CaptureSettings Settings
    {
        get { lock (_gate) { return _settings; } }
    }

    public bool IsDecrypting
    {
        get { lock (_gate) { return _proxy is not null && _settings.DecryptHttps && _authority is not null; } }
    }

    public bool IsSystemProxySupported => _services.SystemProxy is not null;

    public bool IsSystemProxyEnabled => _services.SystemProxy?.IsEnabled ?? false;

    public bool IsRootTrustSupported => _services.RootTrust.IsSupported;

    /// <summary>Whether the Snoop root certificate is in the trusted roots. Loads (or creates) the root if needed.</summary>
    public bool IsRootTrusted => _services.RootTrust.IsSupported && _services.RootTrust.IsTrusted(GetCertificateAuthority().RootCertificate);

    /// <summary>Hosts not decrypted this run because their apps rejected the Snoop certificate (or the user said so).</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> SkippedHosts
    {
        get { lock (_gate) { return _proxy?.SkippedHosts ?? new Dictionary<string, DateTimeOffset>(); } }
    }

    /// <summary>The root certificate authority, loaded from disk or created on first use.</summary>
    public CertificateAuthority GetCertificateAuthority()
    {
        lock (_gate)
        {
            if (_authority is null)
            {
                var store = _services.CertificateStore();
                var isNew = !store.Exists;
                _authority = new CertificateAuthority(store.LoadOrCreate());
                if (isNew)
                {
                    _logger.Info($"Created a new root certificate in {store.Directory}.");
                }
            }

            return _authority;
        }
    }

    /// <summary>
    /// Call once at startup: if an earlier run did not shut down cleanly, puts the user's
    /// system proxy setting back. Returns true when something was repaired.
    /// </summary>
    public bool RecoverSystemProxy()
    {
        SnoopPaths.SweepTempDirectory();
        try
        {
            return _services.SystemProxy?.RecoverIfNeeded() ?? false;
        }
#pragma warning disable CA1031 // Report and carry on; the user can still restore manually.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.Error("Could not restore the system proxy setting from the last run.", ex);
            return false;
        }
    }

    /// <summary>Starts capturing. If a capture is already running it is restarted with the new settings.</summary>
    /// <exception cref="System.Net.Sockets.SocketException">No port could be bound.</exception>
    public async Task StartAsync(CaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(restoreSystemProxy: false).ConfigureAwait(false);

            var authority = settings.DecryptHttps ? GetCertificateAuthority() : null;
            var options = new ProxyOptions
            {
                Port = settings.Port,
                ListenAddress = settings.AllowRemote ? IPAddress.Any : IPAddress.Loopback,
                DecryptHttps = settings.DecryptHttps,
                DoNotDecryptHosts = settings.DoNotDecryptHosts,
                UseBuiltInDoNotDecryptList = settings.UseBuiltInSkips,
                AutoSkipAfterRejections = settings.AutoSkip ? 2 : 0,
                RemoveZstdFromAcceptEncoding = settings.AvoidZstd,
            };

            var proxy = new ProxyServer(options, Sessions, authority, _services.ProcessResolver, _logger, SessionIds, Interceptor);
            try
            {
                proxy.Start();
            }
            catch
            {
                await proxy.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            lock (_gate)
            {
                _proxy = proxy;
                _settings = settings;
            }

            if (settings.SetSystemProxy)
            {
                EnableSystemProxyCore(proxy.Endpoint!, settings.CaptureLoopback);
            }
            else if (IsSystemProxyEnabled)
            {
                // Restarted on a different port, or the user turned it off: never leave a stale setting.
                RestoreSystemProxyNow();
            }
        }
        finally
        {
            _lifecycle.Release();
        }

        RaiseStateChanged();
    }

    /// <summary>Stops capturing and restores the system proxy. Sessions are kept.</summary>
    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(restoreSystemProxy: true).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }

        RaiseStateChanged();
    }

    /// <summary>Turns the system proxy on (pointing at the running capture) or off.</summary>
    /// <exception cref="InvalidOperationException">Turning it on while not capturing, or on an unsupported platform.</exception>
    public void SetSystemProxy(bool enabled)
    {
        if (enabled)
        {
            ProxyServer proxy;
            lock (_gate)
            {
                proxy = _proxy ?? throw new InvalidOperationException("Start capturing before making Snoop the system proxy.");
                _settings = _settings with { SetSystemProxy = true };
            }

            EnableSystemProxyCore(proxy.Endpoint!, Settings.CaptureLoopback);
        }
        else
        {
            lock (_gate)
            {
                _settings = _settings with { SetSystemProxy = false };
            }

            RestoreSystemProxyNow();
        }

        RaiseStateChanged();
    }

    /// <summary>
    /// Puts the user's system proxy setting back right now. Synchronous and never throws, so it is
    /// safe in crash and shutdown handlers.
    /// </summary>
    public void RestoreSystemProxyNow()
    {
        try
        {
            _services.SystemProxy?.Disable();
        }
#pragma warning disable CA1031 // Must never throw from shutdown paths; the RunOnce entry retries at next logon.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.Error("Could not restore the system proxy. Run 'snoop restore-proxy' to fix it.", ex);
        }
    }

    /// <summary>Adds the Snoop root certificate to the trusted roots (Windows asks the user to confirm).</summary>
    public void TrustRootCertificate()
    {
        _services.RootTrust.Trust(GetCertificateAuthority().RootCertificate);
        RaiseStateChanged();
    }

    /// <summary>Removes every Snoop root certificate from the trusted roots.</summary>
    public int UntrustRootCertificates()
    {
        var removed = _services.RootTrust.RemoveAll();
        RaiseStateChanged();
        return removed;
    }

    /// <summary>Stops decrypting a host for the rest of this capture.</summary>
    public bool SkipDecryption(string host)
    {
        ProxyServer? proxy;
        lock (_gate)
        {
            proxy = _proxy;
        }

        var added = proxy?.SkipDecryption(host) ?? false;
        if (added)
        {
            RaiseStateChanged();
        }

        return added;
    }

    /// <summary>
    /// Sends a request from the Composer through the running capture, so it is recorded and rules and
    /// breakpoints apply to it like any other request.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not capturing.</exception>
    public Task<ComposerResult> SendComposedAsync(ComposedRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var endpoint = Endpoint ?? throw new InvalidOperationException("Start capturing before sending a request from the Composer.");
        var root = IsDecrypting ? GetCertificateAuthority().RootCertificate : null;
        return ComposerClient.SendAsync(endpoint, root, Sessions, request, cancellationToken);
    }

    /// <summary>Saves sessions to a snapshot file. Returns how many were saved.</summary>
    public static int SaveSnapshot(Stream destination, IEnumerable<Session> sessions) => SnapshotFile.Save(destination, sessions);

    /// <summary>Adds the sessions in a snapshot file to <see cref="Sessions"/>. Returns how many were added.</summary>
    /// <exception cref="InvalidDataException">The file is not a Snoop snapshot or is damaged.</exception>
    public int LoadSnapshot(Stream source)
    {
        var sessions = SnapshotFile.Load(source, SessionIds);
        return Sessions.Import(sessions);
    }

    /// <summary>Adds the sessions in a HAR file to <see cref="Sessions"/>.</summary>
    /// <returns>How many were added, and how many entries could not be read.</returns>
    /// <exception cref="InvalidDataException">The file is not a HAR file.</exception>
    public (int Added, int Skipped) ImportHar(Stream source)
    {
        var result = HarFile.Load(source, SessionIds);
        return (Sessions.Import(result.Sessions), result.Skipped);
    }

    /// <summary>
    /// Opens a Snoop snapshot or a HAR file, telling them apart by their content (a snapshot is a
    /// zip archive, a HAR file is JSON), and adds its sessions.
    /// </summary>
    /// <param name="source">A seekable stream positioned at the start of the file.</param>
    /// <exception cref="InvalidDataException">The file is neither.</exception>
    public (int Added, int Skipped) OpenCaptureFile(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanSeek)
        {
            throw new ArgumentException("The stream must be seekable.", nameof(source));
        }

        var start = source.Position;
        int first;
        do
        {
            first = source.ReadByte();
        }
        while (first is ' ' or '\t' or '\r' or '\n' or 0xEF or 0xBB or 0xBF);

        source.Position = start;
        return first switch
        {
            'P' => (LoadSnapshot(source), 0),
            '{' => ImportHar(source),
            _ => throw new InvalidDataException("This is not a Snoop snapshot or a HAR file."),
        };
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Sessions.Dispose();
        lock (_gate)
        {
            _authority?.Dispose();
            _authority = null;
        }

        _lifecycle.Dispose();
    }

    private void EnableSystemProxyCore(IPEndPoint endpoint, bool captureLoopback)
    {
        var controller = _services.SystemProxy ?? throw new InvalidOperationException("Setting the system proxy is only supported on Windows.");
        var host = endpoint.Address.Equals(IPAddress.Any) ? "127.0.0.1" : endpoint.Address.ToString();
        controller.Enable(host, endpoint.Port, captureLoopback);
    }

    private async Task StopCoreAsync(bool restoreSystemProxy)
    {
        ProxyServer? proxy;
        lock (_gate)
        {
            proxy = _proxy;
            _proxy = null;
        }

        // Restore first: if stopping hangs or throws, the user still has working internet.
        if (restoreSystemProxy)
        {
            RestoreSystemProxyNow();
        }

        if (proxy is not null)
        {
            await proxy.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
#pragma warning disable CA1031 // A faulty listener must not break capture control.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.Error("A state listener failed.", ex);
        }
    }
}
