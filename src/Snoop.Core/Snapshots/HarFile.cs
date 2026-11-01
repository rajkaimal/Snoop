using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Snoop.Core.Bodies;
using Snoop.Core.Http;
using Snoop.Core.Sessions;

namespace Snoop.Core.Snapshots;

/// <summary>The sessions read from a HAR file, and how many entries could not be used.</summary>
public sealed record HarImportResult(IReadOnlyList<Session> Sessions, int Skipped);

/// <summary>
/// Reads an HTTP Archive (HAR 1.2), as saved by browser developer tools, Snoop and most HTTP tools,
/// into sessions marked <see cref="SessionFlags.Imported"/>. HAR stores response bodies already
/// decoded, so <c>Content-Encoding</c> is dropped from imported responses.
/// </summary>
public static class HarFile
{
    public const string Extension = ".har";

    /// <summary>Reads <paramref name="source"/>. Entries without a usable URL are skipped and counted.</summary>
    /// <exception cref="InvalidDataException">The file is not a HAR file.</exception>
    public static HarImportResult Load(Stream source, SessionIdSource ids, BodyCaptureOptions? bodyOptions = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(ids);
        bodyOptions ??= BodyCaptureOptions.Default;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(source, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"This is not a HAR file: {ex.Message}", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("log", out var log)
                || log.ValueKind != JsonValueKind.Object
                || !log.TryGetProperty("entries", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("This is not a HAR file (log.entries is missing).");
            }

            var sessions = new List<Session>(entries.GetArrayLength());
            var skipped = 0;
            try
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    var session = entry.ValueKind == JsonValueKind.Object ? ReadEntry(entry, ids, bodyOptions) : null;
                    if (session is null)
                    {
                        skipped++;
                    }
                    else
                    {
                        sessions.Add(session);
                    }
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

            return new HarImportResult(sessions, skipped);
        }
    }

