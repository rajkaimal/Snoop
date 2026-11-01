using System.Globalization;
using System.Text;
using Snoop.Core.Formatting;
using Snoop.Core.Http;
using Snoop.Inspection.Bodies;
using Snoop.Inspection.Formatting;
using Snoop.Proxy.Interception;

namespace Snoop.Presentation.Editing;

/// <summary>How a body is shown for editing.</summary>
/// <param name="Text">The body as text, or empty when it cannot be edited as text.</param>
/// <param name="IsEditable">False for binary or undecodable bodies, which are always sent unchanged.</param>
/// <param name="WasDecoded">True when <paramref name="Text"/> is the decompressed body.</param>
/// <param name="Note">A hint for the user, or empty.</param>
public sealed record BodyText(string Text, bool IsEditable, bool WasDecoded, string Note)
{
    public static BodyText Empty { get; } = new(string.Empty, true, false, string.Empty);
}

/// <summary>
/// Turns messages into editable text (a start line plus "Name: value" header lines) and back.
/// Used by the breakpoint editor and the Composer. Accepts any line endings, because text boxes
/// on Windows return carriage returns.
/// </summary>
public static class MessageText
{
    /// <summary>Headers as "Name: value" lines.</summary>
    public static string FormatHeaders(IEnumerable<HttpHeader> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        return string.Join("\n", headers.Select(h => $"{h.Name}: {h.Value}"));
    }

    /// <summary>Parses "Name: value" lines. Blank lines are skipped.</summary>
    /// <param name="lines">The header lines.</param>
    /// <param name="headers">The parsed headers.</param>
    /// <param name="error">What is wrong, quoting the line number as the user sees it.</param>
    /// <param name="firstLineNumber">The number of the first line in the editor (2 when a start line comes first).</param>
    public static bool TryParseHeaders(IEnumerable<string> lines, out HttpHeaderList headers, out string? error, int firstLineNumber = 1)
    {
        ArgumentNullException.ThrowIfNull(lines);
        headers = new HttpHeaderList();
        error = null;
        var number = firstLineNumber - 1;
        foreach (var raw in lines)
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var name = colon > 0 ? line[..colon].Trim() : string.Empty;
            if (name.Length == 0 || name.Any(c => c <= ' ' || c >= 127 || "()<>@,;:\\\"/[]?={}".Contains(c, StringComparison.Ordinal)))
            {
                error = $"Header line {number} should look like \"Name: value\": {Shorten(line)}";
                return false;
            }

            headers.Add(name, line[(colon + 1)..].Trim());
        }

