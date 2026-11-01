namespace Snoop.Inspection.Formatting;

/// <summary>What a piece of highlighted text is.</summary>
public enum SyntaxKind
{
    Plain,
    Punctuation,

    // JSON
    Key,
    String,
    Number,
    Literal,

    // XML and HTML
    Tag,
    AttributeName,
    AttributeValue,
    Comment,
}

/// <summary>A run of <see cref="SyntaxHighlighter"/> output.</summary>
public readonly record struct SyntaxSpan(int Start, int Length, SyntaxKind Kind);

/// <summary>Which highlighter a body uses.</summary>
public enum SyntaxLanguage
{
    None,
    Json,
    Markup,
}

/// <summary>
/// Splits JSON or XML/HTML text into coloured runs for the body view. The runs cover the whole text,
/// in order, with neighbours of the same kind merged. It never fails: text that is not valid (cut off,
/// or not really JSON) is still covered, just with less colour.
/// </summary>
public static class SyntaxHighlighter
{
    /// <summary>Longer text is not highlighted (one UI element per run would make the view slow).</summary>
    public const int MaxChars = 100_000;

    public static IReadOnlyList<SyntaxSpan> Highlight(string text, SyntaxLanguage language)
    {
        ArgumentNullException.ThrowIfNull(text);
        return language switch
        {
            SyntaxLanguage.Json => Json(text),
            SyntaxLanguage.Markup => Markup(text),
            _ => text.Length == 0 ? [] : [new SyntaxSpan(0, text.Length, SyntaxKind.Plain)],
        };
    }

    public static IReadOnlyList<SyntaxSpan> Json(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var spans = new SpanBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var start = i;
            if (c == '"')
            {
                i = SkipString(text, i, '"');
                var next = i;
                while (next < text.Length && char.IsWhiteSpace(text[next]))
                {
                    next++;
                }

                spans.Add(start, i - start, next < text.Length && text[next] == ':' ? SyntaxKind.Key : SyntaxKind.String);
            }
            else if (c == '-' || char.IsAsciiDigit(c))
            {
                i++;
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] is '.' or 'e' or 'E' or '+' or '-'))
                {
                    i++;
                }

                spans.Add(start, i - start, SyntaxKind.Number);
            }
            else if (char.IsAsciiLetter(c))
            {
                while (i < text.Length && char.IsAsciiLetter(text[i]))
                {
                    i++;
                }

                var word = text.AsSpan(start, i - start);
                spans.Add(start, i - start, word is "true" or "false" or "null" ? SyntaxKind.Literal : SyntaxKind.Plain);
            }
            else if (c is '{' or '}' or '[' or ']' or ':' or ',')
            {
                spans.Add(i++, 1, SyntaxKind.Punctuation);
            }
            else
            {
                spans.Add(i++, 1, SyntaxKind.Plain);
            }
        }

        return spans.ToList();
    }

    public static IReadOnlyList<SyntaxSpan> Markup(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var spans = new SpanBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '<')
            {
                var next = text.IndexOf('<', i);
                next = next < 0 ? text.Length : next;
                spans.Add(i, next - i, SyntaxKind.Plain);
                i = next;
                continue;
            }

            if (Starts(text, i, "<!--"))
            {
                var end = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 3;
                spans.Add(i, end - i, SyntaxKind.Comment);
                i = end;
                continue;
            }

            if (Starts(text, i, "<![CDATA["))
            {
                var end = text.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 3;
                spans.Add(i, 9, SyntaxKind.Punctuation);
                var contentEnd = Math.Max(i + 9, end - 3);
                spans.Add(i + 9, contentEnd - i - 9, SyntaxKind.String);
                spans.Add(contentEnd, end - contentEnd, SyntaxKind.Punctuation);
                i = end;
                continue;
            }

            i = Tag(text, i, spans);
        }

        return spans.ToList();
    }

    /// <summary>A tag from '&lt;' to '&gt;': "&lt;", "&lt;/", "&lt;?" or "&lt;!", the name, attributes, then "&gt;" or "/&gt;".</summary>
    private static int Tag(string text, int i, SpanBuilder spans)
    {
        var open = 1;
        if (i + 1 < text.Length && text[i + 1] is '/' or '?' or '!')
        {
            open = 2;
        }

        spans.Add(i, open, SyntaxKind.Punctuation);
        i += open;

        var nameStart = i;
        while (i < text.Length && IsNameChar(text[i]))
        {
            i++;
        }

        spans.Add(nameStart, i - nameStart, SyntaxKind.Tag);

        while (i < text.Length)
        {
            var c = text[i];
            if (c == '>')
            {
                spans.Add(i++, 1, SyntaxKind.Punctuation);
                return i;
            }

            if ((c is '/' or '?') && i + 1 < text.Length && text[i + 1] == '>')
            {
                spans.Add(i, 2, SyntaxKind.Punctuation);
                return i + 2;
            }

            if (c == '<')
            {
                return i; // a broken tag; let the caller start again here
            }

            if (c is '"' or '\'')
            {
                var start = i;
                i = SkipString(text, i, c);
                spans.Add(start, i - start, SyntaxKind.AttributeValue);
            }
            else if (c == '=')
            {
                spans.Add(i++, 1, SyntaxKind.Punctuation);
            }
            else if (IsNameChar(c))
            {
                var start = i;
                while (i < text.Length && IsNameChar(text[i]))
                {
                    i++;
                }

                spans.Add(start, i - start, SyntaxKind.AttributeName);
            }
            else
            {
                spans.Add(i++, 1, SyntaxKind.Plain);
            }
        }

        return i;
    }

    /// <summary>Index just past a quoted string starting at <paramref name="start"/> (backslash escapes count for JSON).</summary>
    private static int SkipString(string text, int start, char quote)
    {
        var i = start + 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\' && quote == '"' && i + 1 < text.Length)
            {
                i += 2;
                continue;
            }

            i++;
            if (c == quote)
            {
                break;
            }
        }

        return Math.Min(i, text.Length);
    }

    private static bool Starts(string text, int index, string value) =>
        string.CompareOrdinal(text, index, value, 0, value.Length) == 0;

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is ':' or '-' or '_' or '.';

    /// <summary>Collects spans, merging neighbours of the same kind and skipping empty ones.</summary>
    private sealed class SpanBuilder
    {
        private readonly List<SyntaxSpan> _spans = [];

        public void Add(int start, int length, SyntaxKind kind)
        {
            if (length <= 0)
            {
                return;
            }

            if (_spans.Count > 0 && _spans[^1] is var last && last.Kind == kind && last.Start + last.Length == start)
            {
                _spans[^1] = last with { Length = last.Length + length };
                return;
            }

            _spans.Add(new SyntaxSpan(start, length, kind));
        }

        public List<SyntaxSpan> ToList() => _spans;
    }
}
