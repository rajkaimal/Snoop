using System.Globalization;
using Snoop.Core.Bodies;
using Snoop.Core.Formatting;
using Snoop.Core.Http;
using Snoop.Core.Sessions;
using Snoop.Inspection.Bodies;
using Snoop.Inspection.Formatting;
using Snoop.Presentation.Mvvm;

namespace Snoop.Presentation.ViewModels;

/// <summary>A name/value line in a table (headers, query parameters, overview).</summary>
public sealed record NameValueRow(string Name, string Value);

/// <summary>The detail pane for the selected session: overview, request and response.</summary>
public sealed class InspectorViewModel : ObservableObject
{
    private Session? _session;
    private IReadOnlyList<NameValueRow> _overview = [];

    public Session? Session => _session;

    public bool HasSession => _session is not null;

    public string Title => _session is null ? "Select a session to inspect it" : $"{_session.Request.Method} {_session.Request.Url}";

    public IReadOnlyList<NameValueRow> Overview
    {
        get => _overview;
        private set => SetProperty(ref _overview, value);
    }

    public MessageViewModel Request { get; } = new("No request selected");

    public MessageViewModel Response { get; } = new("No response yet");

    /// <summary>Shows <paramref name="session"/> (or clears the pane for null).</summary>
    public void Show(Session? session)
    {
        _session = session;
        Reload();
    }

    /// <summary>Re-reads the current session (it has progressed since it was shown).</summary>
    public void Refresh() => Reload();

    private void Reload()
    {
        var session = _session;
        OnPropertyChanged(nameof(Session));
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(Title));
        if (session is null)
        {
            Overview = [];
            Request.Clear("No request selected");
            Response.Clear("No response yet");
            return;
        }

        Overview = BuildOverview(session);
        Request.Load(session.Request.Headers, session.Request.Body, RawMessageFormatter.FormatRequest(session.Request), QueryParameters(session.Request.Url));
        if (session.Response is { } response)
        {
            Response.Load(response.Headers, response.Body, RawMessageFormatter.FormatResponse(response), []);
        }
        else
        {
            Response.Clear(session.Error ?? (session.IsFinished ? "No response" : "Waiting for the response..."));
        }
    }

    internal static IReadOnlyList<NameValueRow> BuildOverview(Session session)
    {
        var rows = new List<NameValueRow>
        {
            new("URL", session.Request.Url.ToString()),
            new("Status", session.Response is { } r ? $"{r.StatusCode} {r.ReasonPhrase}" : session.State.ToString()),
            new("State", session.State.ToString()),
            new("Kind", session.Kind.ToString()),
            new("Process", session.ClientProcess?.ToString() ?? "unknown"),
            new("Client", session.Endpoints.Client.ToString()),
            new("Started", session.Timings.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)),
        };

        AddTime(rows, "Request sent", session.Timings.RequestSent);
        AddTime(rows, "First response byte", session.Timings.ResponseHeadersReceived);
        AddTime(rows, "Total time", session.Timings.Duration);

        if (session.Tls is { } tls)
        {
            rows.Add(new("TLS", tls.Decrypted ? $"Decrypted · {tls.Protocol} · {tls.CipherSuite}" : "Not decrypted"));
            if (tls.ServerName is not null)
            {
                rows.Add(new("Server name (SNI)", tls.ServerName));
            }
        }

        if (session.Kind != SessionKind.Http)
        {
            rows.Add(new("Bytes sent", ByteSize.Format(session.BytesSent)));
            rows.Add(new("Bytes received", ByteSize.Format(session.BytesReceived)));
        }

        rows.Add(new("Request body", Describe(session.Request.Body)));
        rows.Add(new("Response body", Describe(session.Response?.Body)));

        if (session.Error is { } error)
        {
            rows.Add(new("Error", error));
        }

        if (session.Note is { } note)
        {
            rows.Add(new("Note", note));
        }

        if (session.ModifiedBy is { } modifiedBy)
        {
            rows.Add(new("Changed by", modifiedBy));
        }

        if (session.OriginalRequest is { } original)
        {
            if (!string.Equals(original.Method, session.Request.Method, StringComparison.Ordinal) || original.Url != session.Request.Url)
            {
                rows.Add(new("Original request", $"{original.Method} {original.Url}"));
            }

            rows.Add(new("Original headers", original.Headers.ToString().TrimEnd()));
        }

        var origin = new List<string>();
        if (session.Flags.HasFlag(SessionFlags.AutoResponded))
        {
            origin.Add("answered by a rule (never reached the server)");
        }

        if (session.Flags.HasFlag(SessionFlags.Breakpoint))
        {
            origin.Add("paused at a breakpoint");
        }

        if (session.Flags.HasFlag(SessionFlags.Composed))
        {
            origin.Add("sent from the Composer");
        }

        if (session.Flags.HasFlag(SessionFlags.Imported))
        {
            origin.Add("opened from a snapshot or HAR file");
        }

        if (origin.Count > 0)
        {
            rows.Add(new("Origin", string.Join("; ", origin)));
        }

        return rows;
    }

    private static void AddTime(List<NameValueRow> rows, string name, TimeSpan? value)
    {
        if (value is { } time)
        {
            rows.Add(new(name, $"{time.TotalMilliseconds:0.0} ms"));
        }
    }

    private static string Describe(CapturedBody? body) =>
        body is null ? "none" : body.IsTruncated ? $"{ByteSize.Format(body.TotalBytes)} (first {ByteSize.Format(body.StoredBytes)} recorded)" : ByteSize.Format(body.TotalBytes);

    private static IReadOnlyList<NameValueRow> QueryParameters(Uri url)
    {
        if (string.IsNullOrEmpty(url.Query) || url.Query == "?")
        {
            return [];
        }

        return url.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                var equals = part.IndexOf('=', StringComparison.Ordinal);
                var name = equals < 0 ? part : part[..equals];
                var value = equals < 0 ? string.Empty : part[(equals + 1)..];
                return new NameValueRow(Unescape(name), Unescape(value));
            })
            .ToList();
    }

    private static string Unescape(string text)
    {
        try
        {
            return Uri.UnescapeDataString(text.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return text;
        }
    }
}

