using System.Globalization;
using System.Text;
using Snoop.Core.Http;

namespace Snoop.Proxy.Http;

/// <summary>Serialises HTTP/1.x message heads.</summary>
public static class HttpHeadWriter
{
    public static byte[] SerializeRequest(string method, string target, string version, HttpHeaderList headers)
    {
        var builder = new StringBuilder();
        builder.Append(method).Append(' ').Append(target).Append(' ').Append(version).Append("\r\n");
        AppendHeaders(builder, headers);
        return Encoding.Latin1.GetBytes(builder.ToString());
    }

    public static byte[] SerializeResponse(string version, int statusCode, string reasonPhrase, HttpHeaderList headers)
    {
        var builder = new StringBuilder();
        builder.Append(version).Append(' ')
            .Append(statusCode.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(Sanitize(reasonPhrase)).Append("\r\n");
        AppendHeaders(builder, headers);
        return Encoding.Latin1.GetBytes(builder.ToString());
    }

    private static void AppendHeaders(StringBuilder builder, HttpHeaderList headers)
    {
        foreach (var header in headers)
        {
            builder.Append(header.Name).Append(": ").Append(Sanitize(header.Value)).Append("\r\n");
        }

        builder.Append("\r\n");
    }

    /// <summary>Removes CR and LF so a value can never inject extra header lines.</summary>
    private static string Sanitize(string value) =>
        value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal)
            ? value.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)
            : value;
}
