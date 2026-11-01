using System.IO.Compression;
using Snoop.Core.Http;

namespace Snoop.Proxy.WebSockets;

/// <summary>
/// Undoes permessage-deflate (RFC 7692) for one direction. With context takeover the messages form
/// one continuous deflate stream, so the whole stream so far is inflated again and the new output
/// taken; that is simple and exact, and stops after <see cref="MaxHistoryBytes"/> of compressed data.
/// </summary>
internal sealed class PerMessageInflater(bool noContextTakeover)
{
    /// <summary>Compressed bytes kept for decoding later messages with context takeover.</summary>
    internal const int MaxHistoryBytes = 4 * 1024 * 1024;

    /// <summary>Decoded bytes allowed for the whole stream so far.</summary>
    internal const int MaxOutputBytes = 32 * 1024 * 1024;

    private static readonly byte[] Trailer = [0x00, 0x00, 0xFF, 0xFF];

    private readonly MemoryStream _history = new();
    private long _outputSoFar;
    private bool _disabled;

    /// <summary>Reads the negotiated extension from the server's handshake response. Null when it is not on.</summary>
    /// <returns>(app to server, server to app) inflaters.</returns>
    public static (PerMessageInflater? FromClient, PerMessageInflater? FromServer) FromHandshake(HttpHeaderList responseHeaders)
    {
        ArgumentNullException.ThrowIfNull(responseHeaders);
        foreach (var extension in responseHeaders.GetListItems("Sec-WebSocket-Extensions"))
        {
            var parts = extension.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !string.Equals(parts[0], "permessage-deflate", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var clientNoTakeover = parts.Any(p => p.StartsWith("client_no_context_takeover", StringComparison.OrdinalIgnoreCase));
            var serverNoTakeover = parts.Any(p => p.StartsWith("server_no_context_takeover", StringComparison.OrdinalIgnoreCase));
            return (new PerMessageInflater(clientNoTakeover), new PerMessageInflater(serverNoTakeover));
        }

        return (null, null);
    }

    /// <summary>Stops decoding (a message could not be kept whole, so later ones cannot be decoded).</summary>
    public void Disable() => _disabled = true;

    public bool TryInflate(byte[] compressed, out byte[] inflated, out string? error)
    {
        inflated = compressed;
        if (_disabled)
        {
            error = "Shown compressed: an earlier message could not be decoded.";
            return false;
        }

        if (noContextTakeover)
        {
            return Inflate([.. compressed, .. Trailer], 0, out inflated, out error);
        }

        if (_history.Length + compressed.Length + Trailer.Length > MaxHistoryBytes)
        {
            _disabled = true;
            error = "Shown compressed: too much compressed data on this connection to keep decoding.";
            return false;
        }

        _history.Write(compressed);
        _history.Write(Trailer);
        if (!Inflate(_history.ToArray(), _outputSoFar, out inflated, out error))
        {
            _disabled = true;
            return false;
        }

        _outputSoFar += inflated.Length;
        return true;
    }

    private static bool Inflate(byte[] stream, long skip, out byte[] output, out string? error)
    {
        try
        {
            using var inflater = new DeflateStream(new MemoryStream(stream, writable: false), CompressionMode.Decompress);
            using var all = new MemoryStream();
            inflater.CopyTo(all);
            if (all.Length > MaxOutputBytes)
            {
                output = [];
                error = "Shown compressed: the decoded data on this connection grew too large to keep decoding.";
                return false;
            }

            var bytes = all.ToArray();
            output = bytes.AsSpan((int)Math.Min(skip, bytes.Length)).ToArray();
            error = null;
            return true;
        }
        catch (InvalidDataException ex)
        {
            output = [];
            error = $"Shown compressed: it could not be decoded ({ex.Message}).";
            return false;
        }
    }
}
