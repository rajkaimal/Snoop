using System.IO.Compression;
using System.Net;
using System.Security.Authentication;
using System.Text;
using Snoop.Core.Bodies;
using Snoop.Core.Http;
using Snoop.Core.Sessions;
using Snoop.Core.Snapshots;

namespace Snoop.Core.Tests.Snapshots;

public sealed class SnapshotFileTests
{
    private static Session NewSession(long id, string url = "https://example.com/api/items?x=1")
    {
        var request = new RequestInfo("POST", new Uri(url), "HTTP/1.1", new HttpHeaderList([new("Host", "example.com"), new("Content-Type", "application/json"), new("X-Dup", "1"), new("X-Dup", "2")]))
        {
            Body = CapturedBody.FromBytes(Encoding.UTF8.GetBytes("{\"q\":1}")),
        };

        var session = new Session(
            id,
            SessionKind.Http,
            new ConnectionEndpoints(new IPEndPoint(IPAddress.Loopback, 51000), new IPEndPoint(IPAddress.Loopback, 8866)),
            new ClientProcessInfo(4321, "chrome"),
            request)
        {
            Tls = new TlsInfo("example.com", SslProtocols.Tls13, "TLS_AES_128_GCM_SHA256", Decrypted: true),
            Flags = SessionFlags.Decrypted | SessionFlags.Modified,
            ModifiedBy = "rule 'mock'",
            Note = "a note",
        };
        session.Response = new ResponseInfo(201, "Created", "HTTP/2", new HttpHeaderList([new("Content-Type", "image/png")]))
        {
            Body = CapturedBody.FromBytes([0x89, 0x50, 0x4E, 0x47, 0, 1, 2, 255]),
        };
        session.Timings.MarkRequestSent();
        session.Timings.MarkResponseHeaders();
        session.Complete();
        return session;
    }

    private static IReadOnlyList<Session> RoundTrip(IEnumerable<Session> sessions, SessionIdSource? ids = null)
    {
        using var file = new MemoryStream();
        SnapshotFile.Save(file, sessions);
        file.Position = 0;
        return SnapshotFile.Load(file, ids ?? new SessionIdSource());
    }

