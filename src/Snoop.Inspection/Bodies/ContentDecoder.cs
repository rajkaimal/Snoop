using System.IO.Compression;
using Snoop.Core.Http;

namespace Snoop.Inspection.Bodies;

/// <summary>The result of undoing a message's Content-Encoding.</summary>
/// <param name="Bytes">The decoded bytes (or the original bytes when nothing could be decoded).</param>
/// <param name="AppliedEncodings">Encodings that were undone, in the order they were undone.</param>
/// <param name="Error">Why decoding stopped early, if it did.</param>
public sealed record DecodedContent(byte[] Bytes, IReadOnlyList<string> AppliedEncodings, string? Error)
{
    public bool WasDecoded => AppliedEncodings.Count > 0;
}

/// <summary>
/// Undoes Content-Encoding (gzip, deflate, br) so bodies can be read. Encodings are undone
/// last-applied first. Output is capped so a "decompression bomb" cannot exhaust memory.
/// </summary>
public static class ContentDecoder
{
    public const int DefaultMaxDecodedBytes = 64 * 1024 * 1024;

    public static DecodedContent Decode(byte[] body, HttpHeaderList headers, int maxDecodedBytes = DefaultMaxDecodedBytes)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(headers);

        var encodings = headers.GetListItems("Content-Encoding")
            .Where(e => !string.Equals(e, "identity", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (encodings.Count == 0 || body.Length == 0)
        {
            return new DecodedContent(body, [], null);
        }

        var current = body;
        var applied = new List<string>();
        for (var i = encodings.Count - 1; i >= 0; i--)
        {
            var encoding = encodings[i].ToLowerInvariant();
            try
            {
                var next = encoding switch
                {
                    "gzip" or "x-gzip" => Inflate(current, s => new GZipStream(s, CompressionMode.Decompress), maxDecodedBytes),
                    "br" => Inflate(current, s => new BrotliStream(s, CompressionMode.Decompress), maxDecodedBytes),
                    "deflate" => InflateDeflate(current, maxDecodedBytes),
                    _ => null,
                };

                if (next is null)
                {
                    return new DecodedContent(current, applied, $"The '{encodings[i]}' encoding is not supported yet; showing the encoded bytes.");
                }

                current = next;
                applied.Add(encoding);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or DecodedTooLargeException)
            {
                return new DecodedContent(current, applied, $"Could not undo '{encodings[i]}': {ex.Message}");
            }
        }

        return new DecodedContent(current, applied, null);
    }

    /// <summary>HTTP "deflate" is meant to be zlib-wrapped, but some servers send raw deflate. Try both.</summary>
    private static byte[] InflateDeflate(byte[] data, int max)
    {
        try
        {
            return Inflate(data, s => new ZLibStream(s, CompressionMode.Decompress), max);
        }
        catch (InvalidDataException)
        {
            return Inflate(data, s => new DeflateStream(s, CompressionMode.Decompress), max);
        }
    }

    private static byte[] Inflate(byte[] data, Func<Stream, Stream> wrap, int max)
    {
        using var input = new MemoryStream(data, writable: false);
        using var decoder = wrap(input);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = decoder.Read(buffer)) > 0)
        {
            if (output.Length + read > max)
            {
                throw new DecodedTooLargeException($"Decoded body is larger than {max / (1024 * 1024)} MB.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private sealed class DecodedTooLargeException(string message) : Exception(message);
}