/// <summary>One side of the exchange (request or response) in the inspector.</summary>
public sealed class MessageViewModel(string emptyText) : ObservableObject
{
    private HttpHeaderList _headerList = new();
    private CapturedBody? _body;
    private bool _isPresent;
    private string _placeholder = emptyText;
    private IReadOnlyList<NameValueRow> _headers = [];
    private IReadOnlyList<NameValueRow> _parameters = [];
    private string _rawText = string.Empty;
    private BodyViewMode _bodyMode = BodyViewMode.Auto;
    private RenderedBody _renderedBody = RenderedBody.None;
    private RenderedBody? _spansFor;
    private IReadOnlyList<SyntaxSpan>? _spans;

    public static IReadOnlyList<BodyViewMode> BodyModes { get; } = Enum.GetValues<BodyViewMode>();

    public bool IsPresent
    {
        get => _isPresent;
        private set => SetProperty(ref _isPresent, value);
    }

    /// <summary>Shown instead of the message when there is none.</summary>
    public string Placeholder
    {
        get => _placeholder;
        private set => SetProperty(ref _placeholder, value);
    }

    public IReadOnlyList<NameValueRow> Headers
    {
        get => _headers;
        private set
        {
            if (SetProperty(ref _headers, value))
            {
                OnPropertyChanged(nameof(HeadersTitle));
            }
        }
    }

    public string HeadersTitle => $"Headers ({_headers.Count})";

    /// <summary>Query string parameters (requests only).</summary>
    public IReadOnlyList<NameValueRow> Parameters
    {
        get => _parameters;
        private set
        {
            if (SetProperty(ref _parameters, value))
            {
                OnPropertyChanged(nameof(ParametersTitle));
            }
        }
    }

    public string ParametersTitle => $"Params ({_parameters.Count})";

