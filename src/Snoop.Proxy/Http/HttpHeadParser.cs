using System.Globalization;
using System.Text;
using Snoop.Core.Http;

namespace Snoop.Proxy.Http;

/// <summary>
/// Parses HTTP/1.x message heads (start line + header fields) following RFC 9110/9112.
/// Strict where looseness enables request smuggling (whitespace before the colon,
/// obsolete line folding, bad tokens); tolerant of bare LF line endings.
/// </summary>
public static class HttpHeadParser
{
    // Header bytes are not guaranteed to be ASCII; Latin-1 maps every byte to one char and back.
    private static readonly Encoding HeaderEncoding = Encoding.Latin1;

    public static RequestHead ParseRequest(ReadOnlySpan<byte> head)
    {
        var lines = SplitLines(head);
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            throw new HttpParseException($"Invalid request line: '{Truncate(lines[0])}'.");
        }

        var method = parts[0];
        if (!IsToken(method))
        {
            throw new HttpParseException($"Invalid request method: '{Truncate(method)}'.");
        }

        var target = parts[1];
        if (target.Any(c => c <= ' ' || c == '\x7f'))
        {
            throw new HttpParseException("Request target contains whitespace or control characters.");
        }

        var version = ParseVersion(parts[2]);
        return new RequestHead(method, target, version, ParseHeaders(lines));
    }

    public static ResponseHead ParseResponse(ReadOnlySpan<byte> head)
    {
        var lines = SplitLines(head);
        var line = lines[0];

        // status-line = HTTP-version SP status-code SP [ reason-phrase ]
        var firstSpace = line.IndexOf(' ', StringComparison.Ordinal);
        if (firstSpace < 0)
        {
            throw new HttpParseException($"Invalid status line: '{Truncate(line)}'.");
        }

        var version = ParseVersion(line[..firstSpace]);
        var rest = line[(firstSpace + 1)..];
        var secondSpace = rest.IndexOf(' ', StringComparison.Ordinal);
        var codeText = secondSpace < 0 ? rest : rest[..secondSpace];
        var reason = secondSpace < 0 ? string.Empty : rest[(secondSpace + 1)..];

        if (codeText.Length != 3
            || !int.TryParse(codeText, NumberStyles.None, CultureInfo.InvariantCulture, out var status)
            || status < 100)
        {
            throw new HttpParseException($"Invalid status code: '{Truncate(codeText)}'.");
        }

        return new ResponseHead(version, status, reason, ParseHeaders(lines));
    }

    /// <summary>True when <paramref name="value"/> is a non-empty RFC 9110 token.</summary>
    public static bool IsToken(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!IsTokenChar(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTokenChar(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

    private static string ParseVersion(string text) =>
        text switch
        {
            "HTTP/1.1" => "HTTP/1.1",
            "HTTP/1.0" => "HTTP/1.0",
            _ when text.StartsWith("HTTP/", StringComparison.Ordinal) =>
                throw new HttpParseException($"Unsupported HTTP version '{Truncate(text)}'.", 505),
            _ => throw new HttpParseException($"Invalid HTTP version '{Truncate(text)}'."),
        };

    private static List<string> SplitLines(ReadOnlySpan<byte> head)
    {
        var text = HeaderEncoding.GetString(head);
        var lines = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline;
            var lineEnd = end > start && text[end - 1] == '\r' ? end - 1 : end;
            var line = text[start..lineEnd];
            if (line.Length == 0)
            {
                // Blank line: end of the head. Leading blank lines were already skipped by the reader.
                if (lines.Count > 0)
                {
                    break;
                }
            }
            else
            {
                if (line.Contains('\r', StringComparison.Ordinal) || line.Contains('\0', StringComparison.Ordinal))
                {
                    throw new HttpParseException("Bare CR or NUL in message head.");
                }

                lines.Add(line);
            }

            if (newline < 0)
            {
                break;
            }

            start = newline + 1;
        }

        if (lines.Count == 0)
        {
            throw new HttpParseException("Empty message head.");
        }

        return lines;
    }

    private static HttpHeaderList ParseHeaders(List<string> lines)
    {
        var headers = new HttpHeaderList();
        for (var i = 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line[0] is ' ' or '\t')
            {
                throw new HttpParseException("Obsolete header line folding is not allowed.");
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                throw new HttpParseException($"Invalid header line: '{Truncate(line)}'.");
            }

            var name = line[..colon];
            if (!IsToken(name))
            {
                // Includes whitespace between the name and the colon, which RFC 9112 forbids.
                throw new HttpParseException($"Invalid header name: '{Truncate(name)}'.");
            }

            var value = line[(colon + 1)..].Trim(' ', '\t');
            headers.Add(name, value);
        }

        return headers;
    }

    private static string Truncate(string text) => text.Length <= 80 ? text : text[..80] + "...";
}