    [Fact]
    public void Round_trip_keeps_everything_byte_for_byte()
    {
        using var original = NewSession(5);

        var loaded = Assert.Single(RoundTrip([original]));

        using (loaded)
        {
            Assert.Equal("POST", loaded.Request.Method);
            Assert.Equal(original.Request.Url, loaded.Request.Url);
            Assert.Equal(original.Request.Headers.ToString(), loaded.Request.Headers.ToString());
            Assert.Equal("{\"q\":1}", Encoding.UTF8.GetString(loaded.Request.Body!.ToArray()));
            Assert.Equal(201, loaded.Response!.StatusCode);
            Assert.Equal("Created", loaded.Response.ReasonPhrase);
            Assert.Equal("HTTP/2", loaded.Response.HttpVersion);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 1, 2, 255 }, loaded.Response.Body!.ToArray());
            Assert.Equal(SessionState.Completed, loaded.State);
            Assert.Equal(SessionFlags.Decrypted | SessionFlags.Modified | SessionFlags.Imported, loaded.Flags);
            Assert.Equal("rule 'mock'", loaded.ModifiedBy);
            Assert.Equal("a note", loaded.Note);
            Assert.Equal("chrome:4321", loaded.ClientProcess!.ToString());
            Assert.Equal(original.Endpoints.Client, loaded.Endpoints.Client);
            Assert.Equal(SslProtocols.Tls13, loaded.Tls!.Protocol);
            Assert.True(loaded.Tls.Decrypted);
            Assert.Equal(original.Timings.StartedAt, loaded.Timings.StartedAt);
            Assert.InRange(loaded.Timings.Duration!.Value.TotalMilliseconds, original.Timings.Duration!.Value.TotalMilliseconds - 1, original.Timings.Duration.Value.TotalMilliseconds + 1);
        }
    }

    [Fact]
    public void Loaded_sessions_get_new_numbers()
    {
        using var a = NewSession(1);
        using var b = NewSession(2);
        var ids = new SessionIdSource();
        ids.Next();
        ids.Next();
        ids.Next();

        var loaded = RoundTrip([a, b], ids);

        Assert.Equal(new[] { 4L, 5L }, loaded.Select(s => s.Id));
        foreach (var session in loaded)
        {
            session.Dispose();
        }
    }

    [Fact]
    public void Running_session_loads_as_aborted()
    {
        using var running = new Session(
            1,
            SessionKind.Http,
            new ConnectionEndpoints(new IPEndPoint(IPAddress.Loopback, 1), new IPEndPoint(IPAddress.Loopback, 2)),
            null,
            new RequestInfo("GET", new Uri("http://example.com/"), "HTTP/1.1", new HttpHeaderList()));

        using var loaded = Assert.Single(RoundTrip([running]));

        Assert.Equal(SessionState.Aborted, loaded.State);
        Assert.True(loaded.IsFinished);
        Assert.Contains("still running", loaded.Error, StringComparison.Ordinal);
        Assert.Null(loaded.ClientProcess);
        Assert.Null(loaded.Response);
    }

    [Fact]
    public void Truncated_body_keeps_its_original_size()
    {
        using var session = NewSession(1);
        session.Response = session.Response! with { Body = CapturedBody.FromBytes([1, 2, 3], totalBytes: 1000) };

        using var loaded = Assert.Single(RoundTrip([session]));

        Assert.True(loaded.Response!.Body!.IsTruncated);
        Assert.Equal(1000, loaded.Response.Body.TotalBytes);
        Assert.Equal(3, loaded.Response.Body.StoredBytes);
    }

    [Fact]
    public void Original_request_is_kept()
    {
        using var session = NewSession(1);
        session.OriginalRequest = new RequestInfo("GET", new Uri("https://before.example.com/"), "HTTP/1.1", new HttpHeaderList());

        using var loaded = Assert.Single(RoundTrip([session]));

        Assert.Equal("before.example.com", loaded.OriginalRequest!.Url.Host);
    }

    [Fact]
    public void Not_a_zip_is_rejected_with_a_clear_message()
    {
        using var file = new MemoryStream(Encoding.UTF8.GetBytes("hello"));

        var error = Assert.Throws<InvalidDataException>(() => SnapshotFile.Load(file, new SessionIdSource()));

        Assert.Contains("not a Snoop snapshot", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Zip_without_a_manifest_is_rejected()
    {
        using var file = new MemoryStream();
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("other.txt");
        }

        file.Position = 0;

        var error = Assert.Throws<InvalidDataException>(() => SnapshotFile.Load(file, new SessionIdSource()));

        Assert.Contains("snapshot.json", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Newer_format_is_rejected()
    {
        using var file = new MemoryStream();
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("snapshot.json").Open());
            writer.Write("{\"Format\":\"snoop-snapshot\",\"Version\":99,\"Sessions\":[]}");
        }

        file.Position = 0;

        var error = Assert.Throws<InvalidDataException>(() => SnapshotFile.Load(file, new SessionIdSource()));

        Assert.Contains("newer version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Store_import_adds_finished_sessions_and_skips_used_numbers()
    {
        using var store = new SessionStore();
        var changes = new List<SessionChange>();
        store.SessionChanged += (_, e) => changes.Add(e.Change);
        var first = NewSession(1);
        var duplicate = NewSession(1);

        var added = store.Import([first, duplicate]);

        Assert.Equal(1, added);
        Assert.Same(first, store.Find(1));
        Assert.Equal(new[] { SessionChange.Added, SessionChange.Finished }, changes);
    }

    [Fact]
    public void Session_id_source_counts_up_from_one()
    {
        var ids = new SessionIdSource();

        Assert.Equal(1L, ids.Next());
        Assert.Equal(2L, ids.Next());
    }

    [Fact]
    public void Modified_by_lists_each_cause_once()
    {
        using var session = NewSession(1);
        session.ModifiedBy = null;

        session.AddModifiedBy("rule 'a'");
        session.AddModifiedBy("rule 'a'");
        session.AddModifiedBy("edited at a breakpoint");

        Assert.Equal("rule 'a'; edited at a breakpoint", session.ModifiedBy);
    }

    [Fact]
    public void WebSocket_messages_are_kept()
    {
        using var session = NewSession(1);
        session.WebSocket = new WebSocketLog();
        session.WebSocket.Add(new WebSocketMessage(1, DateTimeOffset.UtcNow, true, WebSocketOpcode.Text, 5, Encoding.UTF8.GetBytes("hello"), false, true, null));
        session.WebSocket.Add(new WebSocketMessage(2, DateTimeOffset.UtcNow, false, WebSocketOpcode.Binary, 3, [1, 2, 3], false, false, "a note"));

        using var loaded = Assert.Single(RoundTrip([session]));

        var messages = loaded.WebSocket!.Snapshot();
        Assert.Equal(2, messages.Count);
        Assert.Equal("hello", Encoding.UTF8.GetString(messages[0].Payload));
        Assert.True(messages[0].FromClient);
        Assert.True(messages[0].WasCompressed);
        Assert.Equal(new byte[] { 1, 2, 3 }, messages[1].Payload);
        Assert.Equal("a note", messages[1].Note);
    }
}
