using System.Collections.ObjectModel;
using System.Globalization;
using Snoop.Presentation.Editing;
using Snoop.Presentation.Mvvm;
using Snoop.Presentation.Services;
using Snoop.Proxy.Interception;

namespace Snoop.Presentation.ViewModels;

/// <summary>
/// The Breakpoints tab: switches to pause requests and/or responses, the list of paused exchanges,
/// and an editor for the selected one. Proxy events are marshalled to the UI thread.
/// </summary>
public sealed class BreakpointsViewModel : ObservableObject
{
    private readonly BreakpointController _controller;
    private readonly IUiDispatcher _dispatcher;
    private int _selectedIndex = -1;
    private string _headText = string.Empty;
    private string _bodyText = string.Empty;
    private string? _error;
    private bool _updatingList;
    private string _filterText;

    public BreakpointsViewModel(BreakpointController controller, IUiDispatcher dispatcher)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _filterText = _controller.Filter;
        ResumeCommand = new RelayCommand(Resume, () => Selected is not null);
        AbortCommand = new RelayCommand(Abort, () => Selected is not null);
        ResumeAllCommand = new RelayCommand(ResumeAll, () => Pending.Count > 0);

        _controller.Paused += (_, exchange) => _dispatcher.Post(() => OnPaused(exchange));
        _controller.Resolved += (_, exchange) => _dispatcher.Post(() => OnResolved(exchange));
        foreach (var exchange in _controller.Pending)
        {
            OnPaused(exchange);
        }
    }

    /// <summary>Raised on the UI thread when something pauses, so the window can show this tab.</summary>
    public event EventHandler? PausedArrived;

    public ObservableCollection<PausedItemViewModel> Pending { get; } = [];

    public RelayCommand ResumeCommand { get; }

    public RelayCommand AbortCommand { get; }

    public RelayCommand ResumeAllCommand { get; }

    /// <summary>Pause every request (whose URL contains <see cref="Filter"/>) before it goes to the server.</summary>
    public bool BreakOnRequests
    {
        get => _controller.BreakOnRequests;
        set
        {
            if (_controller.BreakOnRequests != value)
            {
                _controller.BreakOnRequests = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HeaderText));
                OnPropertyChanged(nameof(IsArmed));
            }
        }
    }

    /// <summary>Pause every response (whose URL contains <see cref="Filter"/>) before it goes to the app.</summary>
    public bool BreakOnResponses
    {
        get => _controller.BreakOnResponses;
        set
        {
            if (_controller.BreakOnResponses != value)
            {
                _controller.BreakOnResponses = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HeaderText));
                OnPropertyChanged(nameof(IsArmed));
            }
        }
    }

    /// <summary>Only URLs containing this text pause. Empty pauses everything.</summary>
    public string Filter
    {
        get => _filterText;
        set
        {
            // Keep exactly what was typed (the text box would otherwise lose trailing spaces mid-edit).
            if (SetProperty(ref _filterText, value ?? string.Empty))
            {
                _controller.Filter = _filterText;
            }
        }
    }

    /// <summary>True when the switches will pause traffic.</summary>
    public bool IsArmed => BreakOnRequests || BreakOnResponses;

    /// <summary>Tab title, e.g. "Breakpoints (2 paused)".</summary>
    public string HeaderText => Pending.Count > 0
        ? $"Breakpoints ({Pending.Count.ToString(CultureInfo.InvariantCulture)} paused)"
        : IsArmed ? "Breakpoints (on)" : "Breakpoints";

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            // While the list changes, the list control reports interim selections; ignore them.
            var index = value >= -1 && value < Pending.Count ? value : -1;
            if (_updatingList || index == _selectedIndex)
            {
                return;
            }

            SaveDraft();
            _selectedIndex = index;
            OnPropertyChanged();
            LoadEditor();
        }
    }

    public PausedItemViewModel? Selected => _selectedIndex >= 0 && _selectedIndex < Pending.Count ? Pending[_selectedIndex] : null;

    public bool HasSelection => Selected is not null;

    public bool HasNoSelection => Selected is null;

    /// <summary>Start line and headers of the selected message, editable.</summary>
    public string HeadText
    {
        get => _headText;
        set => SetProperty(ref _headText, value ?? string.Empty);
    }

    /// <summary>Body of the selected message as text, editable when <see cref="IsBodyEditable"/>.</summary>
    public string BodyText
    {
        get => _bodyText;
        set => SetProperty(ref _bodyText, value ?? string.Empty);
    }

    public bool IsBodyEditable => Selected?.Body.IsEditable ?? false;

    public bool IsBodyReadOnly => !IsBodyEditable;

    public string BodyNote => Selected?.Body.Note ?? string.Empty;

    public bool HasBodyNote => BodyNote.Length > 0;

    /// <summary>For response breakpoints: the request, for reference.</summary>
    public string ContextText => Selected?.ContextText ?? string.Empty;

    public bool HasContext => ContextText.Length > 0;

    public string EditorTitle => Selected?.Title ?? "Nothing is paused";

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(ErrorText));
            }
        }
    }

    public bool HasError => _error is not null;

    public string ErrorText => _error ?? string.Empty;

    private void OnPaused(PausedExchange exchange)
    {
        if (exchange.IsResolved || Pending.Any(p => ReferenceEquals(p.Exchange, exchange)))
        {
            return;
        }

        _updatingList = true;
        try
        {
            Pending.Add(new PausedItemViewModel(exchange));
        }
        finally
        {
            _updatingList = false;
        }

        if (_selectedIndex < 0)
        {
            SelectedIndex = Pending.Count - 1;
        }

        RefreshList();
        PausedArrived?.Invoke(this, EventArgs.Empty);
    }

    private void OnResolved(PausedExchange exchange)
    {
        var index = -1;
        for (var i = 0; i < Pending.Count; i++)
        {
            if (ReferenceEquals(Pending[i].Exchange, exchange))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        var wasSelected = index == _selectedIndex;
        if (!wasSelected)
        {
            SaveDraft();
        }

        _updatingList = true;
        try
        {
            Pending.RemoveAt(index);
        }
        finally
        {
            _updatingList = false;
        }

        if (wasSelected)
        {
            _selectedIndex = Math.Min(index, Pending.Count - 1);
        }
        else if (index < _selectedIndex)
        {
            _selectedIndex--;
        }

        OnPropertyChanged(nameof(SelectedIndex));
        LoadEditor();
        RefreshList();
    }

    private void Resume()
    {
        if (Selected is not { } item)
        {
            return;
        }

        SaveDraft();
        if (!item.TryApplyEdits(out var error))
        {
            Error = error;
            return;
        }

        Error = null;
        item.Exchange.Resume();
    }

    private void Abort()
    {
        Error = null;
        Selected?.Exchange.Abort();
    }

    private void ResumeAll()
    {
        // The selected exchange keeps its edits when they are valid; the others go on unchanged.
        if (Selected is { } item)
        {
            SaveDraft();
            if (item.TryApplyEdits(out var error))
            {
                item.Exchange.Resume();
            }
            else
            {
                Error = error;
                return;
            }
        }

        Error = null;
        _controller.ResumeAll();
    }

    private void SaveDraft()
    {
        if (Selected is { } item)
        {
            item.HeadDraft = _headText;
            item.BodyDraft = _bodyText;
        }
    }

    private void LoadEditor()
    {
        var item = Selected;
        Error = null;
        _headText = item?.HeadDraft ?? string.Empty;
        _bodyText = item?.BodyDraft ?? string.Empty;
        OnPropertyChanged(nameof(HeadText));
        OnPropertyChanged(nameof(BodyText));
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasNoSelection));
        OnPropertyChanged(nameof(IsBodyEditable));
        OnPropertyChanged(nameof(IsBodyReadOnly));
        OnPropertyChanged(nameof(BodyNote));
        OnPropertyChanged(nameof(HasBodyNote));
        OnPropertyChanged(nameof(ContextText));
        OnPropertyChanged(nameof(HasContext));
        OnPropertyChanged(nameof(EditorTitle));
        ResumeCommand.NotifyCanExecuteChanged();
        AbortCommand.NotifyCanExecuteChanged();
    }

    private void RefreshList()
    {
        OnPropertyChanged(nameof(HeaderText));
        ResumeAllCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>One paused exchange, with the user's unsaved edits.</summary>
public sealed class PausedItemViewModel
{
    public PausedItemViewModel(PausedExchange exchange)
    {
        Exchange = exchange ?? throw new ArgumentNullException(nameof(exchange));
        if (exchange.Stage == BreakpointStage.Request)
        {
            HeadDraft = MessageText.FormatRequestHead(exchange.Request);
            Body = MessageText.DescribeBody(exchange.Request.Body, exchange.Request.Headers);
        }
        else
        {
            var response = exchange.Response!;
            HeadDraft = MessageText.FormatResponseHead(response);
            Body = MessageText.DescribeBody(response.Body, response.Headers);
            ContextText = $"{exchange.Request.Method} {exchange.Request.Url.AbsoluteUri}";
        }

        BodyDraft = Body.Text;
    }

    public PausedExchange Exchange { get; }

    public string Title => $"#{Exchange.Session.Id.ToString(CultureInfo.InvariantCulture)} {StageText}: {Exchange.Request.Method} {Exchange.Request.Url.AbsoluteUri}";

    public string StageText => Exchange.Stage == BreakpointStage.Request ? "Request" : "Response";

    public string Url => Exchange.Request.Url.AbsoluteUri;

    public string Method => Exchange.Request.Method;

    public string PausedAtText => Exchange.PausedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>How the body was shown when paused.</summary>
    public BodyText Body { get; }

    /// <summary>For responses: the request line, for reference.</summary>
    public string ContextText { get; } = string.Empty;

    public string HeadDraft { get; set; }

    public string BodyDraft { get; set; }

    /// <summary>Writes the drafts into the paused message. Nothing changes when they are invalid.</summary>
    public bool TryApplyEdits(out string? error)
    {
        if (Exchange.Stage == BreakpointStage.Request)
        {
            var request = Exchange.Request.Clone();
            if (!MessageText.TryApplyRequestHead(HeadDraft, request, out error))
            {
                return false;
            }

            request.Body = MessageText.ApplyBody(Exchange.Request.Body, Body, BodyDraft, request.Headers);
            Exchange.Request.Method = request.Method;
            Exchange.Request.Url = request.Url;
            Exchange.Request.Headers = request.Headers;
            Exchange.Request.Body = request.Body;
            return true;
        }

        var target = Exchange.Response!;
        var response = target.Clone();
        if (!MessageText.TryApplyResponseHead(HeadDraft, response, out error))
        {
            return false;
        }

        response.Body = MessageText.ApplyBody(target.Body, Body, BodyDraft, response.Headers) ?? [];
        target.StatusCode = response.StatusCode;
        target.ReasonPhrase = response.ReasonPhrase;
        target.Headers = response.Headers;
        target.Body = response.Body;
        return true;
    }
}
