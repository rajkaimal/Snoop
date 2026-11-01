using Snoop.Core.Http;

namespace Snoop.Proxy.Http;

/// <summary>Edits the Accept-Encoding request header.</summary>
public static class AcceptEncoding
{
    /// <summary>Removes the zstd coding. Returns true when the header changed.</summary>
    public static bool RemoveZstd(HttpHeaderList headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var values = headers.GetAll("Accept-Encoding");
        if (values.Count == 0 || !values.Any(v => v.Contains("zstd", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var kept = values
            .SelectMany(v => v.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Where(item => !string.Equals(item.Split(';', 2)[0].Trim(), "zstd", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (kept.Count == 0)
        {
            headers.Remove("Accept-Encoding");
        }
        else
        {
            headers.Set("Accept-Encoding", string.Join(", ", kept));
        }

        return true;
    }
}