    public string RawText
    {
        get => _rawText;
        private set => SetProperty(ref _rawText, value);
    }

    /// <summary>The chosen body view. Changing it re-renders the body.</summary>
    public BodyViewMode BodyMode
    {
        get => _bodyMode;
        set
        {
            if (SetProperty(ref _bodyMode, value))
            {
                OnPropertyChanged(nameof(BodyModeIndex));
                RenderBody();
            }
        }
    }

    /// <summary><see cref="BodyMode"/> as an index into <see cref="BodyModes"/>, for list controls.</summary>
    public int BodyModeIndex
    {
        get => (int)_bodyMode;
        set
        {
            if (value >= 0 && value < BodyModes.Count)
            {
                BodyMode = BodyModes[value];
            }
        }
    }

    public RenderedBody RenderedBody
    {
        get => _renderedBody;
        private set
        {
            if (SetProperty(ref _renderedBody, value))
            {
                OnPropertyChanged(nameof(BodyText));
                OnPropertyChanged(nameof(BodySummary));
                OnPropertyChanged(nameof(ImageBytes));
                OnPropertyChanged(nameof(IsImage));
                OnPropertyChanged(nameof(IsTextBody));
                OnPropertyChanged(nameof(BodySpans));
                OnPropertyChanged(nameof(HasColoredBody));
                OnPropertyChanged(nameof(IsPlainTextBody));
            }
        }
    }

    /// <summary>
    /// Coloured runs for JSON, XML and HTML bodies, or null when the body is shown as plain text (other
    /// content, hex, or too long to colour quickly). Worked out when first asked for.
    /// </summary>
    public IReadOnlyList<SyntaxSpan>? BodySpans
    {
        get
        {
            if (!ReferenceEquals(_spansFor, _renderedBody))
            {
                _spansFor = _renderedBody;
                var language = LanguageOf(_renderedBody);
                _spans = language == SyntaxLanguage.None || _renderedBody.Text.Length == 0 || _renderedBody.Text.Length > SyntaxHighlighter.MaxChars
                    ? null
                    : SyntaxHighlighter.Highlight(_renderedBody.Text, language);
            }

            return _spans;
        }
    }

    /// <summary>Show the coloured view instead of the plain text box.</summary>
    public bool HasColoredBody => !IsImage && BodySpans is not null;

    /// <summary>Show the plain text box (text that is not coloured, and hex).</summary>
    public bool IsPlainTextBody => !IsImage && BodySpans is null;

    private static SyntaxLanguage LanguageOf(RenderedBody body) => body.Mode switch
    {
        BodyViewMode.Json => SyntaxLanguage.Json,
        BodyViewMode.Xml => SyntaxLanguage.Markup,
        BodyViewMode.Text when body.Kind is ContentKind.Html or ContentKind.Xml => SyntaxLanguage.Markup,
        _ => SyntaxLanguage.None,
    };

    public string BodyText => _renderedBody.Text;

    public string BodySummary => _renderedBody.Summary;

    public byte[]? ImageBytes => _renderedBody.ImageBytes;

    public bool IsImage => _renderedBody.Mode == BodyViewMode.Image && _renderedBody.ImageBytes is not null;

    public bool IsTextBody => !IsImage;

    public void Load(HttpHeaderList headers, CapturedBody? body, string rawText, IReadOnlyList<NameValueRow> parameters)
    {
        ArgumentNullException.ThrowIfNull(headers);
        _headerList = headers;
        _body = body;
        IsPresent = true;
        Headers = headers.Select(h => new NameValueRow(h.Name, h.Value)).ToList();
        Parameters = parameters;
        RawText = rawText;
        RenderBody();
    }

    public void Clear(string placeholder)
    {
        _headerList = new HttpHeaderList();
        _body = null;
        Placeholder = placeholder;
        IsPresent = false;
        Headers = [];
        Parameters = [];
        RawText = string.Empty;
        RenderedBody = RenderedBody.None;
    }

    private void RenderBody() => RenderedBody = BodyRenderer.Render(_body, _headerList, _bodyMode);
}
