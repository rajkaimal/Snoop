using Snoop.Core.Bodies;
using Snoop.Core.Formatting;
using Snoop.Core.Http;
using Snoop.Inspection.Formatting;

namespace Snoop.Inspection.Bodies;

/// <summary>How the user wants to see a body.</summary>
public enum BodyViewMode
{
    /// <summary>Pick the best view for the content.</summary>
    Auto,
    Text,
    Json,
    Xml,
    Hex,
    Image,
}

/// <summary>A body ready to show.</summary>
/// <param name="Mode">The view actually used (never <see cref="BodyViewMode.Auto"/>).</param>
/// <param name="Text">Text to show, for every mode except Image.</param>
/// <param name="ImageBytes">Image data, for Image mode.</param>
/// <param name="Kind">What the body contains.</param>
/// <param name="Summary">One line about the body, e.g. "86.96 KB · application/json · gzip decoded".</param>
public sealed record RenderedBody(BodyViewMode Mode, string Text, byte[]? ImageBytes, ContentKind Kind, string Summary)
{
    public static RenderedBody None { get; } = new(BodyViewMode.Text, string.Empty, null, ContentKind.Empty, "No body");
}

/// <summary>Decodes and formats a captured body for display.</summary>
public static class BodyRenderer
{
    /// <summary>Text views stop after this many characters to keep the UI responsive.</summary>
    public const int MaxTextChars = 2 * 1024 * 1024;

    public static RenderedBody Render(CapturedBody? body, HttpHeaderList headers, BodyViewMode mode = BodyViewMode.Auto)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (body is null || body.TotalBytes == 0)
        {
            return RenderedBody.None;
        }

        var raw = body.ToArray();
        var decoded = ContentDecoder.Decode(raw, headers);
        var bytes = decoded.Bytes;
        var contentType = headers.Get("Content-Type");
        var kind = ContentSniffer.Detect(contentType, bytes);
        var effective = mode == BodyViewMode.Auto ? BestMode(kind) : mode;
        var summary = BuildSummary(body, decoded, contentType, bytes.Length);

        if (effective == BodyViewMode.Image)
        {
            return ContentSniffer.IsImage(bytes)
                ? new RenderedBody(BodyViewMode.Image, string.Empty, bytes, kind, summary)
                : new RenderedBody(BodyViewMode.Hex, HexDump.Format(bytes), null, kind, summary + " · not a displayable image");
        }

        if (effective == BodyViewMode.Hex)
        {
            return new RenderedBody(BodyViewMode.Hex, HexDump.Format(bytes), null, kind, summary);
        }

        var text = TextDecoder.Decode(bytes, ContentSniffer.Charset(contentType));
        switch (effective)
        {
            case BodyViewMode.Json when JsonFormatter.TryFormat(text) is { } json:
                return new RenderedBody(BodyViewMode.Json, Cap(json), null, kind, summary);
            case BodyViewMode.Json:
                summary += " · not valid JSON, shown as text";
                break;
            case BodyViewMode.Xml when XmlFormatter.TryFormat(text) is { } xml:
                return new RenderedBody(BodyViewMode.Xml, Cap(xml), null, kind, summary);
            case BodyViewMode.Xml:
                summary += " · not well-formed XML, shown as text";
                break;
        }

        if (mode == BodyViewMode.Auto && kind is ContentKind.Binary or ContentKind.Font)
        {
            return new RenderedBody(BodyViewMode.Hex, HexDump.Format(bytes), null, kind, summary);
        }

        return new RenderedBody(BodyViewMode.Text, Cap(text), null, kind, summary);
    }

    /// <summary>The view that suits each kind of content.</summary>
    public static BodyViewMode BestMode(ContentKind kind) =>
        kind switch
        {
            ContentKind.Json => BodyViewMode.Json,
            ContentKind.Xml => BodyViewMode.Xml,
            ContentKind.Image => BodyViewMode.Image,
            ContentKind.Binary or ContentKind.Font => BodyViewMode.Hex,
            _ => BodyViewMode.Text,
        };

    private static string BuildSummary(CapturedBody body, DecodedContent decoded, string? contentType, int decodedLength)
    {
        var parts = new List<string> { ByteSize.Format(body.TotalBytes) };
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            parts.Add(ContentSniffer.MediaType(contentType));
        }

        if (decoded.WasDecoded)
        {
            parts.Add($"{string.Join("+", decoded.AppliedEncodings)} decoded to {ByteSize.Format(decodedLength)}");
        }

        if (decoded.Error is not null)
        {
            parts.Add(decoded.Error);
        }

        if (body.IsTruncated)
        {
            parts.Add($"only the first {ByteSize.Format(body.StoredBytes)} were recorded");
        }

        return string.Join(" · ", parts);
    }

    private static string Cap(string text) =>
        text.Length <= MaxTextChars ? text : text[..MaxTextChars] + $"\n\n... {text.Length - MaxTextChars:N0} more characters not shown";
}
