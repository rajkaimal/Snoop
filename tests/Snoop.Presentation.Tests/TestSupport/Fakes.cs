using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Snoop.Core.Bodies;
using Snoop.Core.Http;
using Snoop.Core.Sessions;
using Snoop.Hosting;
using Snoop.Platform.SystemProxy;
using Snoop.Presentation.Services;
using Snoop.Proxy.Processes;
using Snoop.Proxy.Tls;

namespace Snoop.Presentation.Tests.TestSupport;

/// <summary>Runs posted work immediately; timers fire only when <see cref="Tick"/> is called.</summary>
internal sealed class FakeDispatcher : IUiDispatcher
{
    private readonly List<Action> _timers = [];

    public void Post(Action action) => action();

    public IDisposable StartTimer(TimeSpan interval, Action tick)
    {
        _timers.Add(tick);
        return new Registration(() => _timers.Remove(tick));
    }

    public void Tick()
    {
        foreach (var timer in _timers.ToList())
        {
            timer();
        }
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

internal sealed class FakeClipboard : IClipboardService
{
    public string? Text { get; private set; }

    public void SetText(string text) => Text = text;
}

internal sealed class FakeFiles : IFileSaveService
{
    public bool Cancel { get; set; }

    public byte[]? Saved { get; private set; }

    public string? SuggestedName { get; private set; }

    public Task<Stream?> PickSaveFileAsync(string suggestedName, string fileTypeName, string extension)
    {
        SuggestedName = suggestedName;
        return Task.FromResult<Stream?>(Cancel ? null : new CapturingStream(bytes => Saved = bytes));
    }

    private sealed class CapturingStream(Action<byte[]> onClose) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                onClose(ToArray());
            }

            base.Dispose(disposing);
        }
    }
}

internal sealed class FakeProxyApi : IProxySettingsApi
{
    public ProxySettings Current { get; private set; } = ProxySettings.Direct;

    public ProxySettings Read() => Current;

    public void Write(ProxySettings settings) => Current = settings;
}

internal sealed class FakeTrust : IRootCertificateTrust
{
    public bool Trusted { get; set; }

    public bool IsSupported => true;

    public bool IsTrusted(X509Certificate2 root) => Trusted;

    public void Trust(X509Certificate2 root) => Trusted = true;

    public int RemoveAll() => 0;
}

/// <summary>A real <see cref="CaptureHost"/> wired to fakes and a temp folder.</summary>
internal sealed class TestHost : IAsyncDisposable
{
    private static long _nextId = 1000;

    public TestHost()
    {
        Directory = Path.Combine(Path.GetTempPath(), "snoop-tests", Guid.NewGuid().ToString("N"));
        Host = new CaptureHost(services: new CaptureHostServices(
            () => new CertificateAuthorityStore(Directory, UnprotectedSecretProtector.Instance),
            new SystemProxyController(ProxyApi, Path.Combine(Directory, "proxy-backup.json")),
            NullClientProcessResolver.Instance,
            Trust));
    }

    public string Directory { get; }

    public FakeProxyApi ProxyApi { get; } = new();

    public FakeTrust Trust { get; } = new();

    public CaptureHost Host { get; }

    public ISessionSink Sink => Host.Sessions;

    /// <summary>Adds a finished session to the store as the proxy would.</summary>
    public Session AddSession(string url = "https://example.com/api", int? status = 200, string contentType = "application/json", string body = "{\"a\":1}", SessionKind kind = SessionKind.Http)
    {
        var session = NewSession(url, kind);
        Sink.OnSessionStarted(session);
        if (status is not null)
        {
            Respond(session, status.Value, contentType, body);
        }

        session.Complete();
        Sink.OnSessionFinished(session);
        return session;
    }

    public static Session NewSession(string url = "https://example.com/api", SessionKind kind = SessionKind.Http) =>
        new(
            Interlocked.Increment(ref _nextId),
            kind,
            new ConnectionEndpoints(new IPEndPoint(IPAddress.Loopback, 50000), new IPEndPoint(IPAddress.Loopback, 8866)),
            new ClientProcessInfo(42, "chrome"),
            new RequestInfo(kind == SessionKind.Connect ? "CONNECT" : "GET", new Uri(url), "HTTP/1.1", new HttpHeaderList([new("Host", new Uri(url).Authority)])));

    public static void Respond(Session session, int status, string contentType, string body)
    {
        var captured = new CapturedBody();
        captured.Append(Encoding.UTF8.GetBytes(body));
        captured.Complete();
        session.Response = new ResponseInfo(status, "OK", "HTTP/1.1", new HttpHeaderList([new("Content-Type", contentType)])) { Body = captured };
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}

internal sealed class FakeOpenFiles : IFileOpenService
{
    public string? Path { get; set; }

    public string? LastExtension { get; private set; }

    public Task<string?> PickFileAsync(string fileTypeName, params string[] extensions)
    {
        LastExtension = extensions.FirstOrDefault();
        return Task.FromResult(Path);
    }
}
