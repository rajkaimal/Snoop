using Snoop.Inspection.Formatting;

namespace Snoop.Inspection.Tests.Formatting;

public sealed class SyntaxHighlighterTests
{
    private static List<(string Text, SyntaxKind Kind)> Pieces(string text, IReadOnlyList<SyntaxSpan> spans) =>
        spans.Select(s => (text.Substring(s.Start, s.Length), s.Kind)).Where(p => p.Item1.Trim().Length > 0).ToList();

    private static void CoversEverything(string text, IReadOnlyList<SyntaxSpan> spans)
    {
        var position = 0;
        foreach (var span in spans)
        {
            Assert.Equal(position, span.Start);
            Assert.True(span.Length > 0);
            position += span.Length;
        }

        Assert.Equal(text.Length, position);
    }

    [Fact]
    public void Json_keys_strings_numbers_and_literals()
    {
        const string text = "{\n  \"id\": -12.5e3,\n  \"name\": \"Ann \\\"A\\\"\",\n  \"ok\": true,\n  \"none\": null,\n  \"tags\": [\"x\"]\n}";
        var spans = SyntaxHighlighter.Json(text);

        CoversEverything(text, spans);
        var pieces = Pieces(text, spans);
        Assert.Contains(("\"id\"", SyntaxKind.Key), pieces);
        Assert.Contains(("-12.5e3", SyntaxKind.Number), pieces);
        Assert.Contains(("\"Ann \\\"A\\\"\"", SyntaxKind.String), pieces);
        Assert.Contains(("true", SyntaxKind.Literal), pieces);
        Assert.Contains(("null", SyntaxKind.Literal), pieces);
        Assert.Contains(("\"x\"", SyntaxKind.String), pieces);
        Assert.Equal(SyntaxKind.Punctuation, pieces[0].Kind);
    }

    [Fact]
    public void Cut_off_json_is_still_covered()
    {
        const string text = "{\"a\": \"unterminated";
        CoversEverything(text, SyntaxHighlighter.Json(text));
    }

    [Fact]
    public void Markup_tags_attributes_and_comments()
    {
        const string text = "<?xml version=\"1.0\"?>\n<!-- note -->\n<a href='/x' data-id=\"7\">Hi <b>there</b></a><br/><![CDATA[1 < 2]]>";
        var spans = SyntaxHighlighter.Markup(text);

        CoversEverything(text, spans);
        var pieces = Pieces(text, spans);
        Assert.Contains(("xml", SyntaxKind.Tag), pieces);
        Assert.Contains(("<!-- note -->", SyntaxKind.Comment), pieces);
        Assert.Contains(("a", SyntaxKind.Tag), pieces);
        Assert.Contains(("href", SyntaxKind.AttributeName), pieces);
        Assert.Contains(("'/x'", SyntaxKind.AttributeValue), pieces);
        Assert.Contains(("data-id", SyntaxKind.AttributeName), pieces);
        Assert.Contains(("Hi ", SyntaxKind.Plain), pieces);
        Assert.Contains(("1 < 2", SyntaxKind.String), pieces);
        Assert.Contains(("br", SyntaxKind.Tag), pieces);
        Assert.Contains(pieces, p => p.Kind == SyntaxKind.Punctuation && p.Text.Contains("/>", StringComparison.Ordinal));
    }

    [Fact]
    public void Broken_markup_is_still_covered()
    {
        const string text = "a < b and <div class=\"x";
        CoversEverything(text, SyntaxHighlighter.Markup(text));
    }

    [Fact]
    public void Plain_language_is_one_span()
    {
        Assert.Equal([new SyntaxSpan(0, 3, SyntaxKind.Plain)], SyntaxHighlighter.Highlight("abc", SyntaxLanguage.None));
        Assert.Empty(SyntaxHighlighter.Highlight(string.Empty, SyntaxLanguage.Json));
    }
}
