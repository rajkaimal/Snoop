using System.Globalization;
using System.Text;
using Snoop.Core.Http;
using Snoop.Proxy.Interception;

namespace Snoop.Proxy.Rules;

/// <summary>How the request-stage rules ended.</summary>
internal enum RuleResult
{
    /// <summary>Send the (possibly changed) request to the server.</summary>
    Continue,

    /// <summary>A rule answered; send <see cref="RuleOutcome.Response"/> to the app.</summary>
    Respond,

    /// <summary>A rule blocked the request; close the connection.</summary>
    Block,
}

/// <summary>The result of running request-stage rule actions.</summary>
/// <param name="Result">What to do next.</param>
/// <param name="Response">The answer, when <paramref name="Result"/> is Respond.</param>
/// <param name="DecidedBy">The rule that answered or blocked.</param>
/// <param name="AppliedRules">Names of rules whose actions changed or delayed the request.</param>
internal sealed record RuleOutcome(RuleResult Result, EditableResponse? Response, string? DecidedBy, IReadOnlyList<string> AppliedRules);

/// <summary>Runs rule actions against requests and responses.</summary>
internal static class RuleEngine
{
    /// <summary>Largest file a RespondWithFile action will send.</summary>
    internal const long MaxResponseFileBytes = 50L * 1024 * 1024;