    private static Session? ReadEntry(JsonElement entry, SessionIdSource ids, BodyCaptureOptions bodyOptions)
    {
        if (!entry.TryGetProperty("request", out var req) || req.ValueKind != JsonValueKind.Object
            || !Uri.TryCreate(GetString(req, "url"), UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https" or "ws" or "wss"))
        {
            return null;
        }

        var method = GetString(req, "method") is { Length: > 0 } m ? m.ToUpperInvariant() : "GET";
        if (method == "CONNECT")
        {
            return null;
        }

        var requestHeaders = ReadHeaders(req);
        var requestBody = req.TryGetProperty("postData", out var postData) && postData.ValueKind == JsonValueKind.Object
            ? ReadBody(postData, bodyOptions)
            : null;
        var request = new RequestInfo(method, url, NormalizeVersion(GetString(req, "httpVersion")), requestHeaders) { Body = requestBody };

        var webSocket = ReadWebSocket(entry);
        var process = ParseProcess(GetString(entry, "_process"));
        var endpoints = new ConnectionEndpoints(new IPEndPoint(IPAddress.Loopback, 0), new IPEndPoint(IPAddress.Loopback, 0));
        var session = new Session(ids.Next(), webSocket is null ? SessionKind.Http : SessionKind.Upgrade, endpoints, process, request)
        {
            Flags = SessionFlags.Imported,
            WebSocket = webSocket,
        };

        try
        {
            if (url.Scheme is "https" or "wss")
            {
                session.Flags |= SessionFlags.Decrypted;
            }

            var status = 0;
            if (entry.TryGetProperty("response", out var res) && res.ValueKind == JsonValueKind.Object)
            {
                status = GetInt(res, "status") ?? 0;
                if (status > 0)
                {
                    var headers = ReadHeaders(res);
                    CapturedBody? body = null;
                    if (res.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object)
                    {
                        body = ReadBody(content, bodyOptions);
                        if (body is null && (GetInt(content, "size") ?? 0) > 0)
                        {
                            session.Note = "The HAR file did not include this response's body.";
                        }
                    }

                    if (body is not null)
                    {
                        // The body is stored decoded; keep the headers consistent with it.
                        headers.Remove("Content-Encoding");
                    }

                    session.Response = new ResponseInfo(status, GetString(res, "statusText") ?? string.Empty, NormalizeVersion(GetString(res, "httpVersion")), headers)
                    {
                        Body = body,
                    };
                }
            }

            if (status > 0)
            {
                session.State = SessionState.Completed;
            }
            else
            {
                session.State = SessionState.Failed;
                session.Error = GetString(entry, "_error") is { Length: > 0 } error
                    ? error
                    : res.ValueKind == JsonValueKind.Object && GetString(res, "statusText") is { Length: > 0 } text ? text : "No response was recorded.";
            }

            if (session.Error is null && GetString(entry, "_error") is { Length: > 0 } entryError)
            {
                session.Error = entryError;
            }

            session.RestoreTimings(ReadTimings(entry));
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static SessionTimings ReadTimings(JsonElement entry)
    {
        var started = DateTimeOffset.TryParse(GetString(entry, "startedDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : DateTimeOffset.UnixEpoch;
        var total = GetDouble(entry, "time");
        double? sent = null, firstByte = null;
        if (entry.TryGetProperty("timings", out var timings) && timings.ValueKind == JsonValueKind.Object)
        {
            // Phases are -1 when they do not apply.
            double Phase(string name) => GetDouble(timings, name) is { } v && v > 0 ? v : 0;
            sent = Phase("blocked") + Phase("dns") + Phase("connect") + Phase("send");
            firstByte = sent + Phase("wait");
            total ??= firstByte + Phase("receive");
        }

        if (total is { } t && firstByte > t)
        {
            firstByte = t;
            sent = Math.Min(sent ?? 0, t);
        }

        return SessionTimings.Restore(started, Span(sent), Span(firstByte), Span(total ?? firstByte ?? 0));
    }

    private static HttpHeaderList ReadHeaders(JsonElement message)
    {
        var headers = new HttpHeaderList();
        if (!message.TryGetProperty("headers", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return headers;
        }

        foreach (var header in list.EnumerateArray())
        {
            var name = header.ValueKind == JsonValueKind.Object ? GetString(header, "name") : null;

            // HTTP/2 pseudo-headers (:authority, :path ...) are not real headers.
            if (!string.IsNullOrEmpty(name) && name[0] != ':')
            {
                headers.Add(name, GetString(header, "value") ?? string.Empty);
            }
        }

        return headers;
    }

    private static CapturedBody? ReadBody(JsonElement content, BodyCaptureOptions bodyOptions)
    {
        byte[] bytes;
        if (GetString(content, "text") is { } text)
        {
            if (string.Equals(GetString(content, "encoding"), "base64", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    bytes = Convert.FromBase64String(text);
                }
                catch (FormatException)
                {
                    bytes = Encoding.UTF8.GetBytes(text);
                }
            }
            else
            {
                bytes = Encoding.UTF8.GetBytes(text);
            }
        }
        else if (content.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
        {
            // Form posts may be saved as name/value pairs only.
            var pairs = parameters.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.Object)
                .Select(p => $"{Uri.EscapeDataString(GetString(p, "name") ?? string.Empty)}={Uri.EscapeDataString(GetString(p, "value") ?? string.Empty)}");
            bytes = Encoding.UTF8.GetBytes(string.Join('&', pairs));
        }
        else
        {
            return null;
        }

        if (bytes.Length == 0)
        {
            return null;
        }

        var limit = (int)Math.Min(bytes.Length, bodyOptions.MaxCapturedBytes);
        return CapturedBody.FromBytes(bytes.AsSpan(0, limit), bytes.Length, bodyOptions);
    }

    /// <summary>Chrome's <c>_webSocketMessages</c> extension.</summary>
    private static WebSocketLog? ReadWebSocket(JsonElement entry)
    {
        if (!entry.TryGetProperty("_webSocketMessages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var log = new WebSocketLog();
        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var opcode = (WebSocketOpcode)(GetInt(message, "opcode") ?? 1);
            var data = GetString(message, "data") ?? string.Empty;
            byte[] payload;
            if (opcode == WebSocketOpcode.Text)
            {
                payload = Encoding.UTF8.GetBytes(data);
            }
            else
            {
                try
                {
                    payload = Convert.FromBase64String(data);
                }
                catch (FormatException)
                {
                    payload = Encoding.UTF8.GetBytes(data);
                }
            }

            var seconds = GetDouble(message, "time") ?? 0;
            var time = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(seconds * 1000));
            var fromClient = string.Equals(GetString(message, "type"), "send", StringComparison.OrdinalIgnoreCase);
            log.Add(new WebSocketMessage(log.NextIndex(), time, fromClient, opcode, payload.Length, payload, IsTruncated: false, WasCompressed: false, Note: null));
        }

        return log;
    }

    /// <summary>Snoop writes "name:pid"; other tools may write just a name.</summary>
    private static ClientProcessInfo? ParseProcess(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var colon = text.LastIndexOf(':');
        return colon > 0 && int.TryParse(text.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            ? new ClientProcessInfo(pid, text[..colon])
            : new ClientProcessInfo(0, text.Trim());
    }

    private static string NormalizeVersion(string? version) => version?.Trim().ToUpperInvariant() switch
    {
        null or "" or "UNKNOWN" => "HTTP/1.1",
        "H2" or "HTTP/2.0" => "HTTP/2",
        "H3" or "HTTP/3.0" => "HTTP/3",
        var v => v,
    };

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static double? GetDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;

    private static TimeSpan? Span(double? milliseconds) =>
        milliseconds is { } ms && ms >= 0 ? TimeSpan.FromMilliseconds(ms) : null;
}
