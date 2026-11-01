using System.Text;
using Snoop.Core.Http;
using Snoop.Core.Sessions;
using Snoop.Inspection.Bodies;
using Snoop.Inspection.Formatting;

namespace Snoop.Inspection.Export;

/// <summary>Turns a captured request into a command that sends it again.</summary>
public static class CommandExporter
{
    /// <summary>Headers the tools set themselves; copying them causes errors or duplicates.</summary>
    private static readonly HashSet<string> SkippedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host",
        "Content-Length",
        "Connection",
        "Proxy-Connection",
        "Keep-Alive",
        "Transfer-Encoding",
        "Expect",
    };

    /// <summary>A cURL command for bash/zsh (single-quoted, so nothing is expanded).</summary>
    public static string ToCurl(RequestInfo request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var builder = new StringBuilder("curl");
        if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
        {
            builder.Append(" -X ").Append(request.Method);
        }

        builder.Append(' ').Append(QuoteBash(request.Url.AbsoluteUri));
        foreach (var header in ForwardableHeaders(request.Headers))
        {
            builder.Append(" \\\n  -H ").Append(QuoteBash($"{header.Name}: {header.Value}"));
        }

        if (request.Headers.Contains("Accept-Encoding"))
        {
            builder.Append(" \\\n  --compressed");
        }

        if (BodyText(request) is { } body)
        {
            builder.Append(" \\\n  --data-raw ").Append(QuoteBash(body));
        }

        return builder.ToString();
    }

    /// <summary>A PowerShell Invoke-WebRequest command.</summary>
    public static string ToPowerShell(RequestInfo request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var builder = new StringBuilder("$headers = @{\n");
        string? contentType = null;
        foreach (var header in ForwardableHeaders(request.Headers))
        {
            if (string.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = header.Value;
                continue;
            }

            builder.Append("  ").Append(QuotePowerShell(header.Name)).Append(" = ").Append(QuotePowerShell(header.Value)).Append('\n');
        }

        builder.Append("}\n");
        builder.Append("Invoke-WebRequest -UseBasicParsing -Uri ").Append(QuotePowerShell(request.Url.AbsoluteUri))
            .Append(" -Method ").Append(request.Method)
            .Append(" -Headers $headers");
        if (contentType is not null)
        {
            builder.Append(" -ContentType ").Append(QuotePowerShell(contentType));
        }

        if (BodyText(request) is { } body)
        {
            builder.Append(" -Body ").Append(QuotePowerShell(body));
        }

        return builder.ToString();
    }

    internal static string QuoteBash(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    internal static string QuotePowerShell(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static IEnumerable<HttpHeader> ForwardableHeaders(HttpHeaderList headers) =>
        headers.Where(h => !SkippedHeaders.Contains(h.Name) && !h.Name.StartsWith(':'));

    private static string? BodyText(RequestInfo request)
    {
        if (request.Body is not { TotalBytes: > 0 } body)
        {
            return null;
        }

        var bytes = body.ToArray();
        return ContentSniffer.LooksLikeText(bytes)
            ? TextDecoder.Decode(bytes, ContentSniffer.Charset(request.Headers.Get("Content-Type")))
            : null;
    }
}
