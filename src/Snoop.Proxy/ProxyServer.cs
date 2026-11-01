using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Snoop.Core.Diagnostics;
using Snoop.Core.Sessions;
using Snoop.Proxy.Engine;
using Snoop.Proxy.Interception;
using Snoop.Proxy.Processes;
using Snoop.Proxy.Tls;

namespace Snoop.Proxy;

/// <summary>
/// The intercepting HTTP/HTTPS proxy. Listens for client connections, forwards their
/// traffic and reports every exchange to an <see cref="ISessionSink"/>.
/// </summary>
/// <example>
/// <code>
/// var store = new SessionStore();
/// await using var proxy = new ProxyServer(new ProxyOptions(), store, certificateAuthority);
/// proxy.Start();
/// </code>
/// </example>
public sealed class ProxyServer : IAsyncDisposable
{
    private readonly ProxyContext _context;
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private readonly Lock _gate = new();
    private Socket? _listener;
    private CancellationTokenSource? _stopping;
    private Task? _acceptLoop;
    private long _connectionCounter;

    public ProxyServer(
        ProxyOptions options,
        ISessionSink sink,
        CertificateAuthority? certificateAuthority = null,
        IClientProcessResolver? processResolver = null,
        ISnoopLogger? logger = null,
        SessionIdSource? sessionIds = null,
        ITrafficInterceptor? interceptor = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sink);
        options.Validate();
        _context = new ProxyContext(
            options,
            sink,
            certificateAuthority,
            processResolver ?? NullClientProcessResolver.Instance,
            logger ?? NullLogger.Instance,
            sessionIds ?? new SessionIdSource(),
            interceptor);

        if (options.DecryptHttps && certificateAuthority is null)
        {
            _context.Logger.Warn("HTTPS decryption is on but no certificate authority was given; tunnels will be relayed without decryption.");
        }
    }

    public ProxyOptions Options => _context.Options;

    public CertificateAuthority? CertificateAuthority => _context.CertificateAuthority;

    /// <summary>The endpoint actually listened on. Null until <see cref="Start"/> succeeds.</summary>
    public IPEndPoint? Endpoint { get; private set; }

    public bool IsRunning => _acceptLoop is { IsCompleted: false };

    /// <summary>Hosts no longer decrypted because their apps kept rejecting Snoop's certificate, with when.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> SkippedHosts => _context.Rejections.SkippedHosts;

    /// <summary>
    /// Stops decrypting <paramref name="host"/> for the rest of this run. New tunnels to it are relayed
    /// without decryption; tunnels already open are not affected.
    /// </summary>
    public bool SkipDecryption(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return _context.Rejections.Skip(host);
    }

    /// <summary>Rules and breakpoints applied to traffic, or null.</summary>
    public ITrafficInterceptor? Interceptor => _context.Interceptor;

    /// <summary>Number of client connections currently open.</summary>
    public int OpenConnectionCount => _connections.Count;

    /// <summary>
    /// Binds the listening socket and starts accepting connections. If the configured port
    /// is busy, the next <see cref="ProxyOptions.PortFallbackAttempts"/> ports are tried.
    /// </summary>
    /// <exception cref="SocketException">No port could be bound.</exception>
    public void Start()
    {
        lock (_gate)
        {
            if (_listener is not null)
            {
                throw new InvalidOperationException("The proxy is already started.");
            }

            // Locals, not fields: StopAsync may clear the fields before the loop task starts running.
            var listener = Bind(Options);
            var stopping = new CancellationTokenSource();
            var token = stopping.Token;
            _listener = listener;
            _stopping = stopping;
            Endpoint = (IPEndPoint)listener.LocalEndPoint!;
            _context.ListenEndpoint = Endpoint;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, token));
            _context.Logger.Info($"Proxy listening on {Endpoint}.");
        }
    }

    /// <summary>Stops accepting, closes open connections and waits for them to finish.</summary>
    public async Task StopAsync()
    {
        Socket? listener;
        CancellationTokenSource? stopping;
        Task? acceptLoop;
        lock (_gate)
        {
            listener = _listener;
            stopping = _stopping;
            acceptLoop = _acceptLoop;
            _listener = null;
            _stopping = null;
            _acceptLoop = null;
        }

        if (listener is null || stopping is null || acceptLoop is null)
        {
            return;
        }

        await stopping.CancelAsync().ConfigureAwait(false);
        listener.Dispose();
        await acceptLoop.ConfigureAwait(false);

        var open = _connections.Values.ToArray();
        var allClosed = Task.WhenAll(open);
        if (await Task.WhenAny(allClosed, Task.Delay(Options.ShutdownTimeout)).ConfigureAwait(false) != allClosed)
        {
            _context.Logger.Warn($"{_connections.Count} connection(s) did not close within {Options.ShutdownTimeout.TotalSeconds:0}s.");
        }

        stopping.Dispose();
        _context.Logger.Info("Proxy stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _context.Upstream.Dispose();
    }

    private static Socket Bind(ProxyOptions options)
    {
        SocketException? last = null;
        var attempts = options.Port == 0 ? 1 : options.PortFallbackAttempts + 1;
        for (var i = 0; i < attempts; i++)
        {
            var port = options.Port == 0 ? 0 : options.Port + i;
            if (port > IPEndPoint.MaxPort)
            {
                break;
            }

            var socket = new Socket(options.ListenAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    socket.ExclusiveAddressUse = true;
                }

                socket.Bind(new IPEndPoint(options.ListenAddress, port));
                socket.Listen(512);
                return socket;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                socket.Dispose();
                last = ex;
            }
        }

        throw last ?? new SocketException((int)SocketError.AddressAlreadyInUse);
    }

    private async Task AcceptLoopAsync(Socket listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                // Transient (e.g. the client reset before accept completed): keep listening.
                _context.Logger.Debug($"Accept failed: {ex.SocketErrorCode}.");
                continue;
            }

            socket.NoDelay = true;
            var id = Interlocked.Increment(ref _connectionCounter);
            var connection = new ClientConnection(_context, socket);
            var task = Task.Run(() => connection.RunAsync(cancellationToken), CancellationToken.None);
            _connections[id] = task;
            _ = task.ContinueWith(
                (_, state) => _connections.TryRemove((long)state!, out Task? _),
                id,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