    private static readonly Dictionary<string, string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".json"] = "application/json; charset=utf-8",
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
        [".xml"] = "application/xml; charset=utf-8",
        [".csv"] = "text/csv; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".pdf"] = "application/pdf",
        [".wasm"] = "application/wasm",
    };

    /// <summary>Runs the request-stage actions of <paramref name="rules"/> in order, changing <paramref name="request"/> in place.</summary>
    public static async Task<RuleOutcome> ApplyToRequestAsync(EditableRequest request, IReadOnlyList<TrafficRule> rules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rules);
        var applied = new List<string>();
        foreach (var rule in rules)
        {
            var used = false;
            foreach (var action in rule.Actions)
            {
                switch (action.Kind)
                {
                    case RuleActionKind.Delay:
                        if (int.TryParse(action.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) && ms > 0)
                        {
                            await Task.Delay(ms, cancellationToken).ConfigureAwait(false);
                            used = true;
                        }

                        break;

                    case RuleActionKind.RewriteUrl:
                        used |= RewriteUrl(request, action.Name, action.Value);
                        break;

                    case RuleActionKind.SetRequestHeader:
                        request.Headers.Set(action.Name, action.Value);
                        used = true;
                        break;

                    case RuleActionKind.RemoveRequestHeader:
                        used |= request.Headers.Remove(action.Name) > 0;
                        break;

                    case RuleActionKind.Respond:
                        return Finish(RuleResult.Respond, TextResponse(action), rule, applied);

                    case RuleActionKind.RespondWithFile:
                        return Finish(RuleResult.Respond, await FileResponseAsync(rule, action, cancellationToken).ConfigureAwait(false), rule, applied);

                    case RuleActionKind.Block:
                        return Finish(RuleResult.Block, null, rule, applied);

                    default:
                        break; // Response-stage actions and breakpoints are handled elsewhere.
                }
            }

            if (used)
            {
                applied.Add(rule.Name);
            }
        }

        return new RuleOutcome(RuleResult.Continue, null, null, applied);
    }

    /// <summary>Runs the response-header actions of <paramref name="rules"/>. Returns the names of rules that changed something.</summary>
    public static IReadOnlyList<string> ApplyToResponseHeaders(HttpHeaderList headers, IReadOnlyList<TrafficRule> rules)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rules);
        List<string>? applied = null;
        foreach (var rule in rules)
        {
            var used = false;
            foreach (var action in rule.Actions)
            {
                if (action.Kind == RuleActionKind.SetResponseHeader)
                {
                    headers.Set(action.Name, action.Value);
                    used = true;
                }
                else if (action.Kind == RuleActionKind.RemoveResponseHeader)
                {
                    used |= headers.Remove(action.Name) > 0;
                }
            }

            if (used)
            {
                (applied ??= []).Add(rule.Name);
            }
        }

        if (applied is null)
        {
            return [];
        }

        return applied;
    }

    /// <summary>A Content-Type for a body typed into a rule: JSON, HTML, XML or plain text.</summary>
    internal static string GuessTextMediaType(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            return "application/json; charset=utf-8";
        }

        if (trimmed.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            return "text/html; charset=utf-8";
        }

        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
        {
            return "application/xml; charset=utf-8";
        }

        return "text/plain; charset=utf-8";
    }

    internal static string MediaTypeForFile(string path) =>
        MediaTypes.TryGetValue(Path.GetExtension(path), out var type) ? type : "application/octet-stream";

    private static RuleOutcome Finish(RuleResult result, EditableResponse? response, TrafficRule rule, List<string> applied)
    {
        applied.Add(rule.Name);
        return new RuleOutcome(result, response, rule.Name, applied);
    }

    private static bool RewriteUrl(EditableRequest request, string find, string replacement)
    {
        if (string.IsNullOrEmpty(find))
        {
            return false;
        }

        var current = request.Url.AbsoluteUri;
        var rewritten = current.Replace(find, replacement ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        if (string.Equals(rewritten, current, StringComparison.Ordinal)
            || !Uri.TryCreate(rewritten, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(url.Host))
        {
            return false;
        }

        var hostChanged = !string.Equals(url.Authority, request.Url.Authority, StringComparison.OrdinalIgnoreCase);
        request.Url = url;
        if (hostChanged && request.Headers.Contains("Host"))
        {
            // The new server must see its own name, or it may refuse the request.
            request.Headers.Set("Host", url.IsDefaultPort ? url.IdnHost : $"{url.IdnHost}:{url.Port.ToString(CultureInfo.InvariantCulture)}");
        }

        return true;
    }

    private static EditableResponse TextResponse(RuleAction action)
    {
        var status = RuleSet.ParseStatus(action.Name) ?? 200;
        var body = Encoding.UTF8.GetBytes(action.Value ?? string.Empty);
        var headers = new HttpHeaderList();
        if (body.Length > 0)
        {
            headers.Add("Content-Type", GuessTextMediaType(action.Value!));
        }

        headers.Add("Cache-Control", "no-store");
        return new EditableResponse(status, Engine.ReasonPhrases.Get(status), headers, body);
    }

    private static async Task<EditableResponse> FileResponseAsync(TrafficRule rule, RuleAction action, CancellationToken cancellationToken)
    {
        var status = RuleSet.ParseStatus(action.Name) ?? 200;
        var path = Environment.ExpandEnvironmentVariables(action.Value.Trim().Trim('"'));
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return ErrorResponse(404, $"Snoop rule '{rule.Name}': the file {path} does not exist.");
            }

            if (info.Length > MaxResponseFileBytes)
            {
                return ErrorResponse(500, $"Snoop rule '{rule.Name}': the file {path} is larger than {MaxResponseFileBytes / (1024 * 1024)} MB.");
            }

            var body = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var headers = new HttpHeaderList();
            headers.Add("Content-Type", MediaTypeForFile(path));
            headers.Add("Cache-Control", "no-store");
            return new EditableResponse(status, Engine.ReasonPhrases.Get(status), headers, body);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ErrorResponse(500, $"Snoop rule '{rule.Name}': could not read {path}: {ex.Message}");
        }
    }

    private static EditableResponse ErrorResponse(int status, string message)
    {
        var headers = new HttpHeaderList();
        headers.Add("Content-Type", "text/plain; charset=utf-8");
        headers.Add("Cache-Control", "no-store");
        return new EditableResponse(status, Engine.ReasonPhrases.Get(status), headers, Encoding.UTF8.GetBytes(message));
    }
}
