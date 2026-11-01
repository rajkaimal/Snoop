using System.Net;
using System.Text;
using Snoop.Core.Http;
using Snoop.Proxy.Http;

namespace Snoop.Proxy.Engine;

/// <summary>
/// Pages served when a browser talks to the proxy directly (http://127.0.0.1:8866/):
/// a status page and the root certificate download used to set up other devices.
/// </summary>
internal sealed class LocalPages(ProxyContext context)
{
    public const string CertificatePath = "/snoop-root.cer";

    /// <summary>Answers the request. Returns whether the connection may be reused.</summary>
    public async Task<bool> HandleAsync(Stream client, RequestHead head, bool hasBody, CancellationToken cancellationToken)
    {
        var keepAlive = head.WantsKeepAlive && !hasBody;
        var path = GetPath(head.Target);
        var isGet = head.Method is "GET" or "HEAD";

        if (isGet && path is "/" or "/index.html")
        {
            await ClientResponses.WriteAsync(client, 200, "text/html; charset=utf-8", BuildIndexPage(), keepAlive, cancellationToken).ConfigureAwait(false);
            return keepAlive;
        }

        if (isGet && string.Equals(path, CertificatePath, StringComparison.OrdinalIgnoreCase) && context.CertificateAuthority is { } ca)
        {
            await ClientResponses.WriteAsync(
                client,
                200,
                "application/x-x509-ca-cert",
                ca.ExportRootPublicCertificate(),
                keepAlive,
                cancellationToken,
                [new HttpHeader("Content-Disposition", "attachment; filename=\"snoop-root.cer\"")]).ConfigureAwait(false);
            return keepAlive;
        }

        await ClientResponses.WriteErrorAsync(client, 404, $"Nothing here: {path}", cancellationToken).ConfigureAwait(false);
        return false;
    }

    private static string GetPath(string target)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out var absolute))
        {
            return absolute.AbsolutePath;
        }

        var query = target.IndexOf('?', StringComparison.Ordinal);
        return query >= 0 ? target[..query] : target;
    }

    private byte[] BuildIndexPage()
    {
        var endpoint = context.ListenEndpoint;
        var decrypt = context.CanDecrypt ? "on" : "off";
        var certificateLink = context.CertificateAuthority is null
            ? "<p>No root certificate is loaded.</p>"
            : $"<p><a href=\"{CertificatePath}\">Download the Snoop root certificate</a> to trust it on another device.</p>";
        var html = $$"""
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8"><title>Snoop</title>
            <style>body{font-family:Segoe UI,system-ui,sans-serif;max-width:40rem;margin:3rem auto;padding:0 1rem;color:#222}code{background:#f2f2f2;padding:0 .3rem}</style>
            </head>
            <body>
            <h1>Snoop is running</h1>
            <p>Proxy address: <code>{{WebUtility.HtmlEncode(endpoint.ToString())}}</code>. HTTPS decryption is {{decrypt}}.</p>
            {{certificateLink}}
            </body>
            </html>
            """;
        return Encoding.UTF8.GetBytes(html);
    }
}
