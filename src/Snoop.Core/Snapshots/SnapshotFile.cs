using System.IO.Compression;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;
using Snoop.Core.Bodies;
using Snoop.Core.Http;
using Snoop.Core.Sessions;

namespace Snoop.Core.Snapshots;

/// <summary>
/// Saves captured sessions to a <c>.snoop</c> file and loads them back. The file is a zip archive
/// with <c>snapshot.json</c> (every session except its bodies) and one entry per body under
/// <c>bodies/</c>, so bodies are stored byte for byte, exactly as captured.
/// </summary>
public static class SnapshotFile
{
    public const string Extension = ".snoop";

    private const string ManifestEntry = "snapshot.json";
    private const string FormatName = "snoop-snapshot";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Writes <paramref name="sessions"/> to <paramref name="destination"/>. Running sessions are saved as they are now.</summary>
    /// <returns>How many sessions were written.</returns>
    public static int Save(Stream destination, IEnumerable<Session> sessions)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(sessions);

        var document = new SnapshotDocument { Created = DateTimeOffset.UtcNow };
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        var index = 0;
        foreach (var session in sessions)
        {
            index++;
            document.Sessions.Add(new SavedSession
            {
                Id = session.Id,
                Kind = session.Kind,
                State = session.State,
                Flags = session.Flags & ~SessionFlags.Imported,
                ClientEndpoint = session.Endpoints.Client.ToString(),
                ProxyEndpoint = session.Endpoints.Proxy.ToString(),
                ProcessId = session.ClientProcess?.ProcessId,
                ProcessName = session.ClientProcess?.Name,
                Request = SaveRequest(zip, session.Request, $"bodies/{index}-request.bin"),
                OriginalRequest = session.OriginalRequest is { } original ? SaveRequest(zip, original, $"bodies/{index}-original-request.bin") : null,
                Response = session.Response is { } response ? SaveResponse(zip, response, $"bodies/{index}-response.bin") : null,
                Tls = session.Tls is { } tls ? new SavedTls { ServerName = tls.ServerName, Protocol = tls.Protocol.ToString(), CipherSuite = tls.CipherSuite, Decrypted = tls.Decrypted } : null,
                Error = session.Error,
                Note = session.Note,
                ModifiedBy = session.ModifiedBy,
                BytesSent = session.BytesSent,
                BytesReceived = session.BytesReceived,
                StartedAt = session.Timings.StartedAt,
                RequestSentMs = Milliseconds(session.Timings.RequestSent),
                ResponseHeadersMs = Milliseconds(session.Timings.ResponseHeadersReceived),
                CompletedMs = Milliseconds(session.Timings.Completed),
                WebSocket = session.WebSocket is { } log ? SaveWebSocket(log) : null,
            });
        }

        var manifest = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
        using (var stream = manifest.Open())
        {
            JsonSerializer.Serialize(stream, document, JsonOptions);
        }

