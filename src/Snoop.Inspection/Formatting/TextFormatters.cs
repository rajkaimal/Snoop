using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Snoop.Inspection.Formatting;

/// <summary>Pretty-prints JSON. Returns null when the text is not valid JSON.</summary>
public static class JsonFormatter
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // Show text as sent, not \u-escaped.
    };

    public static string? TryFormat(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 256 });
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                document.WriteTo(writer);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Pretty-prints XML. Returns null when the text is not well-formed XML.</summary>
public static class XmlFormatter
{
    public static string? TryFormat(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            var declaration = document.Declaration is null ? string.Empty : document.Declaration + Environment.NewLine;
            return declaration + document.ToString(SaveOptions.None);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}

/// <summary>Classic hex dump: offset, 16 bytes in hex, then the printable characters.</summary>
public static class HexDump
{
    public static string Format(ReadOnlySpan<byte> data, int maxBytes = 256 * 1024)
    {
        var length = Math.Min(data.Length, maxBytes);
        var builder = new StringBuilder((length / 16 + 1) * 78);
        for (var offset = 0; offset < length; offset += 16)
        {
            var line = data.Slice(offset, Math.Min(16, length - offset));
            builder.Append(offset.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
            for (var i = 0; i < 16; i++)
            {
                if (i < line.Length)
                {
                    builder.Append(line[i].ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
                }
                else
                {
                    builder.Append("   ");
                }

                if (i == 7)
                {
                    builder.Append(' ');
                }
            }

            builder.Append(' ');
            foreach (var b in line)
            {
                builder.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            builder.Append('\n');
        }

        if (data.Length > length)
        {
            builder.Append(CultureInfo.InvariantCulture, $"... {data.Length - length:N0} more bytes not shown\n");
        }

        return builder.ToString();
    }
}

/// <summary>Decodes body bytes to text using the declared charset (UTF-8 when none or unknown).</summary>
public static class TextDecoder
{
    static TextDecoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string Decode(ReadOnlySpan<byte> data, string? charset)
    {
        var encoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset);
            }
            catch (ArgumentException)
            {
                // Unknown charset: fall back to UTF-8.
            }
        }

        var text = encoding.GetString(data);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }
}
