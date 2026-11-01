using System.Globalization;
using System.Text;
using Snoop.Core.Http;

namespace Snoop.Inspection.Export;

/// <summary>A request read from a cURL command.</summary>
/// <param name="Method">HTTP method.</param>
/// <param name="Url">The URL as written (it may have no scheme).</param>
/// <param name="Headers">Headers in the order given.</param>
/// <param name="Body">The body, or null for none.</param>
/// <param name="Warnings">Options that were ignored or could not be used, for the user to see.</param>
public sealed record CurlRequest(string Method, string Url, HttpHeaderList Headers, string? Body, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads a cURL command, as copied from browser developer tools ("Copy as cURL" for bash or for cmd),
/// API documentation or <see cref="CommandExporter.ToCurl"/>, into a request.
/// </summary>
public static class CurlImporter
{
    // Options that take a value but do not change the request.
    private static readonly HashSet<string> IgnoredWithValue = new(StringComparer.Ordinal)
    {
        "-o", "--output", "-w", "--write-out", "-m", "--max-time", "--connect-timeout", "--retry", "--retry-delay",
        "--retry-max-time", "-x", "--proxy", "--cacert", "--capath", "--cert", "-E", "--key", "--cert-type", "--key-type",
        "-c", "--cookie-jar", "--resolve", "--connect-to", "--limit-rate", "-r", "--range", "--interface", "--local-port",
        "-y", "--speed-time", "-Y", "--speed-limit", "--max-redirs", "--max-filesize", "-U", "--proxy-user", "--dns-servers",
        "--trace", "--trace-ascii", "--stderr", "--ciphers", "--pinnedpubkey", "--user-agent-file", "-D", "--dump-header",
    };

    // Options without a value that do not change the request.
    private static readonly HashSet<string> IgnoredFlags = new(StringComparer.Ordinal)
    {
        "-s", "--silent", "-S", "--show-error", "-L", "--location", "-k", "--insecure", "-i", "--include", "-v", "--verbose",
        "-f", "--fail", "--fail-with-body", "--compressed", "-N", "--no-buffer", "-g", "--globoff", "-#", "--progress-bar",
        "--http1.0", "--http1.1", "--http2", "--http2-prior-knowledge", "--http3", "-0", "-1", "-2", "-3", "-4", "-6",
        "--tlsv1", "--tlsv1.0", "--tlsv1.1", "--tlsv1.2", "--tlsv1.3", "--ssl", "--ssl-reqd", "--no-keepalive",
        "--path-as-is", "-q", "--disable", "--raw", "--tr-encoding", "--no-progress-meter", "-Z", "--parallel", "-O",
        "--remote-name", "-J", "--remote-header-name", "--location-trusted", "--anyauth", "--basic", "--digest", "--ntlm",
        "--negotiate", "--no-sessionid", "--tcp-nodelay", "--tcp-fastopen", "-n", "--netrc", "--netrc-optional",
    };

    // Short options that take a value (it may be attached: -XPOST).
    private const string ShortWithValue = "XHdubAeFTomwxcrEDyYU";

    /// <summary>True when <paramref name="text"/> looks like a cURL command.</summary>
    public static bool LooksLikeCurl(string? text)
    {
        var trimmed = text?.TrimStart() ?? string.Empty;
        return trimmed.StartsWith("curl ", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("curl.exe ", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("curl\t", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("curl\n", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("curl\r", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads <paramref name="command"/>. False with <paramref name="error"/> when it is not a usable cURL command.</summary>
    public static bool TryParse(string? command, out CurlRequest? request, out string? error)
    {
        request = null;
        if (!LooksLikeCurl(command))
        {
            error = "This is not a cURL command (it should start with \"curl\").";
            return false;
        }

        List<string> words;
        try
        {
            words = Split(command!);
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }

        words.RemoveAt(0); // "curl"
        return TryBuild(words, out request, out error);
    }

    private static bool TryBuild(List<string> words, out CurlRequest? request, out string? error)
    {
        request = null;
        string? method = null;
        string? url = null;
        var headers = new HttpHeaderList();
        var data = new List<string>();
        var warnings = new List<string>();
        var getWithData = false;
        var head = false;
        var json = false;
        string? user = null;

        var optionsDone = false;
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (optionsDone || word.Length < 2 || word[0] != '-')
            {
                if (url is null)
                {
                    url = word;
                }
                else
                {
                    warnings.Add($"Only the first URL is used; \"{word}\" was left out.");
                }

                continue;
            }

            if (word == "--")
            {
                optionsDone = true;
                continue;
            }

            // Short options can be grouped (-sSL) and short values attached (-XPOST).
            var options = new List<(string Name, string? Attached)>();
            if (word[1] != '-')
            {
                for (var c = 1; c < word.Length; c++)
                {
                    var name = "-" + word[c];
                    if (ShortWithValue.Contains(word[c], StringComparison.Ordinal))
                    {
                        options.Add((name, c + 1 < word.Length ? word[(c + 1)..] : null));
                        break;
                    }

                    options.Add((name, null));
                }
            }
            else
            {
                options.Add((word, null));
            }

            foreach (var (name, attached) in options)
            {
                string Value()
                {
                    if (attached is not null)
                    {
                        return attached;
                    }

                    if (i + 1 >= words.Count)
                    {
                        throw new FormatException($"{name} needs a value.");
                    }

                    return words[++i];
                }

                try
                {
                    switch (name)
                    {
                        case "-X":
                        case "--request":
                            method = Value().Trim().ToUpperInvariant();
                            break;
                        case "-H":
                        case "--header":
                            AddHeader(headers, Value(), warnings);
                            break;
                        case "-d":
                        case "--data":
                        case "--data-ascii":
                        case "--data-binary":
                            AddData(data, Value(), allowFile: true, warnings);
                            break;
                        case "--data-raw":
                            data.Add(Value());
                            break;
                        case "--data-urlencode":
                            data.Add(UrlEncodeData(Value(), warnings));
                            break;
                        case "--json":
                            AddData(data, Value(), allowFile: true, warnings);
                            json = true;
                            break;
                        case "-G":
                        case "--get":
                            getWithData = true;
                            break;
                        case "-I":
                        case "--head":
                            head = true;
                            break;
                        case "-u":
                        case "--user":
                            user = Value();
                            break;
                        case "-A":
                        case "--user-agent":
                            headers.Set("User-Agent", Value());
                            break;
                        case "-e":
                        case "--referer":
                            headers.Set("Referer", Value());
                            break;
                        case "-b":
                        case "--cookie":
                            var cookie = Value();
                            if (cookie.Contains('=', StringComparison.Ordinal))
                            {
                                headers.Add("Cookie", cookie);
                            }
                            else
                            {
                                warnings.Add($"Cookies from the file \"{cookie}\" were left out.");
                            }

                            break;
                        case "--url":
                            var value = Value();
                            if (url is null)
                            {
                                url = value;
                            }
                            else
                            {
                                warnings.Add($"Only the first URL is used; \"{value}\" was left out.");
                            }

                            break;
                        case "-F":
                        case "--form":
                        case "--form-string":
                            warnings.Add($"Multipart form fields ({name} {Value()}) are not supported yet and were left out.");
                            break;
                        case "-T":
                        case "--upload-file":
                            warnings.Add($"Uploading a file ({name} {Value()}) is not supported; the body was left out.");
                            break;
                        default:
                            if (IgnoredWithValue.Contains(name))
                            {
                                Value();
                            }
                            else if (!IgnoredFlags.Contains(name))
                            {
                                warnings.Add($"{name} is not understood and was ignored.");
                            }

                            break;
                    }
                }
                catch (FormatException ex)
                {
                    error = ex.Message;
                    return false;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            error = "The cURL command has no URL.";
            return false;
        }

        if (user is not null)
        {
            var credentials = user.Contains(':', StringComparison.Ordinal) ? user : user + ":";
            headers.Set("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        }

        string? body = null;
        if (data.Count > 0)
        {
            var joined = string.Join('&', data);
            if (getWithData)
            {
                url += (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + joined;
            }
            else
            {
                body = joined;
                if (json)
                {
                    if (!headers.Contains("Content-Type"))
                    {
                        headers.Add("Content-Type", "application/json");
                    }

                    if (!headers.Contains("Accept"))
                    {
                        headers.Add("Accept", "application/json");
                    }
                }
                else if (!headers.Contains("Content-Type"))
                {
                    headers.Add("Content-Type", "application/x-www-form-urlencoded");
                }
            }
        }

        method ??= head ? "HEAD" : body is not null ? "POST" : "GET";
        request = new CurlRequest(method, url.Trim(), headers, body, warnings);
        error = null;
        return true;
    }

    private static void AddHeader(HttpHeaderList headers, string header, List<string> warnings)
    {
        if (header.StartsWith('@'))
        {
            warnings.Add($"Headers from the file \"{header[1..]}\" were left out.");
            return;
        }

        var colon = header.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            // "Name;" sends an empty header in cURL.
            if (header.EndsWith(';') && header.Length > 1)
            {
                headers.Add(header[..^1].Trim(), string.Empty);
            }
            else
            {
                warnings.Add($"\"{header}\" is not a header (Name: value) and was left out.");
            }

            return;
        }

        var name = header[..colon].Trim();
        var value = header[(colon + 1)..].Trim();
        if (value.Length == 0)
        {
            // "Name:" removes a header cURL would add itself; there is nothing to remove here.
            return;
        }

        headers.Add(name, value);
    }

    private static void AddData(List<string> data, string value, bool allowFile, List<string> warnings)
    {
        if (allowFile && value.StartsWith('@'))
        {
            warnings.Add($"The body from the file \"{value[1..]}\" was left out; paste the file's text into the body.");
            return;
        }

        data.Add(value);
    }

    /// <summary>cURL's --data-urlencode forms: "content", "=content", "name=content" (and "@file" forms, not supported).</summary>
    private static string UrlEncodeData(string value, List<string> warnings)
    {
        var at = value.IndexOf('@', StringComparison.Ordinal);
        var equals = value.IndexOf('=', StringComparison.Ordinal);
        if (at >= 0 && (equals < 0 || at < equals))
        {
            warnings.Add($"--data-urlencode {value}: data from files was left out.");
            return string.Empty;
        }

        if (equals < 0)
        {
            return Uri.EscapeDataString(value);
        }

        var name = value[..equals];
        var content = Uri.EscapeDataString(value[(equals + 1)..]);
        return name.Length == 0 ? content : $"{name}={content}";
    }

    /// <summary>
    /// Splits a command into words the way a shell would. Understands bash quoting ('...', "...", $'...',
    /// backslash escapes and line continuations) and Windows cmd quoting (^ escapes, ^ line continuations),
    /// which is what browsers produce for "Copy as cURL (cmd)". PowerShell's ` line continuation is accepted too.
    /// </summary>
    internal static List<string> Split(string command)
    {
        var text = command.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (LooksLikeCmd(text))
        {
            text = UnescapeCmd(text);
        }

        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case ' ' or '\t' or '\n':
                    if (inWord)
                    {
                        words.Add(word.ToString());
                        word.Clear();
                        inWord = false;
                    }

                    break;

                case '\\':
                    inWord = true;
                    if (i + 1 < text.Length)
                    {
                        i++;
                        if (text[i] != '\n')
                        {
                            word.Append(text[i]);
                        }
                        else if (word.Length == 0)
                        {
                            inWord = false; // a line continuation between words
                        }
                    }

                    break;

                case '`' when i + 1 < text.Length && text[i + 1] == '\n':
                    i++; // PowerShell line continuation
                    break;

                case '\'':
                    inWord = true;
                    var end = text.IndexOf('\'', i + 1);
                    if (end < 0)
                    {
                        throw new FormatException("A quote (') is not closed.");
                    }

                    word.Append(text, i + 1, end - i - 1);
                    i = end;
                    break;

                case '$' when i + 1 < text.Length && text[i + 1] == '\'':
                    inWord = true;
                    i = ReadAnsiQuoted(text, i + 2, word);
                    break;

                case '"':
                    inWord = true;
                    i = ReadDoubleQuoted(text, i + 1, word);
                    break;

                default:
                    inWord = true;
                    word.Append(c);
                    break;
            }
        }

        if (inWord)
        {
            words.Add(word.ToString());
        }

        return words;
    }

    /// <summary>cmd-style commands use ^ to escape: ^" quotes, ^ at a line end continues the line.</summary>
    private static bool LooksLikeCmd(string text) =>
        text.Contains("^\"", StringComparison.Ordinal) || text.Contains("^\n", StringComparison.Ordinal);

    private static string UnescapeCmd(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '^' && i + 1 < text.Length)
            {
                i++;
                if (text[i] != '\n')
                {
                    builder.Append(text[i]);
                }
                else
                {
                    builder.Append(' ');
                }
            }
            else
            {
                builder.Append(text[i]);
            }
        }

        return builder.ToString();
    }

    /// <summary>Inside "...": backslash escapes only $ ` " \ and a newline.</summary>
    private static int ReadDoubleQuoted(string text, int start, StringBuilder word)
    {
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                return i;
            }

            if (c == '\\' && i + 1 < text.Length && text[i + 1] is '$' or '`' or '"' or '\\' or '\n')
            {
                i++;
                if (text[i] != '\n')
                {
                    word.Append(text[i]);
                }

                continue;
            }

            word.Append(c);
        }

        throw new FormatException("A quote (\") is not closed.");
    }

    /// <summary>$'...': C-style escapes (\n, \t, \', \\, \xHH, \uHHHH ...).</summary>
    private static int ReadAnsiQuoted(string text, int start, StringBuilder word)
    {
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'')
            {
                return i;
            }

            if (c != '\\' || i + 1 >= text.Length)
            {
                word.Append(c);
                continue;
            }

            var e = text[++i];
            switch (e)
            {
                case 'n': word.Append('\n'); break;
                case 't': word.Append('\t'); break;
                case 'r': word.Append('\r'); break;
                case '0': word.Append('\0'); break;
                case 'a': word.Append('\a'); break;
                case 'b': word.Append('\b'); break;
                case 'e' or 'E': word.Append('\u001b'); break;
                case 'f': word.Append('\f'); break;
                case 'v': word.Append('\v'); break;
                case 'x':
                    i = AppendHex(text, i, 2, word);
                    break;
                case 'u':
                    i = AppendHex(text, i, 4, word);
                    break;
                case 'U':
                    i = AppendHex(text, i, 8, word);
                    break;
                default:
                    word.Append(e); // \\ \' \" and anything else
                    break;
            }
        }

        throw new FormatException("A quote ($'...') is not closed.");
    }

    private static int AppendHex(string text, int index, int maxDigits, StringBuilder word)
    {
        var digits = 0;
        while (digits < maxDigits && index + 1 + digits < text.Length && Uri.IsHexDigit(text[index + 1 + digits]))
        {
            digits++;
        }

        if (digits == 0)
        {
            word.Append('\\').Append(text[index]);
            return index;
        }

        var code = int.Parse(text.AsSpan(index + 1, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (maxDigits == 2)
        {
            word.Append((char)code); // a byte; treated as Latin-1
        }
        else if (code <= 0x10FFFF && code is < 0xD800 or > 0xDFFF)
        {
            word.Append(char.ConvertFromUtf32(code));
        }

        return index + digits;
    }
}