        return true;
    }

    public static bool TryParseHeaders(string text, out HttpHeaderList headers, out string? error) =>
        TryParseHeaders(SplitLines(text ?? string.Empty), out headers, out error);

    /// <summary>"METHOD URL HTTP/1.1" then the headers.</summary>
    public static string FormatRequestHead(EditableRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var head = $"{request.Method} {request.Url.AbsoluteUri} HTTP/1.1";
        return request.Headers.Count == 0 ? head : head + "\n" + FormatHeaders(request.Headers);
    }

    /// <summary>Reads an edited request head into <paramref name="request"/>. Nothing changes when it fails.</summary>
    public static bool TryApplyRequestHead(string text, EditableRequest request, out string? error)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lines = SplitLines(text ?? string.Empty).SkipWhile(string.IsNullOrWhiteSpace).ToList();
        if (lines.Count == 0)
        {
            error = "The request line is missing (for example: GET https://example.com/ HTTP/1.1).";
            return false;
        }

        var parts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 3)
        {
            error = $"The first line should be \"METHOD URL HTTP/1.1\": {Shorten(lines[0])}";
            return false;
        }

        if (parts[0].Any(c => c <= ' ' || c >= 127 || "()<>@,;:\\\"/[]?={}".Contains(c, StringComparison.Ordinal)))
        {
            error = $"'{parts[0]}' is not a valid method.";
            return false;
        }

        if (!Uri.TryCreate(parts[1], UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(url.Host))
        {
            error = $"'{Shorten(parts[1])}' is not an absolute http:// or https:// URL.";
            return false;
        }

        if (!TryParseHeaders(lines.Skip(1), out var headers, out error, firstLineNumber: 2))
        {
            return false;
        }

        request.Method = parts[0].ToUpperInvariant();
        request.Url = url;
        request.Headers = headers;
        return true;
    }

    /// <summary>"HTTP/1.1 200 OK" then the headers.</summary>
    public static string FormatResponseHead(EditableResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var head = $"HTTP/1.1 {response.StatusCode.ToString(CultureInfo.InvariantCulture)} {response.ReasonPhrase}".TrimEnd();
        return response.Headers.Count == 0 ? head : head + "\n" + FormatHeaders(response.Headers);
    }

    /// <summary>Reads an edited response head into <paramref name="response"/>. Nothing changes when it fails.</summary>
    public static bool TryApplyResponseHead(string text, EditableResponse response, out string? error)
    {
        ArgumentNullException.ThrowIfNull(response);
        var lines = SplitLines(text ?? string.Empty).SkipWhile(string.IsNullOrWhiteSpace).ToList();
        if (lines.Count == 0)
        {
            error = "The status line is missing (for example: HTTP/1.1 200 OK).";
            return false;
        }

        var parts = lines[0].Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status) || status is < 100 or > 599)
        {
            error = $"The first line should be \"HTTP/1.1 <status 100-599> <reason>\": {Shorten(lines[0])}";
            return false;
        }

        if (!TryParseHeaders(lines.Skip(1), out var headers, out error, firstLineNumber: 2))
        {
            return false;
        }

        response.StatusCode = status;
        response.ReasonPhrase = parts.Length > 2 ? parts[2].Trim() : string.Empty;
        response.Headers = headers;
        return true;
    }

    /// <summary>How to show <paramref name="body"/> for editing: decompressed text, or not editable.</summary>
    public static BodyText DescribeBody(byte[]? body, HttpHeaderList headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (body is null || body.Length == 0)
        {
            return BodyText.Empty;
        }

        var decoded = ContentDecoder.Decode(body, headers);
        if (decoded.Error is not null)
        {
            return new BodyText(string.Empty, false, false, $"The compressed body could not be decoded ({decoded.Error}); it is sent unchanged.");
        }

        if (!ContentSniffer.LooksLikeText(decoded.Bytes))
        {
            return new BodyText(string.Empty, false, decoded.WasDecoded, $"Binary body ({ByteSize.Format(decoded.Bytes.Length)}); it is sent unchanged.");
        }

        var text = TextDecoder.Decode(decoded.Bytes, ContentSniffer.Charset(headers.Get("Content-Type")));
        var note = decoded.WasDecoded
            ? $"Shown decompressed ({string.Join(", ", decoded.AppliedEncodings)}). If you change it, it is sent uncompressed."
            : string.Empty;
        return new BodyText(text, true, decoded.WasDecoded, note);
    }

    /// <summary>
    /// The body to send after editing. When the text is unchanged (ignoring line endings) the original bytes
    /// are kept; otherwise the text is sent as UTF-8 and any Content-Encoding header is dropped.
    /// </summary>
    public static byte[]? ApplyBody(byte[]? original, BodyText shown, string editedText, HttpHeaderList headers)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(headers);
        editedText ??= string.Empty;
        if (!shown.IsEditable || NormalizeNewlines(editedText) == NormalizeNewlines(shown.Text))
        {
            return original;
        }

        if (shown.WasDecoded)
        {
            headers.Remove("Content-Encoding");
        }

        var lineEnding = shown.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return Encoding.UTF8.GetBytes(NormalizeNewlines(editedText).Replace("\n", lineEnding, StringComparison.Ordinal));
    }

    /// <summary>Converts \r\n and \r to \n.</summary>
    public static string NormalizeNewlines(string text) =>
        (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    public static IEnumerable<string> SplitLines(string text) => NormalizeNewlines(text).Split('\n');

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..80] + "...";
}
