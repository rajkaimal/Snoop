using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using Snoop.Core.Bodies;
using Snoop.Core.Http;
using Snoop.Core.Sessions;
using Snoop.Hosting;
using Snoop.Hosting.Composer;
using Snoop.Inspection.Export;
using Snoop.Inspection.Formatting;
using Snoop.Presentation.Composer;
using Snoop.Presentation.Editing;
using Snoop.Presentation.Mvvm;
using Snoop.Presentation.Services;

namespace Snoop.Presentation.ViewModels;

/// <summary>
/// The Composer: write a request (or start from a captured one, a pasted cURL command or a saved
/// request), send it through the capture so it is recorded and rules apply, and see the response.
/// <c>{{name}}</c> placeholders are filled from the active environment when sending.
/// </summary>
public sealed class ComposerViewModel : ObservableObject
{
    public const string UntitledName = "New request";

    private static readonly HashSet<string> SkippedWhenCopying = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection", "Proxy-Connection", "Keep-Alive", "Proxy-Authorization", "TE",
    };

    private readonly CaptureHost _host;
    private readonly IClipboardService? _clipboard;
    private CancellationTokenSource? _sending;
    private string _method = "GET";
    private string _url = "https://";
    private string _headersText = string.Empty;
    private bool _isBulkEdit;
    private string _bodyText = string.Empty;
    private string _lineEnding = "\n";
    private string _resultText = "Write a request and press Send. It goes through Snoop, so it is captured and rules apply.";
    private bool _resultIsError;
    private bool _isSending;
    private string? _currentPath;
    private bool _isModified;
    private int _loading;
    private bool _isLibraryOpen = true;

    public ComposerViewModel(CaptureHost host, IClipboardService? clipboard = null, string? libraryDirectory = null, string? environmentsPath = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _clipboard = clipboard;
        SendCommand = new AsyncRelayCommand(SendAsync, ex => ShowResult(ex.Message, isError: true), () => !_isSending);
        CancelCommand = new RelayCommand(() => _sending?.Cancel(), () => _isSending);
        AddHeaderCommand = new RelayCommand(() => Headers.Add(new HeaderRowViewModel()));
        NewRequestCommand = new RelayCommand(NewRequest);
        SaveCommand = new RelayCommand(Save);
        CopyAsCurlCommand = new RelayCommand(CopyAsCurl, () => _clipboard is not null);
        Library = new RequestLibraryViewModel(libraryDirectory is null ? null : new RequestLibrary(libraryDirectory), ShowResult);
        Library.Moved += (_, move) => OnLibraryMoved(move.OldPath, move.NewPath, move.IsFolder);
        Environments = new EnvironmentsViewModel(environmentsPath);
        Headers.CollectionChanged += (_, e) =>
        {
            foreach (HeaderRowViewModel row in e.NewItems ?? Array.Empty<HeaderRowViewModel>())
            {
                row.PropertyChanged += OnHeaderRowChanged;
            }

            foreach (HeaderRowViewModel row in e.OldItems ?? Array.Empty<HeaderRowViewModel>())
            {
                row.PropertyChanged -= OnHeaderRowChanged;
            }

            MarkModified();
            OnPropertyChanged(nameof(HeadersTitle));
        };
    }

    /// <summary>Raised when a sent request was recorded, so the session list can show it.</summary>
    public event EventHandler<Session>? Sent;

    /// <summary>Raised when Save needs a name (the request was never saved); the view asks, then calls <see cref="SaveAs"/>.</summary>
    public event EventHandler? SaveAsRequested;

    public AsyncRelayCommand SendCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand AddHeaderCommand { get; }

    public RelayCommand NewRequestCommand { get; }

    public RelayCommand SaveCommand { get; }

    public RelayCommand CopyAsCurlCommand { get; }

    /// <summary>Saved requests.</summary>
    public RequestLibraryViewModel Library { get; }

    /// <summary>Show the saved-requests list beside the editor.</summary>
    public bool IsLibraryOpen
    {
        get => _isLibraryOpen;
        set => SetProperty(ref _isLibraryOpen, value);
    }

    /// <summary>Variables and the active environment.</summary>
    public EnvironmentsViewModel Environments { get; }

    public string Method
    {
        get => _method;
        set
        {
            if (SetProperty(ref _method, value ?? string.Empty))
            {
                MarkModified();
            }
        }
    }

    /// <summary>The URL. Pasting a whole cURL command here imports it.</summary>
    public string Url
    {
        get => _url;
        set
        {
            value ??= string.Empty;

            // A paste (many characters at once) of a cURL command fills in the whole request.
            if (_loading == 0 && value.Length - _url.Length > 5 && CurlImporter.LooksLikeCurl(value) && ImportCurl(value))
            {
                return;
            }

            if (SetProperty(ref _url, value))
            {
                MarkModified();
            }
        }
    }

    /// <summary>The headers table. Unticked rows are not sent.</summary>
    public ObservableCollection<HeaderRowViewModel> Headers { get; } = [];

    /// <summary>"Headers" or "Headers (3)".</summary>
    public string HeadersTitle
    {
        get
        {
            var count = Headers.Count(h => h.Enabled && !h.IsBlank);
            return count == 0 ? "Headers" : $"Headers ({count.ToString(CultureInfo.InvariantCulture)})";
        }
    }

    /// <summary>Edit the headers as text (one "Name: value" per line; "# " at the start turns a header off).</summary>
    public bool IsBulkEdit
    {
        get => _isBulkEdit;
        set
        {
            if (value == _isBulkEdit)
            {
                return;
            }

            if (value)
            {
                _headersText = FormatRows();
            }
            else if (!TryParseHeaderText(_headersText, out var rows, out var error))
            {
                ShowResult(error!, isError: true);
                OnPropertyChanged(); // stay in text mode
                return;
            }
            else
            {
                ReplaceRows(rows);
            }

            _isBulkEdit = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTableEdit));
            OnPropertyChanged(nameof(HeadersText));
        }
    }

    public bool IsTableEdit => !_isBulkEdit;

    /// <summary>
    /// The headers as text. Setting it while the table is shown fills the table; text the table cannot
    /// show (a line that is not a header) switches to text editing so the mistake can be fixed.
    /// </summary>
    public string HeadersText
    {
        get => _isBulkEdit ? _headersText : FormatRows();
        set
        {
            value ??= string.Empty;
            if (_isBulkEdit)
            {
                if (SetProperty(ref _headersText, value))
                {
                    MarkModified();
                }

                return;
            }

            // The (hidden) text box echoing the table's own text back changes nothing.
            if (value == FormatRows())
            {
                return;
            }

            if (TryParseHeaderText(value, out var rows, out _))
            {
                ReplaceRows(rows);
                OnPropertyChanged();
                return;
            }

            _headersText = value;
            _isBulkEdit = true;
            OnPropertyChanged(nameof(IsBulkEdit));
            OnPropertyChanged(nameof(IsTableEdit));
            OnPropertyChanged();
            MarkModified();
        }
    }

    public string BodyText
    {
        get => _bodyText;
        set
        {
            if (SetProperty(ref _bodyText, value ?? string.Empty))
            {
                MarkModified();
            }
        }
    }

    public bool IsSending
    {
        get => _isSending;
        private set
        {
            if (SetProperty(ref _isSending, value))
            {
                CancelCommand.NotifyCanExecuteChanged();
                SendCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Status line of the last send, or why it failed.</summary>
    public string ResultText
    {
        get => _resultText;
        private set => SetProperty(ref _resultText, value);
    }

    public bool ResultIsError
    {
        get => _resultIsError;
        private set => SetProperty(ref _resultIsError, value);
    }

    /// <summary>The response to the last request.</summary>
    public MessageViewModel Response { get; } = new("No response yet");

    /// <summary>The recorded session of the last request, if it was captured.</summary>
    public Session? LastSession { get; private set; }

    /// <summary>Library path of the saved request being edited, or null for an unsaved one.</summary>
    public string? CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (SetProperty(ref _currentPath, value))
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(CurrentName));
            }
        }
    }

    public string CurrentName => _currentPath is null ? UntitledName : RequestLibrary.NameOf(_currentPath);

    /// <summary>True when there are changes since the request was opened or saved.</summary>
    public bool IsModified
    {
        get => _isModified;
        private set
        {
            if (SetProperty(ref _isModified, value))
            {
                OnPropertyChanged(nameof(Title));
            }
        }
    }

    /// <summary>e.g. "Get user — Edited".</summary>
    public string Title => _isModified && _currentPath is not null ? $"{CurrentName} — Edited" : CurrentName;

    public void RemoveHeader(HeaderRowViewModel row) => Headers.Remove(row);

    /// <summary>Fills the Composer from a captured request, ready to edit and resend.</summary>
    public void LoadFrom(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var request = session.Request;
        var body = request.Body is { } captured ? MessageText.DescribeBody(captured.ToArray(), request.Headers) : Snoop.Presentation.Editing.BodyText.Empty;
        Load(
            request.Method,
            request.Url.AbsoluteUri,
            request.Headers.Where(h => !SkippedWhenCopying.Contains(h.Name)).Select(h => new SavedHeader(h.Name, h.Value)),
            body.Text,
            path: null);
        ShowResult(
            body.IsEditable
                ? $"Loaded #{session.Id.ToString(CultureInfo.InvariantCulture)}. Edit it and press Send."
                : $"Loaded #{session.Id.ToString(CultureInfo.InvariantCulture)}, but its body is binary and was left out.",
            isError: !body.IsEditable);
    }

    /// <summary>Fills the Composer from a cURL command. False (with the reason shown) when it cannot be read.</summary>
    public bool ImportCurl(string command)
    {
        if (!CurlImporter.TryParse(command, out var parsed, out var error))
        {
            ShowResult(error!, isError: true);
            return false;
        }

        Load(parsed!.Method, parsed.Url, parsed.Headers.Select(h => new SavedHeader(h.Name, h.Value)), parsed.Body ?? string.Empty, path: null);
        ShowResult(
            parsed.Warnings.Count == 0
                ? "Imported the cURL command. Press Send."
                : "Imported the cURL command. " + string.Join(" ", parsed.Warnings),
            isError: parsed.Warnings.Count > 0);
        return true;
    }

    /// <summary>Opens a saved request.</summary>
    public bool Open(LibraryItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsFolder || Library.Library is not { } library)
        {
            return false;
        }

        try
        {
            var saved = library.Load(item.Path);
            Load(saved.Method, saved.Url, saved.Headers, saved.Body, item.Path);
            ShowResult($"Opened \"{item.Name}\".", isError: false);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ShowResult(ex.Message, isError: true);
            return false;
        }
    }

    /// <summary>Saves under a new name in <paramref name="folder"/> (null: top level) and keeps editing that saved request.</summary>
    public bool SaveAs(string name, string? folder)
    {
        if (Library.Library is not { } library)
        {
            ShowResult("Saved requests are not available.", isError: true);
            return false;
        }

        try
        {
            CurrentPath = library.SaveNew(folder, name, ToSaved());
            IsModified = false;
            Library.Refresh(reveal: CurrentPath);
            ShowResult($"Saved \"{CurrentName}\".", isError: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowResult($"Could not save: {ex.Message}", isError: true);
            return false;
        }
    }

    /// <summary>The name the Save dialog suggests: the current name, or one made from the URL.</summary>
    public string SuggestedName()
    {
        if (_currentPath is not null)
        {
            return CurrentName;
        }

        var url = _url.Trim();
        var text = Uri.TryCreate(url.Contains("://", StringComparison.Ordinal) ? url : "https://" + url, UriKind.Absolute, out var uri)
            ? (uri.AbsolutePath.Trim('/') is { Length: > 0 } path ? path.Split('/')[^1] : uri.Host)
            : url;
        return RequestLibrary.CleanName($"{_method.Trim().ToUpperInvariant()} {Uri.UnescapeDataString(text)}");
    }

    /// <summary>Builds the request from the fields, filling in variables. Null with an explanation when something is wrong.</summary>
    internal ComposedRequest? BuildRequest(out string? error)
    {
        var variables = Environments.Variables;
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        string Fill(string text) => ComposerVariables.Resolve(text, variables, missing, now);

        var method = _method.Trim().ToUpperInvariant();
        if (method.Length == 0 || method.Any(c => c <= ' ' || c >= 127))
        {
            error = "Enter a method such as GET or POST.";
            return null;
        }

        var urlText = Fill(_url.Trim());
        if (!TryCollectHeaders(Fill, out var headers, out error))
        {
            return null;
        }

        var bodyText = Fill(MessageText.NormalizeNewlines(_bodyText));
        if (missing.Count > 0)
        {
            var names = string.Join(", ", missing.Select(m => "{{" + m + "}}"));
            error = $"{(missing.Count == 1 ? "This variable is" : "These variables are")} not set in {Environments.ActiveName}: {names}. Use Variables to add {(missing.Count == 1 ? "it" : "them")}.";
            return null;
        }

        if (!urlText.Contains("://", StringComparison.Ordinal))
        {
            urlText = "https://" + urlText;
        }

        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(url.Host))
        {
            error = "Enter an http:// or https:// URL.";
            return null;
        }

        byte[]? body = bodyText.Length == 0 ? null : Encoding.UTF8.GetBytes(bodyText.Replace("\n", _lineEnding, StringComparison.Ordinal));
        if (body is not null && !headers.Contains("Content-Type"))
        {
            headers.Add("Content-Type", GuessContentType(bodyText));
        }

        error = null;
        return new ComposedRequest(method, url, headers, body);
    }

    /// <summary>
    /// A captured request, ready to send again unchanged (Ctrl+R in the session list). Null when it cannot
    /// be: a body that was too large to keep in full, or not an http(s) URL.
    /// </summary>
    internal static ComposedRequest? FromCapturedRequest(RequestInfo request, out string? error)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Url.Scheme != Uri.UriSchemeHttp && request.Url.Scheme != Uri.UriSchemeHttps)
        {
            error = "only http and https requests can be sent again.";
            return null;
        }

        if (request.Body is { } captured && captured.TotalBytes > captured.StoredBytes)
        {
            error = "its body was too large to keep in full.";
            return null;
        }

        var headers = new HttpHeaderList(request.Headers.Where(h => !SkippedWhenCopying.Contains(h.Name) || string.Equals(h.Name, "Host", StringComparison.OrdinalIgnoreCase)));
        var body = request.Body is { TotalBytes: > 0 } b ? b.ToArray() : null;
        error = null;
        return new ComposedRequest(request.Method, request.Url, headers, body);
    }

    /// <summary>The request as it would be sent, for "Copy as cURL".</summary>
    internal string? BuildCurl(out string? error)
    {
        if (BuildRequest(out error) is not { } request)
        {
            return null;
        }

        var headers = request.Headers.Clone();
        if (!headers.Contains("Host"))
        {
            headers.Add("Host", request.Url.Authority);
        }

        var info = new RequestInfo(request.Method, request.Url, "HTTP/1.1", headers)
        {
            Body = request.Body is null ? null : CapturedBody.FromBytes(request.Body),
        };
        return CommandExporter.ToCurl(info);
    }

    // ------------------------------------------------------------ Loading and saving

    private void NewRequest()
    {
        Load("GET", "https://", [], string.Empty, path: null);
        Response.Clear("No response yet");
        ShowResult("New request. Paste a cURL command into the URL box to fill everything in at once.", isError: false);
    }

    private void Save()
    {
        if (_currentPath is null || Library.Library is not { } library)
        {
            SaveAsRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            library.Overwrite(_currentPath, ToSaved());
            IsModified = false;
            Library.Refresh();
            ShowResult($"Saved \"{CurrentName}\".", isError: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowResult($"Could not save: {ex.Message}", isError: true);
        }
    }

    private void CopyAsCurl()
    {
        if (BuildCurl(out var error) is not { } curl)
        {
            ShowResult(error!, isError: true);
            return;
        }

        try
        {
            _clipboard!.SetText(curl);
            ShowResult("Copied as cURL (with variables filled in).", isError: false);
        }
#pragma warning disable CA1031 // The clipboard can be locked by another app; report it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ShowResult($"Could not copy to the clipboard: {ex.Message}", isError: true);
        }
    }

    /// <summary>A saved request was renamed or deleted (or one of its folders): follow it.</summary>
    private void OnLibraryMoved(string oldPath, string? newPath, bool isFolder)
    {
        if (_currentPath is null)
        {
            return;
        }

        var affected = isFolder
            ? _currentPath.StartsWith(oldPath + "/", StringComparison.Ordinal)
            : string.Equals(_currentPath, oldPath, StringComparison.Ordinal);
        if (!affected)
        {
            return;
        }

        if (newPath is null)
        {
            CurrentPath = null; // keep the text; it is now an unsaved request
            IsModified = true;
        }
        else
        {
            CurrentPath = isFolder ? newPath + _currentPath[oldPath.Length..] : newPath;
        }
    }

    private SavedRequest ToSaved()
    {
        IEnumerable<SavedHeader> headers;
        if (_isBulkEdit && TryParseHeaderText(_headersText, out var rows, out _))
        {
            headers = rows.Select(r => new SavedHeader(r.Name, r.Value, r.Enabled));
        }
        else
        {
            headers = Headers.Where(h => !h.IsBlank).Select(h => new SavedHeader(h.Name.Trim(), h.Value.Trim(), h.Enabled));
        }

        return new SavedRequest
        {
            Method = _method.Trim().Length == 0 ? "GET" : _method.Trim().ToUpperInvariant(),
            Url = _url.Trim(),
            Headers = headers.ToList(),
            Body = MessageText.NormalizeNewlines(_bodyText),
        };
    }

    private void Load(string method, string url, IEnumerable<SavedHeader> headers, string body, string? path)
    {
        _loading++;
        try
        {
            Method = method;
            Url = url;
            _isBulkEdit = false;
            ReplaceRows([.. headers.Select(h => new HeaderRowViewModel(h.Name, h.Value, h.Enabled))]);
            _lineEnding = body.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            BodyText = body;
            CurrentPath = path;
            OnPropertyChanged(nameof(IsBulkEdit));
            OnPropertyChanged(nameof(IsTableEdit));
            OnPropertyChanged(nameof(HeadersText));
        }
        finally
        {
            _loading--;
        }

        Response.Clear("No response yet");
        IsModified = false;
    }

    // ------------------------------------------------------------ Headers

    private void ReplaceRows(IReadOnlyList<HeaderRowViewModel> rows)
    {
        _loading++;
        try
        {
            Headers.Clear();
            foreach (var row in rows)
            {
                Headers.Add(row);
            }
        }
        finally
        {
            _loading--;
        }

        MarkModified();
        OnPropertyChanged(nameof(HeadersTitle));
    }

    private void OnHeaderRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        MarkModified();
        OnPropertyChanged(nameof(HeadersTitle));
    }

    /// <summary>Enabled rows as "Name: value" lines, disabled ones as "# Name: value".</summary>
    private string FormatRows() =>
        string.Join("\n", Headers.Where(h => !h.IsBlank).Select(h => $"{(h.Enabled ? string.Empty : "# ")}{h.Name.Trim()}: {h.Value.Trim()}"));

    /// <summary>Parses header text; lines starting with # (or //) are disabled headers.</summary>
    private static bool TryParseHeaderText(string text, out List<HeaderRowViewModel> rows, out string? error)
    {
        rows = [];
        var number = 0;
        foreach (var raw in MessageText.SplitLines(text ?? string.Empty))
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var enabled = true;
            if (line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
            {
                enabled = false;
                line = line.TrimStart('#', '/').Trim();
                if (line.Length == 0)
                {
                    continue;
                }
            }

            if (!MessageText.TryParseHeaders([line], out var parsed, out error, number))
            {
                return false;
            }

            var header = parsed[0];
            rows.Add(new HeaderRowViewModel(header.Name, header.Value, enabled));
        }

        error = null;
        return true;
    }

    private bool TryCollectHeaders(Func<string, string> fill, out HttpHeaderList headers, out string? error)
    {
        headers = new HttpHeaderList();
        List<HeaderRowViewModel> rows;
        if (_isBulkEdit)
        {
            if (!TryParseHeaderText(_headersText, out rows, out error))
            {
                return false;
            }
        }
        else
        {
            rows = [.. Headers];
        }

        var number = 0;
        foreach (var row in rows)
        {
            number++;
            if (!row.Enabled || row.IsBlank)
            {
                continue;
            }

            var name = fill(row.Name.Trim());
            if (name.Length == 0 || name.Any(c => c <= ' ' || c >= 127 || "()<>@,;:\\\"/[]?={}".Contains(c, StringComparison.Ordinal)))
            {
                error = $"Header {number.ToString(CultureInfo.InvariantCulture)}: \"{name}\" is not a valid header name.";
                return false;
            }

            var value = fill(row.Value.Trim());
            if (value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal))
            {
                error = $"Header {name}: the value cannot span lines.";
                return false;
            }

            headers.Add(name, value);
        }

        error = null;
        return true;
    }

    // ------------------------------------------------------------ Sending

    private async Task SendAsync()
    {
        if (BuildRequest(out var error) is not { } request)
        {
            ShowResult(error!, isError: true);
            return;
        }

        if (!_host.IsRunning)
        {
            ShowResult("Start capturing first: the Composer sends requests through Snoop.", isError: true);
            return;
        }

        using var sending = new CancellationTokenSource();
        _sending = sending;
        IsSending = true;
        ShowResult($"Sending {request.Method} {request.Url}...", isError: false);
        Response.Clear("Waiting for the response...");
        try
        {
            var result = await _host.SendComposedAsync(request, sending.Token).ConfigureAwait(true);
            ShowOutcome(result);
        }
        catch (OperationCanceledException)
        {
            ShowResult("Cancelled.", isError: true);
            Response.Clear("No response");
        }
        finally
        {
            _sending = null;
            IsSending = false;
        }
    }

    private void ShowOutcome(ComposerResult result)
    {
        LastSession = result.Session;
        if (result.Session is { } session)
        {
            if (session.Response is { } response)
            {
                Response.Load(response.Headers, response.Body, RawMessageFormatter.FormatResponse(response), []);
            }
            else
            {
                Response.Clear(session.Error ?? "No response");
            }

            var time = session.Timings.Duration is { } d ? $" · {d.TotalMilliseconds:0} ms" : string.Empty;
            var status = session.Response is { } r ? $"{r.StatusCode.ToString(CultureInfo.InvariantCulture)} {r.ReasonPhrase}" : "No response";
            ShowResult($"{status}{time} · session #{session.Id.ToString(CultureInfo.InvariantCulture)}", isError: session.Response is null || session.Response.StatusCode >= 400 || session.Error is not null);
            Sent?.Invoke(this, session);
            return;
        }

        Response.Clear(result.Error ?? "The response was not captured.");
        if (result.StatusCode is { } code)
        {
            ShowResult($"{code.ToString(CultureInfo.InvariantCulture)} {result.ReasonPhrase} · not captured (HTTPS decryption is off, so Snoop could not see inside)", isError: code >= 400);
        }
        else
        {
            ShowResult(result.Error ?? "No response.", isError: true);
        }
    }

    private void MarkModified()
    {
        if (_loading == 0)
        {
            IsModified = true;
        }
    }

    private void ShowResult(string text, bool isError)
    {
        ResultIsError = isError;
        ResultText = text;
    }

    private static string GuessContentType(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            return "application/json";
        }

        if (trimmed.StartsWith('<'))
        {
            return "application/xml";
        }

        return trimmed.Contains('=', StringComparison.Ordinal) && !trimmed.Contains(' ', StringComparison.Ordinal) && !trimmed.Contains('\n', StringComparison.Ordinal)
            ? "application/x-www-form-urlencoded"
            : "text/plain; charset=utf-8";
    }
}
