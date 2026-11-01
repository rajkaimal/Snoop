using System.Globalization;
using System.Text;
using Snoop.Core.Bodies;
using Snoop.Core.Http;
using Snoop.Core.Sessions;
using Snoop.Inspection.Bodies;
using Snoop.Inspection.Formatting;

namespace Snoop.Inspection.Export;

/// <summary>
/// Writes sessions as plain text: for each one a heading, the raw request and the raw response,
/// with compressed bodies decoded so the text is readable. Binary bodies are summarised.
/// </summary>
public static class TextDumpExporter
{
    /// <summary>Longer bodies are cut off.</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    private const string Rule = "================================================================================";

    public static void Write(TextWriter writer, IEnumerable<Session> sessions)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(sessions);
        foreach (var session in sessions.OrderBy(s => s.Id))
        {
            WriteSession(writer, session);
        }
    }

    public static string WriteToString(IEnumerable<Session> sessions)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\r\n" };
        Write(writer, sessions);
        return writer.ToString();
    }

    private static void WriteSession(TextWriter writer, Session session)
    {
        var request = session.Request;
        writer.WriteLine(Rule);
        var heading = new StringBuilder()
            .Append('#').Append(session.Id.ToString(CultureInfo.InvariantCulture)).Append("  ")
            .Append(request.Method).Append(' ').Append(request.Url.AbsoluteUri);
        if (session.Response is { } r)
        {
            heading.Append("  -> ").Append(r.StatusCode.ToString(CultureInfo.InvariantCulture));
        }

        writer.WriteLine(heading.ToString());
        var details = new List<string> { session.Timings.StartedAt.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture) };
        if (session.Timings.Duration is { } duration)
        {
            details.Add($"{duration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms");
        }

        if (session.ClientProcess is { } process)
        {
            details.Add(process.ToString());
        }

        if (session.ModifiedBy is { } modified)
        {
            details.Add($"modified by {modified}");
        }

        writer.WriteLine(string.Join("  |  ", details));
        if (session.Error is { } error)
        {
            writer.WriteLine($"Error: {error}");
        }

        writer.WriteLine(Rule);
        writer.WriteLine();

        var target = request.Method == "CONNECT"
            ? request.Url.GetComponents(UriComponents.HostAndPort, UriFormat.UriEscaped)
            : request.Url.PathAndQuery;
        writer.Write($"{request.Method} {target} {request.HttpVersion}\r\n");
        WriteHeadersAndBody(writer, request.Headers, request.Body);

        writer.WriteLine();
        if (session.Response is { } response)
        {
            writer.Write($"{response.HttpVersion} {response.StatusCode.ToString(CultureInfo.InvariantCulture)} {response.ReasonPhrase}\r\n");
            WriteHeadersAndBody(writer, response.Headers, response.Body);
        }
        else
        {
            writer.WriteLine("(no response)");
        }

        writer.WriteLine();
        writer.WriteLine();
    }

    private static void WriteHeadersAndBody(TextWriter writer, HttpHeaderList headers, CapturedBody? body)
    {
        writer.Write(headers.ToString());
        writer.Write("\r\n");
        if (body is null || body.TotalBytes == 0)
        {
            return;
        }

        var stored = body.ToArray();
        var decoded = ContentDecoder.Decode(stored, headers, MaxBodyBytes * 4);
        var bytes = decoded.Bytes;
        if (decoded.WasDecoded)
        {
            writer.WriteLine($"[decoded from {string.Join(", ", decoded.AppliedEncodings)}]");
        }

        if (!ContentSniffer.LooksLikeText(bytes) || ContentSniffer.IsImage(bytes))
        {
            writer.WriteLine($"[{bytes.Length.ToString("N0", CultureInfo.InvariantCulture)} bytes of binary data]");
            return;
        }

        var shown = Math.Min(bytes.Length, MaxBodyBytes);
        writer.Write(TextDecoder.Decode(bytes.AsSpan(0, shown), ContentSniffer.Charset(headers.Get("Content-Type"))));
        writer.WriteLine();
        var more = (long)bytes.Length - shown + (body.TotalBytes - body.StoredBytes);
        if (more > 0)
        {
            writer.WriteLine($"[... {more.ToString("N0", CultureInfo.InvariantCulture)} more bytes not shown]");
        }
    }
}