        return document.Sessions.Count;
    }

    /// <summary>
    /// Reads a snapshot. Sessions get new numbers from <paramref name="ids"/> (so they never clash with
    /// sessions already captured) and are marked <see cref="SessionFlags.Imported"/>. Sessions that were
    /// still running when saved are loaded as aborted.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a Snoop snapshot, or is damaged.</exception>
    public static IReadOnlyList<Session> Load(Stream source, SessionIdSource ids, BodyCaptureOptions? bodyOptions = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(ids);
        bodyOptions ??= BodyCaptureOptions.Default;

        ZipArchive zip;
        try
        {
            zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("This is not a Snoop snapshot file.", ex);
        }

        using (zip)
        {
            var manifest = zip.GetEntry(ManifestEntry) ?? throw new InvalidDataException("This is not a Snoop snapshot file (snapshot.json is missing).");
            SnapshotDocument document;
            try
            {
                using var stream = manifest.Open();
                document = JsonSerializer.Deserialize<SnapshotDocument>(stream, JsonOptions)
                           ?? throw new InvalidDataException("The snapshot is empty.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"The snapshot is damaged: {ex.Message}", ex);
            }

            if (document.Format != FormatName)
            {
                throw new InvalidDataException("This is not a Snoop snapshot file.");
            }

            if (document.Version > FormatVersion)
            {
                throw new InvalidDataException($"This snapshot was saved by a newer version of Snoop (format {document.Version}).");
            }

            var sessions = new List<Session>(document.Sessions.Count);
            try
            {
                foreach (var saved in document.Sessions)
                {
                    sessions.Add(Restore(zip, saved, ids, bodyOptions));
                }
            }
            catch
            {
                foreach (var session in sessions)
                {
                    session.Dispose();
                }

                throw;
            }

            return sessions;
        }
    }

    // ------------------------------------------------------------ Saving

    private static SavedRequest SaveRequest(ZipArchive zip, RequestInfo request, string bodyEntry) => new()
    {
        Method = request.Method,
        Url = request.Url.OriginalString,
        HttpVersion = request.HttpVersion,
        Headers = SaveHeaders(request.Headers),
        Body = SaveBody(zip, request.Body, bodyEntry),
    };

    private static SavedResponse SaveResponse(ZipArchive zip, ResponseInfo response, string bodyEntry) => new()
    {
        StatusCode = response.StatusCode,
        ReasonPhrase = response.ReasonPhrase,
        HttpVersion = response.HttpVersion,
        Headers = SaveHeaders(response.Headers),
        Body = SaveBody(zip, response.Body, bodyEntry),
    };

    private static List<string[]> SaveHeaders(HttpHeaderList headers) => [.. headers.Select(h => new[] { h.Name, h.Value })];

    private static SavedBody? SaveBody(ZipArchive zip, CapturedBody? body, string entryName)
    {
        if (body is null)
        {
            return null;
        }

        var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
        long stored;
        using (var output = entry.Open())
        {
            try
            {
                using var input = body.OpenRead();
                input.CopyTo(output);
                stored = input.Length;
            }
            catch (ObjectDisposedException)
            {
                // The session was removed while saving; keep the entry empty.
                stored = 0;
            }
        }

        return new SavedBody { Entry = entryName, TotalBytes = Math.Max(body.TotalBytes, stored), StoredBytes = stored };
    }

    private static double? Milliseconds(TimeSpan? span) => span?.TotalMilliseconds;

    private static SavedWebSocket SaveWebSocket(WebSocketLog log) => new()
    {
        Dropped = log.DroppedCount,
        Messages = [.. log.Snapshot().Select(m => new SavedWebSocketMessage
        {
            Time = m.Time,
            FromClient = m.FromClient,
            Opcode = m.Opcode,
            Length = m.Length,
            Payload = m.Payload,
            IsTruncated = m.IsTruncated,
            WasCompressed = m.WasCompressed,
            Note = m.Note,
        })],
    };

    // ------------------------------------------------------------ Loading

    private static Session Restore(ZipArchive zip, SavedSession saved, SessionIdSource ids, BodyCaptureOptions bodyOptions)
    {
        var request = RestoreRequest(zip, saved.Request ?? throw new InvalidDataException("A session in the snapshot has no request."), bodyOptions);
        var endpoints = new ConnectionEndpoints(ParseEndpoint(saved.ClientEndpoint), ParseEndpoint(saved.ProxyEndpoint));
        var process = saved.ProcessName is { } name ? new ClientProcessInfo(saved.ProcessId ?? 0, name) : null;

        var session = new Session(ids.Next(), saved.Kind, endpoints, process, request)
        {
            Flags = saved.Flags | SessionFlags.Imported,
            Error = saved.Error,
            Note = saved.Note,
            ModifiedBy = saved.ModifiedBy,
            BytesSent = saved.BytesSent,
            BytesReceived = saved.BytesReceived,
        };

        try
        {
            if (saved.OriginalRequest is { } original)
            {
                session.OriginalRequest = RestoreRequest(zip, original, bodyOptions);
            }

            if (saved.Response is { } response)
            {
                session.Response = new ResponseInfo(response.StatusCode, response.ReasonPhrase ?? string.Empty, response.HttpVersion ?? "HTTP/1.1", RestoreHeaders(response.Headers))
                {
                    Body = RestoreBody(zip, response.Body, bodyOptions),
                };
            }

            if (saved.WebSocket is { } webSocket)
            {
                var log = new WebSocketLog();
                foreach (var m in webSocket.Messages ?? [])
                {
                    log.Add(new WebSocketMessage(log.NextIndex(), m.Time, m.FromClient, m.Opcode, m.Length, m.Payload ?? [], m.IsTruncated, m.WasCompressed, m.Note));
                }

                log.RestoreDropped(webSocket.Dropped);
                session.WebSocket = log;
            }

            if (saved.Tls is { } tls)
            {
                _ = Enum.TryParse<SslProtocols>(tls.Protocol, out var protocol);
                session.Tls = new TlsInfo(tls.ServerName, protocol, tls.CipherSuite, tls.Decrypted);
            }

            var completed = saved.CompletedMs;
            var state = saved.State;
            if (state is not (SessionState.Completed or SessionState.Failed or SessionState.Aborted))
            {
                // It was still running when saved; it can never finish now.
                state = SessionState.Aborted;
                session.Error ??= "The session was still running when the snapshot was saved.";
                completed ??= Math.Max(saved.ResponseHeadersMs ?? 0, saved.RequestSentMs ?? 0);
            }

            session.State = state;
            session.RestoreTimings(SessionTimings.Restore(saved.StartedAt, Span(saved.RequestSentMs), Span(saved.ResponseHeadersMs), Span(completed)));
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static RequestInfo RestoreRequest(ZipArchive zip, SavedRequest saved, BodyCaptureOptions bodyOptions)
    {
        if (!Uri.TryCreate(saved.Url, UriKind.Absolute, out var url))
        {
            throw new InvalidDataException($"The snapshot has an invalid URL '{saved.Url}'.");
        }

        return new RequestInfo(saved.Method ?? "GET", url, saved.HttpVersion ?? "HTTP/1.1", RestoreHeaders(saved.Headers))
        {
            Body = RestoreBody(zip, saved.Body, bodyOptions),
        };
    }

    private static HttpHeaderList RestoreHeaders(List<string[]>? saved)
    {
        var headers = new HttpHeaderList();
        foreach (var pair in saved ?? [])
        {
            if (pair is [var name, var value] && !string.IsNullOrEmpty(name))
            {
                headers.Add(name, value ?? string.Empty);
            }
        }

        return headers;
    }

    private static CapturedBody? RestoreBody(ZipArchive zip, SavedBody? saved, BodyCaptureOptions bodyOptions)
    {
        if (saved is null)
        {
            return null;
        }

        var entry = zip.GetEntry(saved.Entry ?? string.Empty) ?? throw new InvalidDataException($"The snapshot is missing body '{saved.Entry}'.");
        if (entry.Length > bodyOptions.MaxCapturedBytes)
        {
            throw new InvalidDataException($"Body '{saved.Entry}' in the snapshot is larger than the {bodyOptions.MaxCapturedBytes:N0}-byte limit.");
        }

        var body = new CapturedBody(bodyOptions);
        try
        {
            using var input = entry.Open();
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                body.Append(buffer.AsSpan(0, read));
            }
        }
        catch
        {
            body.Dispose();
            throw;
        }

        body.Complete();
        return saved.TotalBytes > body.TotalBytes ? WithTotal(body, saved.TotalBytes) : body;
    }

    private static CapturedBody WithTotal(CapturedBody body, long totalBytes)
    {
        using (body)
        {
            return CapturedBody.FromBytes(body.ToArray(), totalBytes);
        }
    }

    private static IPEndPoint ParseEndpoint(string? text) =>
        text is not null && IPEndPoint.TryParse(text, out var endpoint) ? endpoint : new IPEndPoint(IPAddress.Loopback, 0);

    private static TimeSpan? Span(double? milliseconds) =>
        milliseconds is { } ms && double.IsFinite(ms) && ms >= 0 ? TimeSpan.FromMilliseconds(ms) : null;

    // ------------------------------------------------------------ File model

    private sealed class SnapshotDocument
    {
        public string Format { get; set; } = FormatName;

        public int Version { get; set; } = FormatVersion;

        public DateTimeOffset Created { get; set; }

        public List<SavedSession> Sessions { get; set; } = [];
    }

    private sealed class SavedSession
    {
        public long Id { get; set; }

        public SessionKind Kind { get; set; }

        public SessionState State { get; set; }

        public SessionFlags Flags { get; set; }

        public string? ClientEndpoint { get; set; }

        public string? ProxyEndpoint { get; set; }

        public int? ProcessId { get; set; }

        public string? ProcessName { get; set; }

        public SavedRequest? Request { get; set; }

        public SavedRequest? OriginalRequest { get; set; }

        public SavedResponse? Response { get; set; }

        public SavedTls? Tls { get; set; }

        public string? Error { get; set; }

        public string? Note { get; set; }

        public string? ModifiedBy { get; set; }

        public long BytesSent { get; set; }

        public long BytesReceived { get; set; }

        public DateTimeOffset StartedAt { get; set; }

        public double? RequestSentMs { get; set; }

        public double? ResponseHeadersMs { get; set; }

        public double? CompletedMs { get; set; }

        public SavedWebSocket? WebSocket { get; set; }
    }

    private sealed class SavedWebSocket
    {
        public long Dropped { get; set; }

        public List<SavedWebSocketMessage>? Messages { get; set; }
    }

    private sealed class SavedWebSocketMessage
    {
        public DateTimeOffset Time { get; set; }

        public bool FromClient { get; set; }

        public WebSocketOpcode Opcode { get; set; }

        public long Length { get; set; }

        /// <summary>Base64 in the file.</summary>
        public byte[]? Payload { get; set; }

        public bool IsTruncated { get; set; }

        public bool WasCompressed { get; set; }

        public string? Note { get; set; }
    }

    private sealed class SavedRequest
    {
        public string? Method { get; set; }

        public string? Url { get; set; }

        public string? HttpVersion { get; set; }

        public List<string[]>? Headers { get; set; }

        public SavedBody? Body { get; set; }
    }

    private sealed class SavedResponse
    {
        public int StatusCode { get; set; }

        public string? ReasonPhrase { get; set; }

        public string? HttpVersion { get; set; }

        public List<string[]>? Headers { get; set; }

        public SavedBody? Body { get; set; }
    }

    private sealed class SavedBody
    {
        public string? Entry { get; set; }

        public long TotalBytes { get; set; }

        public long StoredBytes { get; set; }
    }

    private sealed class SavedTls
    {
        public string? ServerName { get; set; }

        public string? Protocol { get; set; }

        public string? CipherSuite { get; set; }

        public bool Decrypted { get; set; }
    }
}
