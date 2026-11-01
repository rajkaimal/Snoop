using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Web;
using Snoop.Core.Bodies;
using Snoop.Core.Http;
using Snoop.Core.Sessions;
using Snoop.Inspection.Bodies;
using Snoop.Inspection.Formatting;

namespace Snoop.Inspection.Export;

/// <summary>Options for <see cref="HarExporter"/>.</summary>
public sealed record HarExportOptions
{
    /// <summary>Replace Authorization, Cookie, Set-Cookie and token-like query values with "[redacted]".</summary>
    public bool RedactSecrets { get; init; }

    public string CreatorName { get; init; } = "Snoop";

    public string CreatorVersion { get; init; } = "0.2";
}

/// <summary>
/// Writes sessions as an HTTP Archive (HAR 1.2), the format browser developer tools and most
/// HTTP debugging tools import. CONNECT tunnels are left out; response bodies are stored decoded,
/// binary bodies as base64.
/// </summary>
public static class HarExporter
{
    private const string Redacted = "[redacted]";

    private static readonly HashSet<string> SecretHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Set-Cookie",
        "X-Api-Key",
        "X-Auth-Token",
    };

    private static readonly string[] SecretQueryWords = ["token", "key", "secret", "password", "pwd", "sig", "signature", "auth", "session", "code"];

    public static void Write(Stream output, IEnumerable<Session> sessions, HarExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(sessions);
        options ??= new HarExportOptions();

        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        writer.WriteStartObject();
        writer.WriteStartObject("log");
        writer.WriteString("version", "1.2");
        writer.WriteStartObject("creator");
        writer.WriteString("name", options.CreatorName);
        writer.WriteString("version", options.CreatorVersion);
        writer.WriteEndObject();
        writer.WriteStartArray("entries");
        foreach (var session in sessions.Where(s => s.Kind != SessionKind.Connect).OrderBy(s => s.Id))
        {
            WriteEntry(writer, session, options);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    public static string WriteToString(IEnumerable<Session> sessions, HarExportOptions? options = null)
    {
        using var stream = new MemoryStream();
        Write(stream, sessions, options);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteEntry(Utf8JsonWriter writer, Session session, HarExportOptions options)
    {
        var timings = session.Timings;
        var total = timings.Duration?.TotalMilliseconds ?? 0;
        var sent = timings.RequestSent?.TotalMilliseconds ?? 0;
        var firstByte = timings.ResponseHeadersReceived?.TotalMilliseconds ?? sent;

        writer.WriteStartObject();
        writer.WriteString("startedDateTime", timings.StartedAt.ToString("o", CultureInfo.InvariantCulture));
        writer.WriteNumber("time", Math.Round(total, 3));
        WriteRequest(writer, session.Request, options);
        WriteResponse(writer, session, options);
        writer.WriteStartObject("cache");
        writer.WriteEndObject();
        writer.WriteStartObject("timings");
        writer.WriteNumber("send", Math.Round(sent, 3));
        writer.WriteNumber("wait", Math.Round(Math.Max(0, firstByte - sent), 3));
        writer.WriteNumber("receive", Math.Round(Math.Max(0, total - firstByte), 3));
        writer.WriteEndObject();
        if (session.ClientProcess is { } process)
        {
            writer.WriteString("_process", process.ToString());
        }

        if (session.Error is { } error)
        {
            writer.WriteString("_error", error);
        }

        if (session.WebSocket is { } webSocket)
        {
            WriteWebSocketMessages(writer, webSocket);
        }

        writer.WriteEndObject();
    }

    /// <summary>WebSocket messages in Chrome's HAR extension format (<c>_webSocketMessages</c>).</summary>
    private static void WriteWebSocketMessages(Utf8JsonWriter writer, WebSocketLog log)
    {
        writer.WriteStartArray("_webSocketMessages");
        foreach (var message in log.Snapshot())
        {
            writer.WriteStartObject();
            writer.WriteString("type", message.FromClient ? "send" : "receive");
            writer.WriteNumber("time", Math.Round(message.Time.ToUnixTimeMilliseconds() / 1000.0, 3));
            writer.WriteNumber("opcode", (int)message.Opcode);
            writer.WriteString(
                "data",
                message.Opcode == WebSocketOpcode.Text && message.Note is null
                    ? System.Text.Encoding.UTF8.GetString(message.Payload)
                    : Convert.ToBase64String(message.Payload));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteRequest(Utf8JsonWriter writer, RequestInfo request, HarExportOptions options)
    {
        writer.WriteStartObject("request");
        writer.WriteString("method", request.Method);
        writer.WriteString("url", options.RedactSecrets ? RedactUrl(request.Url) : request.Url.AbsoluteUri);
        writer.WriteString("httpVersion", request.HttpVersion);
        WriteHeaders(writer, request.Headers, options);
        WriteCookies(writer, "cookies");
        writer.WriteStartArray("queryString");
        var query = HttpUtility.ParseQueryString(request.Url.Query);
        foreach (var name in query.AllKeys.Where(k => k is not null))
        {
            foreach (var value in query.GetValues(name!) ?? [])
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteString("value", options.RedactSecrets && IsSecretName(name!) ? Redacted : value);
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();
        writer.WriteNumber("headersSize", -1);
        writer.WriteNumber("bodySize", request.Body?.TotalBytes ?? 0);
        if (request.Body is { TotalBytes: > 0 } body)
        {
            var bytes = body.ToArray();
            writer.WriteStartObject("postData");
            writer.WriteString("mimeType", request.Headers.Get("Content-Type") ?? string.Empty);
            if (ContentSniffer.LooksLikeText(bytes))
            {
                writer.WriteString("text", TextDecoder.Decode(bytes, ContentSniffer.Charset(request.Headers.Get("Content-Type"))));
            }
            else
            {
                writer.WriteString("text", Convert.ToBase64String(bytes));
                writer.WriteString("encoding", "base64");
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteResponse(Utf8JsonWriter writer, Session session, HarExportOptions options)
    {
        var response = session.Response;
        writer.WriteStartObject("response");
        writer.WriteNumber("status", response?.StatusCode ?? 0);
        writer.WriteString("statusText", response?.ReasonPhrase ?? session.Error ?? string.Empty);
        writer.WriteString("httpVersion", response?.HttpVersion ?? session.Request.HttpVersion);
        WriteHeaders(writer, response?.Headers ?? new HttpHeaderList(), options);
        WriteCookies(writer, "cookies");

        writer.WriteStartObject("content");
        var headers = response?.Headers ?? new HttpHeaderList();
        WriteContent(writer, response?.Body, headers);
        writer.WriteEndObject();

        writer.WriteString("redirectURL", headers.Get("Location") ?? string.Empty);
        writer.WriteNumber("headersSize", -1);
        writer.WriteNumber("bodySize", response?.Body?.TotalBytes ?? 0);
        writer.WriteEndObject();
    }

    private static void WriteContent(Utf8JsonWriter writer, CapturedBody? body, HttpHeaderList headers)
    {
        var mimeType = headers.Get("Content-Type") ?? string.Empty;
        if (body is null || body.TotalBytes == 0)
        {
            writer.WriteNumber("size", 0);
            writer.WriteString("mimeType", mimeType);
            return;
        }

        var decoded = ContentDecoder.Decode(body.ToArray(), headers);
        writer.WriteNumber("size", decoded.Bytes.Length);
        if (decoded.WasDecoded)
        {
            writer.WriteNumber("compression", decoded.Bytes.Length - body.StoredBytes);
        }

        writer.WriteString("mimeType", mimeType);
        if (ContentSniffer.LooksLikeText(decoded.Bytes) && !ContentSniffer.IsImage(decoded.Bytes))
        {
            writer.WriteString("text", TextDecoder.Decode(decoded.Bytes, ContentSniffer.Charset(mimeType)));
        }
        else
        {
            writer.WriteString("text", Convert.ToBase64String(decoded.Bytes));
            writer.WriteString("encoding", "base64");
        }
    }

    private static void WriteHeaders(Utf8JsonWriter writer, HttpHeaderList headers, HarExportOptions options)
    {
        writer.WriteStartArray("headers");
        foreach (var header in headers)
        {
            writer.WriteStartObject();
            writer.WriteString("name", header.Name);
            writer.WriteString("value", options.RedactSecrets && SecretHeaders.Contains(header.Name) ? Redacted : header.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteCookies(Utf8JsonWriter writer, string name)
    {
        // Cookies are already present in the headers; parsed cookie lists are optional in HAR.
        writer.WriteStartArray(name);
        writer.WriteEndArray();
    }

    private static bool IsSecretName(string name) =>
        SecretQueryWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static string RedactUrl(Uri url)
    {
        if (string.IsNullOrEmpty(url.Query))
        {
            return url.AbsoluteUri;
        }

        var parts = url.Query.TrimStart('?').Split('&').Select(part =>
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            var name = equals < 0 ? part : part[..equals];
            return equals >= 0 && IsSecretName(Uri.UnescapeDataString(name)) ? $"{name}={Uri.EscapeDataString(Redacted)}" : part;
        });
        var builder = new UriBuilder(url) { Query = string.Join('&', parts) };
        return builder.Uri.AbsoluteUri;
    }
}
