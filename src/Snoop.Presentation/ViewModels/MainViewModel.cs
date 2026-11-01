using System.Globalization;
using System.Net.Sockets;
using Snoop.Core.Diagnostics;
using Snoop.Core.Snapshots;
using Snoop.Hosting;
using Snoop.Inspection.Export;
using Snoop.Inspection.Filtering;
using Snoop.Presentation.Mvvm;
using Snoop.Presentation.Services;
using Snoop.Presentation.Settings;

namespace Snoop.Presentation.ViewModels;

/// <summary>
/// The main window: capture controls, filters, the session grid and the inspector.
/// All methods are called on the UI thread; host events are marshalled there.
/// </summary>
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly CaptureHost _host;
    private readonly IUiDispatcher _dispatcher;
    private readonly IClipboardService _clipboard;
    private readonly IFileSaveService _files;
    private readonly IFileOpenService? _openFiles;
    private readonly string? _settingsPath;
    private AppSettings _settings;
    private string _searchText = string.Empty;
    private bool _hideTunnels;
    private bool _hideStatic;
    private bool _onlyErrors;
    private bool _searchBodies;
    private string _onlyHost = string.Empty;
    private string _onlyProcess = string.Empty;
    private string? _message;
    private bool _messageIsError;
    private bool _isRootTrusted;
    private int _selectedTabIndex;
    private int _lastInspectView;
    private string _newFilterName = string.Empty;
    private SessionRowViewModel? _markedForCompare;

    public MainViewModel(
        CaptureHost host,
        IUiDispatcher dispatcher,
        IClipboardService clipboard,
        IFileSaveService files,
        AppSettings? settings = null,
        string? settingsPath = null,
        IFileOpenService? openFiles = null,
        string? rulesPath = null,
        string? composerDataDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _host = host;
        _dispatcher = dispatcher;
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _openFiles = openFiles;
        _settingsPath = settingsPath;
        _settings = settings ?? new AppSettings();

        // Saved requests go in <dir>\requests\, variables in <dir>\environments.json (none in tests).
        Composer = new ComposerViewModel(
            host,
            clipboard,
            composerDataDirectory is null ? null : Path.Combine(composerDataDirectory, "requests"),
            composerDataDirectory is null ? null : Path.Combine(composerDataDirectory, "environments.json"));
        Rules = new RulesViewModel(host.Rules, rulesPath, openFiles, SetMessage);
        Breakpoints = new BreakpointsViewModel(host.Breakpoints, dispatcher);
        Breakpoints.PausedArrived += (_, _) => SelectedTabIndex = BreakpointsTab;
        Breakpoints.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BreakpointsViewModel.IsArmed))
            {
                OnPropertyChanged(nameof(StatusText));
            }

            if (e.PropertyName is nameof(BreakpointsViewModel.HeaderText))
            {
                OnPropertyChanged(nameof(BreakpointsBadge));
            }
        };
        Rules.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RulesViewModel.HeaderText))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(RulesBadge));
            }
        };
        _hideTunnels = _settings.HideTunnels;
        _searchBodies = _settings.SearchBodies;

        Sessions = new SessionListViewModel(host.Sessions, dispatcher) { AutoScroll = _settings.AutoScroll };
        Sessions.Columns.Load(_settings.Columns);
        if (_settings.SortColumn is { } sortColumn && Enum.TryParse<SessionColumn>(sortColumn, ignoreCase: true, out var column))
        {
            Sessions.Sort = new SessionSort(column, _settings.SortDescending);
        }

        Sessions.Columns.Changed += (_, _) => SaveSettings(_settings with { Columns = Sessions.Columns.ToSettings() });
        foreach (var filter in _settings.SavedFilters)
        {
            SavedFilters.Add(filter);
        }

        Sessions.SelectionChanged += (_, _) => OnSelectionChanged();
        Sessions.SelectedSessionUpdated += (_, _) =>
        {
            Inspector.Refresh();
            WebSocket.Refresh();
        };
        Sessions.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionListViewModel.Sort))
            {
                var sort = Sessions.Sort;
                SaveSettings(_settings with { SortColumn = sort.IsDefault ? null : sort.Column.ToString(), SortDescending = sort.Descending });
            }

            if (e.PropertyName is nameof(SessionListViewModel.TotalCount) or nameof(SessionListViewModel.VisibleCount) or nameof(SessionListViewModel.Rows) or "")
            {
                OnPropertyChanged(nameof(CountText));
                RaiseEmptyState();
            }

            if (e.PropertyName == nameof(SessionListViewModel.SelectedRows))
            {
                OnPropertyChanged(nameof(CountText));
                OnPropertyChanged(nameof(RemoveText));
                ExportSelectedHarCommand?.NotifyCanExecuteChanged();
                ResendCommand?.NotifyCanExecuteChanged();
                ExportSelectedTextCommand?.NotifyCanExecuteChanged();
                SaveSelectedSnapshotCommand?.NotifyCanExecuteChanged();
            }
        };
        ApplyFilter();

        ToggleCaptureCommand = new AsyncRelayCommand(ToggleCaptureAsync, ShowError);
        ClearCommand = new RelayCommand(() => Sessions.Clear());
        TrustRootCommand = new RelayCommand(TrustRoot, () => _host.IsRootTrustSupported);
        CopyUrlCommand = new RelayCommand(() => CopySelected(r => r.Url, "\n"), HasSelection);
        CopyCurlCommand = new RelayCommand(() => CopySelected(r => CommandExporter.ToCurl(r.Session.Request), "\n\n"), HasSelection);
        CopyPowerShellCommand = new RelayCommand(() => CopySelected(r => CommandExporter.ToPowerShell(r.Session.Request), "\n\n"), HasSelection);
        ResendCommand = new AsyncRelayCommand(ResendSelectedAsync, ShowError, () => ResendableRows().Count > 0);
        RemoveSelectedCommand = new RelayCommand(RemoveSelected, HasSelection);
        ExportSelectedHarCommand = new AsyncRelayCommand(() => ExportHarAsync(redact: false, selectedOnly: true), ShowError, HasSelection);
        ExportSelectedTextCommand = new AsyncRelayCommand(() => ExportTextAsync(selectedOnly: true), ShowError, HasSelection);
        SaveSelectedSnapshotCommand = new AsyncRelayCommand(() => SaveSnapshotAsync(selectedOnly: true), ShowError, HasSelection);
        ExportTextCommand = new AsyncRelayCommand(() => ExportTextAsync(selectedOnly: false), ShowError);
        ShowOnlyHostCommand = new RelayCommand(() => IsOnlyHost = true, HasSelection);
        ShowOnlyProcessCommand = new RelayCommand(() => IsOnlyProcess = true, () => Sessions.SelectedRow?.Session.ClientProcess is not null);
        ClearScopeCommand = new RelayCommand(ClearScope);
        SkipHostCommand = new RelayCommand(SkipSelectedHost, () => HasSelection() && _host.IsRunning);
        ExportHarCommand = new AsyncRelayCommand(() => ExportHarAsync(redact: false, selectedOnly: false), ShowError);
        ExportRedactedHarCommand = new AsyncRelayCommand(() => ExportHarAsync(redact: true, selectedOnly: false), ShowError);
        DismissMessageCommand = new RelayCommand(() => SetMessage(null, false));
        SaveSnapshotCommand = new AsyncRelayCommand(() => SaveSnapshotAsync(selectedOnly: false), ShowError);
        LoadSnapshotCommand = new AsyncRelayCommand(OpenCaptureFileAsync, ShowError, () => _openFiles is not null);
        EditInComposerCommand = new RelayCommand(EditInComposer, () => Sessions.SelectedRow?.Session.Kind == Core.Sessions.SessionKind.Http);
        CreateRuleCommand = new RelayCommand(CreateRuleFromSelected, () => Sessions.SelectedRow?.Session.Kind == Core.Sessions.SessionKind.Http);
        NewComposerRequestCommand = new RelayCommand(() => SelectedTabIndex = ComposerTab);
        RefreshTimelineCommand = new RelayCommand(() => Timeline.Build(Sessions.VisibleSessions));
        MarkForCompareCommand = new RelayCommand(MarkForCompare, HasSelection);
        CompareWithMarkedCommand = new RelayCommand(CompareWithMarked, () => HasSelection() && _markedForCompare is not null && !ReferenceEquals(_markedForCompare, Sessions.SelectedRow));
        ApplySettingsCommand = new AsyncRelayCommand(ApplySettingsAsync, ShowError);
        RevertSettingsCommand = new RelayCommand(() => Settings.Load(_settings));
        UntrustRootCommand = new RelayCommand(UntrustRoot, () => _host.IsRootTrustSupported);
        ExportRootCommand = new AsyncRelayCommand(ExportRootAsync, ShowError);
        ResetColumnsCommand = new RelayCommand(() => Sessions.Columns.Reset());
        SaveFilterCommand = new RelayCommand(SaveCurrentFilter);
        Settings.Load(_settings);
        OpenSettingsCommand = new RelayCommand(() =>
        {
            if (!Settings.IsDirty)
            {
                Settings.Load(_settings);
            }

            SettingsRequested?.Invoke(this, EventArgs.Empty);
        });

        _host.StateChanged += (_, _) => _dispatcher.Post(RefreshCaptureState);
    }

    /// <summary>Tab positions in the right-hand pane.</summary>
    public const int InspectorsTab = 0;
    public const int OverviewTab = 1;
    public const int WebSocketTab = 2;
    public const int ComposerTab = 3;
    public const int RulesTab = 4;
    public const int BreakpointsTab = 5;
    public const int TimelineTab = 6;
    public const int CompareTab = 7;

    public SessionListViewModel Sessions { get; }

    public InspectorViewModel Inspector { get; } = new();

    public ComposerViewModel Composer { get; }

    public RulesViewModel Rules { get; }

    public BreakpointsViewModel Breakpoints { get; }

    public WebSocketViewModel WebSocket { get; } = new();

    public TimelineViewModel Timeline { get; } = new();

    public CompareViewModel Compare { get; } = new();

    public SettingsViewModel Settings { get; } = new();

    /// <summary>Filters saved by name, shown in the Filters menu.</summary>
    public System.Collections.ObjectModel.ObservableCollection<SavedFilter> SavedFilters { get; } = [];

    /// <summary>Name for "save current filter".</summary>
    public string NewFilterName
    {
        get => _newFilterName;
        set => SetProperty(ref _newFilterName, value ?? string.Empty);
    }

    /// <summary>The session marked for Compare, shown in the status of the menu item.</summary>
    public string MarkedForCompareText => _markedForCompare is { } marked ? $"Compare with marked (#{marked.Id})" : "Compare with marked";

    public RelayCommand RefreshTimelineCommand { get; }

    public RelayCommand MarkForCompareCommand { get; }

    public RelayCommand CompareWithMarkedCommand { get; }

    public AsyncRelayCommand ApplySettingsCommand { get; }

    public RelayCommand RevertSettingsCommand { get; }

    public RelayCommand UntrustRootCommand { get; }

    public AsyncRelayCommand ExportRootCommand { get; }

    public RelayCommand ResetColumnsCommand { get; }

    public RelayCommand SaveFilterCommand { get; }

    /// <summary>The selected tab of the right-hand pane (see the *Tab constants).</summary>
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (SetProperty(ref _selectedTabIndex, value is < 0 or > CompareTab ? 0 : value))
            {
                if (_selectedTabIndex <= WebSocketTab)
                {
                    _lastInspectView = _selectedTabIndex;
                }

                OnPropertyChanged(nameof(IsInspectorTab));
                OnPropertyChanged(nameof(IsToolMode));
                OnPropertyChanged(nameof(SelectedToolIndex));
                OnPropertyChanged(nameof(InspectViewIndex));
                OnPropertyChanged(nameof(CardTitle));
                RaiseTabVisibility();
                if (_selectedTabIndex == TimelineTab)
                {
                    Timeline.Build(Sessions.VisibleSessions);
                }
            }
        }
    }

    /// <summary>True while the Inspectors or Overview tab is shown (the selected session's title is shown above them).</summary>
    public bool IsInspectorTab => _selectedTabIndex <= WebSocketTab;

    // ------------------------------------------------------------ Tool rail and the Inspect views
    //
    // The details card has a tool rail: Inspect, Composer, Rules, Breakpoints, Timeline, Compare. Inspect
    // has three views of the selected session (Request & Response, Overview, WebSocket). Both map onto
    // SelectedTabIndex, so there is a single source of truth.

    /// <summary>Tool names, in rail order (for headings, tooltips and screen readers).</summary>
    public static IReadOnlyList<string> ToolNames { get; } = new[] { "Inspect", "Composer", "Rules", "Breakpoints", "Timeline", "Compare" };

    /// <summary>Selected rail item: 0 Inspect, then the tools in <see cref="ToolNames"/> order.</summary>
    public int SelectedToolIndex
    {
        get => _selectedTabIndex <= WebSocketTab ? 0 : _selectedTabIndex - WebSocketTab;
        set
        {
            if (value < 0 || value >= ToolNames.Count || value == SelectedToolIndex)
            {
                return;
            }

            SelectedTabIndex = value == 0 ? (_lastInspectView == WebSocketTab && !IsWebSocketSelected ? InspectorsTab : _lastInspectView) : value + WebSocketTab;
        }
    }

    /// <summary>Selected Inspect view: 0 Request &amp; Response, 1 Overview, 2 WebSocket (the last one shown while another tool is open).</summary>
    public int InspectViewIndex
    {
        get => _selectedTabIndex <= WebSocketTab ? _selectedTabIndex : _lastInspectView;
        set
        {
            if (value is >= InspectorsTab and <= WebSocketTab && (value != WebSocketTab || IsWebSocketSelected))
            {
                SelectedTabIndex = value;
            }
        }
    }

    /// <summary>True while a tool (not Inspect) fills the card.</summary>
    public bool IsToolMode => _selectedTabIndex > WebSocketTab;

    /// <summary>Heading of the card: the tool's name (Inspect shows the session title instead).</summary>
    public string CardTitle => ToolNames[SelectedToolIndex];

    /// <summary>Number on the Rules rail icon: rules switched on (0 hides it).</summary>
    public int RulesBadge => Rules.Items.Count(r => r.Enabled && !r.HasError);

    /// <summary>Number on the Breakpoints rail icon: exchanges paused (0 hides it).</summary>
    public int BreakpointsBadge => Breakpoints.Pending.Count;

    /// <summary>Shows the selected session (double-click in the list): back to Inspect from any tool.</summary>
    public void ShowInspector()
    {
        if (IsToolMode)
        {
            SelectedToolIndex = 0;
        }
    }

    // One flag per tab, for showing that tab's content (the tab strip itself is not a selector with an index).
    public bool ShowInspectTab => _selectedTabIndex == InspectorsTab;

    public bool ShowOverviewTab => _selectedTabIndex == OverviewTab;

    public bool ShowWebSocketTab => _selectedTabIndex == WebSocketTab;

    public bool ShowComposerTab => _selectedTabIndex == ComposerTab;

    public bool ShowRulesTab => _selectedTabIndex == RulesTab;

    public bool ShowBreakpointsTab => _selectedTabIndex == BreakpointsTab;

    public bool ShowTimelineTab => _selectedTabIndex == TimelineTab;

    public bool ShowCompareTab => _selectedTabIndex == CompareTab;

    /// <summary>The selected session is a WebSocket connection (its tab is only offered then).</summary>
    public bool IsWebSocketSelected => Sessions.SelectedRow?.Session.WebSocket is not null;

    /// <summary>Nothing to show in the grid; <see cref="EmptyGridText"/> says why.</summary>
    public bool IsGridEmpty => Sessions.VisibleCount == 0;

    public string EmptyGridTitle => Sessions.TotalCount > 0 ? "No matching sessions" : IsCapturing ? "Waiting for traffic" : "Not capturing";

    public string EmptyGridText => Sessions.TotalCount > 0
        ? "Nothing matches the search and filters. Clear the search or change the filter."
        : IsCapturing
            ? "Browse a site or run an app. Requests appear here as they happen."
            : "Press Start to capture this PC's HTTP and HTTPS traffic.";

    /// <summary>Raised when the user asks for Settings; the window shows them.</summary>
    public event EventHandler? SettingsRequested;

    public RelayCommand OpenSettingsCommand { get; private set; } = null!;

    private void RaiseTabVisibility()
    {
        OnPropertyChanged(nameof(ShowInspectTab));
        OnPropertyChanged(nameof(ShowOverviewTab));
        OnPropertyChanged(nameof(ShowWebSocketTab));
        OnPropertyChanged(nameof(ShowComposerTab));
        OnPropertyChanged(nameof(ShowRulesTab));
        OnPropertyChanged(nameof(ShowBreakpointsTab));
        OnPropertyChanged(nameof(ShowTimelineTab));
        OnPropertyChanged(nameof(ShowCompareTab));
    }

    public AsyncRelayCommand SaveSnapshotCommand { get; }

    public AsyncRelayCommand LoadSnapshotCommand { get; }

    public RelayCommand EditInComposerCommand { get; }

    /// <summary>Sends the selected request(s) again exactly as captured (Ctrl+R). The copies are new sessions.</summary>
    public AsyncRelayCommand ResendCommand { get; }

    public RelayCommand CreateRuleCommand { get; }

    public RelayCommand NewComposerRequestCommand { get; }

    public AsyncRelayCommand ToggleCaptureCommand { get; }

    public RelayCommand ClearCommand { get; }

    public RelayCommand TrustRootCommand { get; }

    public RelayCommand CopyUrlCommand { get; }

    public RelayCommand CopyCurlCommand { get; }

    public RelayCommand CopyPowerShellCommand { get; }

    public RelayCommand RemoveSelectedCommand { get; }

    public RelayCommand SkipHostCommand { get; }

    public AsyncRelayCommand ExportHarCommand { get; }

    public AsyncRelayCommand ExportRedactedHarCommand { get; }

    /// <summary>Plain-text dump of the shown sessions.</summary>
    public AsyncRelayCommand ExportTextCommand { get; }

    public AsyncRelayCommand ExportSelectedHarCommand { get; }

    public AsyncRelayCommand ExportSelectedTextCommand { get; }

    public AsyncRelayCommand SaveSelectedSnapshotCommand { get; }

    /// <summary>Show only the selected session's host.</summary>
    public RelayCommand ShowOnlyHostCommand { get; }

    /// <summary>Show only the selected session's app.</summary>
    public RelayCommand ShowOnlyProcessCommand { get; }

    /// <summary>Stop limiting the list to one host or app.</summary>
    public RelayCommand ClearScopeCommand { get; }

    /// <summary>"Remove (Del)", or "Remove 3 sessions (Del)" with several selected.</summary>
    public string RemoveText => Sessions.SelectedCount > 1
        ? $"Remove {Sessions.SelectedCount.ToString("N0", CultureInfo.InvariantCulture)} sessions (Del)"
        : "Remove (Del)";

    public RelayCommand DismissMessageCommand { get; }

    // ------------------------------------------------------------ Capture state

    public bool IsCapturing => _host.IsRunning;

    public string CaptureButtonText => IsCapturing ? "Stop capture" : "Start capture";

    /// <summary>Whether Snoop is the Windows system proxy. Setting it switches the system proxy immediately.</summary>
    public bool IsSystemProxyOn
    {
        get => _host.IsSystemProxyEnabled;
        set
        {
            if (value == _host.IsSystemProxyEnabled)
            {
                return;
            }

            try
            {
                if (value && !_host.IsRunning)
                {
                    SetMessage("Start capturing first, then turn on the system proxy.", isError: true);
                }
                else
                {
                    _host.SetSystemProxy(value);
                    SaveSettings(_settings with { UseSystemProxy = value });
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                ShowError(ex);
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool IsSystemProxySupported => _host.IsSystemProxySupported;

    /// <summary>Decrypt HTTPS. Changing it while capturing restarts the capture.</summary>
    public bool DecryptHttps
    {
        get => _settings.DecryptHttps;
        set
        {
            if (value == _settings.DecryptHttps)
            {
                return;
            }

            SaveSettings(_settings with { DecryptHttps = value });
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowTrustPrompt));
            if (_host.IsRunning)
            {
                _ = RestartAsync();
            }
        }
    }

    public bool IsRootTrusted
    {
        get => _isRootTrusted;
        private set
        {
            if (SetProperty(ref _isRootTrusted, value))
            {
                OnPropertyChanged(nameof(ShowTrustPrompt));
            }
        }
    }

    /// <summary>True when decryption is wanted but the root is not trusted yet.</summary>
    public bool ShowTrustPrompt => _host.IsRootTrustSupported && DecryptHttps && !_isRootTrusted;

    public string StatusText
    {
        get
        {
            if (!_host.IsRunning)
            {
                return "Not capturing";
            }

            var parts = new List<string> { $"Listening on {_host.Endpoint}" };
            parts.Add(_host.IsDecrypting ? "HTTPS decryption on" : "HTTPS decryption off");
            parts.Add(_host.IsSystemProxyEnabled ? "system proxy on" : "system proxy off");
            var skipped = _host.SkippedHosts.Count;
            if (skipped > 0)
            {
                parts.Add($"{skipped} host(s) not decrypted (apps rejected the certificate)");
            }

            var rules = _host.Rules.EnabledCount;
            if (rules > 0)
            {
                parts.Add(rules == 1 ? "1 rule on" : $"{rules} rules on");
            }

            if (Breakpoints.IsArmed)
            {
                parts.Add("breakpoints on");
            }

            return string.Join(" · ", parts);
        }
    }

    public string CountText
    {
        get
        {
            var text = Sessions.VisibleCount == Sessions.TotalCount
                ? $"{Sessions.TotalCount.ToString("N0", CultureInfo.InvariantCulture)} sessions"
                : $"{Sessions.VisibleCount.ToString("N0", CultureInfo.InvariantCulture)} of {Sessions.TotalCount.ToString("N0", CultureInfo.InvariantCulture)} sessions";
            return Sessions.SelectedCount > 1
                ? $"{text} · {Sessions.SelectedCount.ToString("N0", CultureInfo.InvariantCulture)} selected"
                : text;
        }
    }

    // ------------------------------------------------------------ Messages

    /// <summary>A message for the info bar, or null.</summary>
    public string? Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => _message is not null;

    public bool MessageIsError
    {
        get => _messageIsError;
        private set => SetProperty(ref _messageIsError, value);
    }

    // ------------------------------------------------------------ Filters

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    public bool HideTunnels
    {
        get => _hideTunnels;
        set
        {
            if (SetProperty(ref _hideTunnels, value))
            {
                ApplyFilter();
                SaveSettings(_settings with { HideTunnels = value });
            }
        }
    }

    public bool HideStatic
    {
        get => _hideStatic;
        set
        {
            if (SetProperty(ref _hideStatic, value))
            {
                ApplyFilter();
            }
        }
    }

    public bool OnlyErrors
    {
        get => _onlyErrors;
        set
        {
            if (SetProperty(ref _onlyErrors, value))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>Let the search match text inside request and response bodies.</summary>
    public bool SearchBodies
    {
        get => _searchBodies;
        set
        {
            if (SetProperty(ref _searchBodies, value))
            {
                ApplyFilter();
                SaveSettings(_settings with { SearchBodies = value });
            }
        }
    }

    /// <summary>Only sessions to one host are shown. Turning it on takes the selected session's host.</summary>
    public bool IsOnlyHost
    {
        get => _onlyHost.Length > 0;
        set
        {
            if (value == IsOnlyHost)
            {
                return;
            }

            if (value)
            {
                var host = Sessions.SelectedRow?.Session.Request.Url.Host;
                if (string.IsNullOrEmpty(host))
                {
                    SetMessage("Select a session first; the list then shows only that session's host.", isError: false);
                    _dispatcher.Post(() => OnPropertyChanged(nameof(IsOnlyHost))); // un-tick the menu item
                    return;
                }

                _onlyHost = host;
            }
            else
            {
                _onlyHost = string.Empty;
            }

            RaiseScope();
            ApplyFilter();
        }
    }

    /// <summary>Only sessions from one app are shown. Turning it on takes the selected session's app.</summary>
    public bool IsOnlyProcess
    {
        get => _onlyProcess.Length > 0;
        set
        {
            if (value == IsOnlyProcess)
            {
                return;
            }

            if (value)
            {
                var name = Sessions.SelectedRow?.Session.ClientProcess?.Name;
                if (string.IsNullOrEmpty(name))
                {
                    SetMessage(Sessions.SelectedRow is null
                        ? "Select a session first; the list then shows only that session's app."
                        : "Snoop does not know which app sent the selected session.", isError: false);
                    _dispatcher.Post(() => OnPropertyChanged(nameof(IsOnlyProcess)));
                    return;
                }

                _onlyProcess = name;
            }
            else
            {
                _onlyProcess = string.Empty;
            }

            RaiseScope();
            ApplyFilter();
        }
    }

    public string OnlyHostMenuText => IsOnlyHost ? $"Only {_onlyHost}" : "Only the selected session's host";

    public string OnlyProcessMenuText => IsOnlyProcess ? $"Only {_onlyProcess}" : "Only the selected session's app";

    /// <summary>True when the list is limited to one host and/or app (shown as a chip next to the search box).</summary>
    public bool HasScope => IsOnlyHost || IsOnlyProcess;

    /// <summary>The chip's text, e.g. "api.example.com · chrome".</summary>
    public string ScopeText => string.Join(" · ", new[] { _onlyHost, _onlyProcess }.Where(t => t.Length > 0));

    public bool AutoScroll
    {
        get => Sessions.AutoScroll;
        set
        {
            if (Sessions.AutoScroll != value)
            {
                Sessions.AutoScroll = value;
                SaveSettings(_settings with { AutoScroll = value });
                OnPropertyChanged();
            }
        }
    }

    // ------------------------------------------------------------ Lifecycle

    /// <summary>Call once after the window opens: repairs a stale proxy setting, then starts capturing if configured.</summary>
    public async Task InitializeAsync()
    {
        if (_host.RecoverSystemProxy())
        {
            SetMessage("Snoop did not close cleanly last time; your system proxy setting has been restored.", isError: false);
        }

        RefreshTrust();
        Rules.Load();
        if (_settings.CaptureOnStartup)
        {
            await StartCaptureAsync().ConfigureAwait(true);
        }

        RefreshCaptureState();
    }

    /// <summary>Call when the window closes: stops capture and restores the system proxy.</summary>
    public async Task ShutdownAsync()
    {
        _host.RestoreSystemProxyNow();
        await _host.StopAsync().ConfigureAwait(true);
    }

    public async ValueTask DisposeAsync()
    {
        Sessions.Dispose();
        await _host.DisposeAsync().ConfigureAwait(true);
    }

    // ------------------------------------------------------------ Actions

    private async Task ToggleCaptureAsync()
    {
        if (_host.IsRunning)
        {
            await _host.StopAsync().ConfigureAwait(true);
        }
        else
        {
            await StartCaptureAsync().ConfigureAwait(true);
        }

        RefreshCaptureState();
    }

    private async Task StartCaptureAsync()
    {
        var settings = _settings.ToCaptureSettings() with { SetSystemProxy = _settings.UseSystemProxy && _host.IsSystemProxySupported };
        try
        {
            await _host.StartAsync(settings).ConfigureAwait(true);
        }
        catch (SocketException ex)
        {
            SetMessage($"Could not listen on port {settings.Port} or the ports after it: {ex.Message}", isError: true);
        }
    }

    private async Task RestartAsync()
    {
        try
        {
            await StartCaptureAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Report any failure in the info bar.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ShowError(ex);
        }

        RefreshCaptureState();
    }

    private void TrustRoot()
    {
        try
        {
            _host.TrustRootCertificate();
            SetMessage("The Snoop root certificate is now trusted. Restart browsers that were already open.", isError: false);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            SetMessage($"The certificate was not installed (the Windows prompt may have been declined): {ex.Message}", isError: true);
        }

        RefreshTrust();
    }

    /// <summary>The selected rows in the order shown (the inspected one alone when nothing else is selected).</summary>
    private List<SessionRowViewModel> SelectedRowsInOrder()
    {
        if (Sessions.SelectedRows.Count <= 1)
        {
            return Sessions.SelectedRow is { } row ? [row] : [];
        }

        var wanted = Sessions.SelectedRows.ToHashSet();
        return [.. Sessions.Rows.Where(wanted.Contains)];
    }

    /// <summary>Copies something about every selected session, one after another.</summary>
    private void CopySelected(Func<SessionRowViewModel, string> text, string separator)
    {
        var rows = SelectedRowsInOrder();
        if (rows.Count == 0)
        {
            return;
        }

        try
        {
            _clipboard.SetText(string.Join(separator, rows.Select(text)));
        }
#pragma warning disable CA1031 // The clipboard can be locked by another app; report it, don't crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SetMessage($"Could not copy to the clipboard: {ex.Message}", isError: true);
        }
    }

    private void RemoveSelected()
    {
        if (Sessions.SelectedRows.Count > 0)
        {
            Sessions.Remove(Sessions.SelectedRows);
        }
        else if (Sessions.SelectedRow is { } row)
        {
            Sessions.Remove([row]);
        }
    }

    private void SkipSelectedHost()
    {
        if (Sessions.SelectedRow is { } row)
        {
            var host = row.Session.Request.Url.Host;
            SetMessage(
                _host.SkipDecryption(host)
                    ? $"{host} will no longer be decrypted in this capture. New connections pass through untouched."
                    : $"{host} is already not being decrypted.",
                isError: false);
        }
    }

    /// <summary>The sessions an export works on: the selected ones, or every shown one.</summary>
    private IReadOnlyList<Core.Sessions.Session> ExportScope(bool selectedOnly) =>
        selectedOnly ? Sessions.SelectedSessions : Sessions.VisibleSessions;

    private async Task ExportHarAsync(bool redact, bool selectedOnly)
    {
        var sessions = ExportScope(selectedOnly);
        if (sessions.Count == 0)
        {
            SetMessage("There are no sessions to export.", isError: true);
            return;
        }

        var name = $"snoop-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.har";
        var stream = await _files.PickSaveFileAsync(name, "HTTP Archive", ".har").ConfigureAwait(true);
        if (stream is null)
        {
            return;
        }

        await using (stream.ConfigureAwait(true))
        {
            HarExporter.Write(stream, sessions, new HarExportOptions { RedactSecrets = redact });
        }

        SetMessage($"Exported {sessions.Count:N0} session(s){(redact ? " with secrets redacted" : string.Empty)}.", isError: false);
    }

    private async Task ExportTextAsync(bool selectedOnly)
    {
        var sessions = ExportScope(selectedOnly);
        if (sessions.Count == 0)
        {
            SetMessage("There are no sessions to export.", isError: true);
            return;
        }

        var name = $"snoop-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.txt";
        var stream = await _files.PickSaveFileAsync(name, "Text file", ".txt").ConfigureAwait(true);
        if (stream is null)
        {
            return;
        }

        await using (stream.ConfigureAwait(true))
        {
            var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true) { NewLine = "\r\n" };
            await using (writer.ConfigureAwait(true))
            {
                TextDumpExporter.Write(writer, sessions);
            }
        }

        SetMessage($"Exported {sessions.Count:N0} session(s) as text.", isError: false);
    }

    private async Task SaveSnapshotAsync(bool selectedOnly)
    {
        var sessions = ExportScope(selectedOnly);
        if (sessions.Count == 0)
        {
            SetMessage("There are no sessions to save.", isError: true);
            return;
        }

        var name = $"snoop-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}{SnapshotFile.Extension}";
        var stream = await _files.PickSaveFileAsync(name, "Snoop snapshot", SnapshotFile.Extension).ConfigureAwait(true);
        if (stream is null)
        {
            return;
        }

        int saved;
        await using (stream.ConfigureAwait(true))
        {
            saved = CaptureHost.SaveSnapshot(stream, sessions);
        }

        SetMessage($"Saved {saved:N0} session(s) to the snapshot.", isError: false);
    }

    /// <summary>Opens a Snoop snapshot or a HAR file (from a browser's developer tools or another HTTP tool).</summary>
    private async Task OpenCaptureFileAsync()
    {
        if (_openFiles is null)
        {
            return;
        }

        var path = await _openFiles.PickFileAsync("Snoop snapshot or HAR file", SnapshotFile.Extension, HarFile.Extension).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        OpenFile(path);
    }

    /// <summary>Adds the sessions in a snapshot or HAR file and says how it went (also used by --open).</summary>
    public void OpenFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            (int Added, int Skipped) result;
            using (var stream = File.OpenRead(path))
            {
                result = _host.OpenCaptureFile(stream);
            }

            Sessions.ApplyPendingChanges();
            var skipped = result.Skipped > 0 ? $" ({result.Skipped:N0} entries could not be read)" : string.Empty;
            SetMessage($"Added {result.Added:N0} session(s) from {Path.GetFileName(path)}{skipped}.", isError: false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            SetMessage($"Could not open {Path.GetFileName(path)}: {ex.Message}", isError: true);
        }
    }

    private List<SessionRowViewModel> ResendableRows() =>
        [.. SelectedRowsInOrder().Where(r => r.Session.Kind == Core.Sessions.SessionKind.Http)];

    private async Task ResendSelectedAsync()
    {
        var rows = ResendableRows();
        if (rows.Count == 0)
        {
            return;
        }

        if (!_host.IsRunning)
        {
            SetMessage("Start capturing first: requests are sent again through Snoop.", isError: true);
            return;
        }

        var sent = new List<string>();
        foreach (var row in rows)
        {
            if (ComposerViewModel.FromCapturedRequest(row.Session.Request, out var error) is not { } request)
            {
                SetMessage($"#{row.Id.ToString(CultureInfo.InvariantCulture)} cannot be sent again: {error}", isError: true);
                return;
            }

            var result = await _host.SendComposedAsync(request, CancellationToken.None).ConfigureAwait(true);
            var outcome = result.StatusCode is { } code
                ? $"{code.ToString(CultureInfo.InvariantCulture)} {result.ReasonPhrase}".TrimEnd()
                : result.Error ?? "no response";
            sent.Add(result.Session is { } copy
                ? $"#{row.Id.ToString(CultureInfo.InvariantCulture)} as #{copy.Id.ToString(CultureInfo.InvariantCulture)} ({outcome})"
                : $"#{row.Id.ToString(CultureInfo.InvariantCulture)} ({outcome})");
        }

        SetMessage(rows.Count == 1 ? $"Sent {sent[0]} again." : $"Sent {rows.Count} requests again: {string.Join(", ", sent)}.", isError: false);
    }

    private void EditInComposer()
    {
        if (Sessions.SelectedRow is { } row)
        {
            OpenInComposer(row);
        }
    }

    /// <summary>Whether <paramref name="row"/> can go to the Composer (drag and drop): HTTP requests only, not tunnels.</summary>
    public static bool CanOpenInComposer(SessionRowViewModel? row) => row?.Session.Kind == Core.Sessions.SessionKind.Http;

    /// <summary>Loads <paramref name="row"/>'s request into the Composer and shows it (used by drag and drop).</summary>
    public bool OpenInComposer(SessionRowViewModel? row)
    {
        if (!CanOpenInComposer(row))
        {
            return false;
        }

        Composer.LoadFrom(row!.Session);
        SelectedTabIndex = ComposerTab;
        return true;
    }

    private void CreateRuleFromSelected()
    {
        if (Sessions.SelectedRow is { } row)
        {
            Rules.AddFromSession(row.Session);
            SelectedTabIndex = RulesTab;
            SetMessage("Added a rule that answers this URL with the captured response. Edit it in the Rules tab.", isError: false);
        }
    }

    /// <summary>Applies a saved filter.</summary>
    public void ApplySavedFilter(SavedFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _searchText = filter.SearchText;
        _hideTunnels = filter.HideTunnels;
        _hideStatic = filter.HideStatic;
        _onlyErrors = filter.OnlyErrors;
        _onlyHost = filter.Host ?? string.Empty;
        _onlyProcess = filter.ProcessName ?? string.Empty;
        RaiseScope();
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(HideTunnels));
        OnPropertyChanged(nameof(HideStatic));
        OnPropertyChanged(nameof(OnlyErrors));
        ApplyFilter();
        SaveSettings(_settings with { HideTunnels = _hideTunnels });
    }

    public void DeleteSavedFilter(SavedFilter filter)
    {
        if (SavedFilters.Remove(filter))
        {
            SaveSettings(_settings with { SavedFilters = [.. SavedFilters] });
        }
    }

    /// <summary>Selects a session in the grid (from the timeline), if the current filter shows it.</summary>
    public void SelectSession(long id)
    {
        var row = Sessions.Rows.FirstOrDefault(r => r.Id == id);
        if (row is not null)
        {
            Sessions.SelectedRow = row;
            SelectedTabIndex = InspectorsTab;
        }
    }

    private void SaveCurrentFilter()
    {
        var name = _newFilterName.Trim();
        if (name.Length == 0)
        {
            name = _searchText.Trim().Length > 0 ? _searchText.Trim()
                : ScopeText.Length > 0 ? ScopeText
                : $"Filter {SavedFilters.Count + 1}";
        }

        var filter = new SavedFilter(name, _searchText, _hideTunnels, _hideStatic, _onlyErrors, _onlyHost, _onlyProcess);
        var existing = SavedFilters.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SavedFilters[SavedFilters.IndexOf(existing)] = filter;
        }
        else
        {
            SavedFilters.Add(filter);
        }

        NewFilterName = string.Empty;
        SaveSettings(_settings with { SavedFilters = [.. SavedFilters] });
        SetMessage($"Saved the filter \"{name}\".", isError: false);
    }

    private void MarkForCompare()
    {
        _markedForCompare = Sessions.SelectedRow;
        OnPropertyChanged(nameof(MarkedForCompareText));
        CompareWithMarkedCommand.NotifyCanExecuteChanged();
        if (_markedForCompare is not null)
        {
            SetMessage($"Marked #{_markedForCompare.Id}. Now right-click another session and choose Compare with marked.", isError: false);
        }
    }

    private void CompareWithMarked()
    {
        if (_markedForCompare is { } marked && Sessions.SelectedRow is { } selected)
        {
            if (_host.Sessions.Find(marked.Id) is null)
            {
                _markedForCompare = null;
                OnPropertyChanged(nameof(MarkedForCompareText));
                SetMessage("The marked session was removed. Mark another one first.", isError: true);
                return;
            }

            Compare.Compare(marked.Session, selected.Session);
            SelectedTabIndex = CompareTab;
        }
    }

    private async Task ApplySettingsAsync()
    {
        if (Settings.TryBuild() is not { } updated)
        {
            return;
        }

        var before = _settings;
        SaveSettings(updated);
        Settings.Load(updated);
        OnPropertyChanged(nameof(DecryptHttps));
        OnPropertyChanged(nameof(ShowTrustPrompt));

        if (_host.IsRunning && SettingsViewModel.NeedsRestart(before, updated))
        {
            await StartCaptureAsync().ConfigureAwait(true);
            SetMessage("Settings saved; the capture restarted to use them.", isError: false);
        }
        else
        {
            if (_host.IsRunning && before.UseSystemProxy != updated.UseSystemProxy && _host.IsSystemProxySupported)
            {
                _host.SetSystemProxy(updated.UseSystemProxy);
            }

            SetMessage("Settings saved.", isError: false);
        }

        RefreshCaptureState();
    }

    private void UntrustRoot()
    {
        try
        {
            var removed = _host.UntrustRootCertificates();
            SetMessage(removed == 0 ? "No Snoop root certificate was trusted." : "The Snoop root certificate was removed from your trusted roots.", isError: false);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            SetMessage($"The certificate was not removed: {ex.Message}", isError: true);
        }

        RefreshTrust();
    }

    private async Task ExportRootAsync()
    {
        var stream = await _files.PickSaveFileAsync("snoop-root.cer", "Certificate", ".cer").ConfigureAwait(true);
        if (stream is null)
        {
            return;
        }

        await using (stream.ConfigureAwait(true))
        {
            var bytes = _host.GetCertificateAuthority().RootCertificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert);
            await stream.WriteAsync(bytes).ConfigureAwait(true);
        }

        SetMessage("Saved the Snoop root certificate. Install it on a phone or another PC to decrypt its traffic.", isError: false);
    }

    // ------------------------------------------------------------ Helpers

    private bool HasSelection() => Sessions.SelectedRow is not null;

    private void OnSelectionChanged()
    {
        Inspector.Show(Sessions.SelectedRow?.Session);
        WebSocket.Show(Sessions.SelectedRow?.Session);
        OnPropertyChanged(nameof(IsWebSocketSelected));
        if (!IsWebSocketSelected && _lastInspectView == WebSocketTab)
        {
            _lastInspectView = InspectorsTab;
            if (_selectedTabIndex == WebSocketTab)
            {
                SelectedTabIndex = InspectorsTab;
            }
            else
            {
                OnPropertyChanged(nameof(InspectViewIndex));
            }
        }
        MarkForCompareCommand.NotifyCanExecuteChanged();
        CompareWithMarkedCommand.NotifyCanExecuteChanged();
        CopyUrlCommand.NotifyCanExecuteChanged();
        CopyCurlCommand.NotifyCanExecuteChanged();
        CopyPowerShellCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        SkipHostCommand.NotifyCanExecuteChanged();
        EditInComposerCommand.NotifyCanExecuteChanged();
        CreateRuleCommand.NotifyCanExecuteChanged();
        ResendCommand.NotifyCanExecuteChanged();
        ExportSelectedHarCommand.NotifyCanExecuteChanged();
        ExportSelectedTextCommand.NotifyCanExecuteChanged();
        SaveSelectedSnapshotCommand.NotifyCanExecuteChanged();
        ShowOnlyHostCommand.NotifyCanExecuteChanged();
        ShowOnlyProcessCommand.NotifyCanExecuteChanged();
    }

    private void ApplyFilter()
    {
        Sessions.Filter = new SessionFilter
        {
            SearchText = _searchText,
            HideTunnels = _hideTunnels,
            HideStatic = _hideStatic,
            OnlyErrors = _onlyErrors,
            SearchBodies = _searchBodies,
            Host = _onlyHost,
            ProcessName = _onlyProcess,
        };
        Sessions.Columns.HighlightText = _searchText;
    }

    private void ClearScope()
    {
        if (!HasScope)
        {
            return;
        }

        _onlyHost = string.Empty;
        _onlyProcess = string.Empty;
        RaiseScope();
        ApplyFilter();
    }

    private void RaiseScope()
    {
        OnPropertyChanged(nameof(IsOnlyHost));
        OnPropertyChanged(nameof(IsOnlyProcess));
        OnPropertyChanged(nameof(OnlyHostMenuText));
        OnPropertyChanged(nameof(OnlyProcessMenuText));
        OnPropertyChanged(nameof(HasScope));
        OnPropertyChanged(nameof(ScopeText));
    }

    private void RaiseEmptyState()
    {
        OnPropertyChanged(nameof(IsGridEmpty));
        OnPropertyChanged(nameof(EmptyGridTitle));
        OnPropertyChanged(nameof(EmptyGridText));
    }

    private void RefreshCaptureState()
    {
        RaiseEmptyState();
        OnPropertyChanged(nameof(IsCapturing));
        OnPropertyChanged(nameof(CaptureButtonText));
        OnPropertyChanged(nameof(IsSystemProxyOn));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ShowTrustPrompt));
        SkipHostCommand.NotifyCanExecuteChanged();
    }

    private void RefreshTrust()
    {
        try
        {
            IsRootTrusted = _host.IsRootTrusted;
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Security.Cryptography.CryptographicException)
        {
            IsRootTrusted = false;
            ShowError(ex);
        }

        OnPropertyChanged(nameof(ShowTrustPrompt));
    }

    private void SaveSettings(AppSettings settings)
    {
        _settings = settings;
        if (_settingsPath is null)
        {
            return;
        }

        try
        {
            settings.Save(_settingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetMessage($"Could not save settings: {ex.Message}", isError: true);
        }
    }

    private void SetMessage(string? message, bool isError)
    {
        MessageIsError = isError;
        Message = message;
    }

    private void ShowError(Exception ex) => SetMessage(ex.Message, isError: true);
}
