using System.Text;

namespace Snoop.Inspection.Bodies;

/// <summary>What a body contains, for choosing a viewer and an icon.</summary>
public enum ContentKind
{
    Empty,
    Json,
    Xml,
    Html,
    JavaScript,
    Css,
    Text,
    Form,
    Image,
    Font,
    Binary,
}

/// <summary>Works out a body's <see cref="ContentKind"/> from its Content-Type, falling back to sniffing the bytes.</summary>
public static class ContentSniffer
{
    public static ContentKind FromContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return ContentKind.Binary;
        }

        var mediaType = MediaType(contentType);
        return mediaType switch
        {
            "application/json" or "text/json" or "application/problem+json" or "application/manifest+json" => ContentKind.Json,
            _ when mediaType.EndsWith("+json", StringComparison.Ordinal) => ContentKind.Json,
            "text/html" or "application/xhtml+xml" => ContentKind.Html,
            "application/xml" or "text/xml" or "image/svg+xml" => ContentKind.Xml,
            _ when mediaType.EndsWith("+xml", StringComparison.Ordinal) => ContentKind.Xml,
            "application/javascript" or "text/javascript" or "application/x-javascript" or "application/ecmascript" => ContentKind.JavaScript,
            "text/css" => ContentKind.Css,
            "application/x-www-form-urlencoded" => ContentKind.Form,
            _ when mediaType.StartsWith("image/", StringComparison.Ordinal) => ContentKind.Image,
            _ when mediaType.StartsWith("font/", StringComparison.Ordinal) || mediaType.Contains("font", StringComparison.Ordinal) => ContentKind.Font,
            _ when mediaType.StartsWith("text/", StringComparison.Ordinal) => ContentKind.Text,
            _ => ContentKind.Binary,
        };
    }

    /// <summary>Uses the Content-Type when it is specific; otherwise looks at the bytes.</summary>
    public static ContentKind Detect(string? contentType, ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty)
        {
            return ContentKind.Empty;
        }

        var declared = FromContentType(contentType);
        if (declared != ContentKind.Binary)
        {
            return declared;
        }

        if (IsImage(body))
        {
            return ContentKind.Image;
        }

        var start = SkipWhitespaceAndBom(body);
        if (start < body.Length)
        {
            var first = body[start];
            if ((first == '{' || first == '[') && LooksLikeText(body))
            {
                return ContentKind.Json;
            }

            if (first == '<' && LooksLikeText(body))
            {
                var head = Encoding.UTF8.GetString(body[start..Math.Min(body.Length, start + 100)]).ToLowerInvariant();
                return head.StartsWith("<!doctype html", StringComparison.Ordinal) || head.StartsWith("<html", StringComparison.Ordinal)
                    ? ContentKind.Html
                    : ContentKind.Xml;
            }
        }

        return LooksLikeText(body) ? ContentKind.Text : ContentKind.Binary;
    }

    /// <summary>The lower-case media type without parameters, e.g. "application/json".</summary>
    public static string MediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return string.Empty;
        }

        var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        return (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim().ToLowerInvariant();
    }

    /// <summary>The charset parameter of a Content-Type, or null.</summary>
    public static string? Charset(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        foreach (var part in contentType.Split(';').Skip(1))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length == 2 && string.Equals(pair[0], "charset", StringComparison.OrdinalIgnoreCase))
            {
                return pair[1].Trim('"');
            }
        }

        return null;
    }

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Gif = [0x47, 0x49, 0x46, 0x38];
    private static readonly byte[] Riff = [0x52, 0x49, 0x46, 0x46];
    private static readonly byte[] Webp = [0x57, 0x45, 0x42, 0x50];
    private static readonly byte[] Icon = [0x00, 0x00, 0x01, 0x00];
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>True for PNG, JPEG, GIF, WebP and ICO data.</summary>
    public static bool IsImage(ReadOnlySpan<byte> body) =>
        body.StartsWith(Png)
        || body.StartsWith(Jpeg)
        || body.StartsWith(Gif)
        || (body.Length > 12 && body.StartsWith(Riff) && body[8..12].SequenceEqual(Webp))
        || body.StartsWith(Icon);

    /// <summary>True when the first few KB contain no control characters other than whitespace.</summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> body)
    {
        var sample = body[..Math.Min(body.Length, 4096)];
        foreach (var b in sample)
        {
            if (b < 0x20 && b is not (byte)'\t' and not (byte)'\n' and not (byte)'\r' and not 0x0C)
            {
                return false;
            }
        }

        return true;
    }

    private static int SkipWhitespaceAndBom(ReadOnlySpan<byte> body)
    {
        var i = body.StartsWith(Utf8Bom) ? 3 : 0;
        while (i < body.Length && body[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            i++;
        }

        return i;
    }
}
