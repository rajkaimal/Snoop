using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Snoop.Core.Sessions;
using Snoop.Hosting.Rules;
using Snoop.Presentation.Editing;
using Snoop.Presentation.Mvvm;
using Snoop.Presentation.Services;
using Snoop.Proxy.Rules;

namespace Snoop.Presentation.ViewModels;

/// <summary>
/// The Rules tab: an ordered list of traffic rules. Every change applies to traffic at once and is
/// saved to the rules file.
/// </summary>
public sealed class RulesViewModel : ObservableObject
{
    private readonly RuleSet _ruleSet;
    private readonly string? _path;
    private readonly IFileOpenService? _files;
    private readonly Action<string, bool> _report;
    private int _selectedIndex = -1;
    private bool _loading;
    private bool _moving;

    /// <param name="ruleSet">The live rules used by the proxy.</param>
    /// <param name="path">The rules file; null keeps rules in memory only.</param>
    /// <param name="files">For choosing response files; null hides Browse.</param>
    /// <param name="report">Shows a message (text, isError) to the user.</param>
    public RulesViewModel(RuleSet ruleSet, string? path, IFileOpenService? files, Action<string, bool> report)
    {
        _ruleSet = ruleSet ?? throw new ArgumentNullException(nameof(ruleSet));
        _path = path;
        _files = files;
        _report = report ?? throw new ArgumentNullException(nameof(report));
        Items.CollectionChanged += OnItemsChanged;

        AddCommand = new RelayCommand(() => Add(new TrafficRule { Name = NextName(), Actions = [new RuleAction(RuleActionKind.Respond, "200", string.Empty)] }));
        RemoveCommand = new RelayCommand(RemoveSelected, () => Selected is not null);
        DuplicateCommand = new RelayCommand(DuplicateSelected, () => Selected is not null);
        MoveUpCommand = new RelayCommand(() => Move(-1), () => _selectedIndex > 0);
        MoveDownCommand = new RelayCommand(() => Move(1), () => _selectedIndex >= 0 && _selectedIndex < Items.Count - 1);
    }

    public ObservableCollection<RuleEditorViewModel> Items { get; } = [];

    public RelayCommand AddCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public RelayCommand DuplicateCommand { get; }

    public RelayCommand MoveUpCommand { get; }

    public RelayCommand MoveDownCommand { get; }

    /// <summary>Index of the rule being edited, or -1.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            // While a rule moves, the list control briefly reports "nothing selected"; ignore that.
            var index = value >= -1 && value < Items.Count ? value : -1;
            if (_moving && index == -1)
            {
                return;
            }

            if (SetProperty(ref _selectedIndex, index))
            {
                OnPropertyChanged(nameof(Selected));
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(HasNoSelection));
                RefreshCommands();
            }
        }
    }

    public RuleEditorViewModel? Selected => _selectedIndex >= 0 && _selectedIndex < Items.Count ? Items[_selectedIndex] : null;

    public bool HasSelection => Selected is not null;

    public bool HasNoSelection => Selected is null;

    public bool IsEmpty => Items.Count == 0;

    /// <summary>Tab title, e.g. "Rules (2 on)".</summary>
    public string HeaderText
    {
        get
        {
            var on = Items.Count(r => r.Enabled && !r.HasError);
            return on == 0 ? "Rules" : $"Rules ({on.ToString(CultureInfo.InvariantCulture)} on)";
        }
    }

    /// <summary>Loads the rules file and applies it. Problems are reported, never thrown.</summary>
    public void Load()
    {
        IReadOnlyList<TrafficRule> rules = [];
        if (_path is not null)
        {
            try
            {
                rules = RulesFile.Load(_path);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                _report($"Could not load rules: {ex.Message}", true);
            }
        }

        _loading = true;
        try
        {
            Items.Clear();
            foreach (var rule in rules)
            {
                Items.Add(CreateEditor(rule));
            }
        }
        finally
        {
            _loading = false;
        }

        SelectedIndex = Items.Count > 0 ? 0 : -1;
        Apply(save: false);
    }

    /// <summary>Adds a rule that answers <paramref name="session"/>'s URL with its captured response.</summary>
    public RuleEditorViewModel AddFromSession(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var url = session.Request.Url;
        var actions = new List<RuleAction>();
        if (session.Response is { } response)
        {
            var body = response.Body is { } captured ? MessageText.DescribeBody(captured.ToArray(), response.Headers) : BodyText.Empty;
            actions.Add(new RuleAction(RuleActionKind.Respond, response.StatusCode.ToString(CultureInfo.InvariantCulture), body.IsEditable ? body.Text : string.Empty));
            if (response.Headers.Get("Content-Type") is { } contentType)
            {
                actions.Add(new RuleAction(RuleActionKind.SetResponseHeader, "Content-Type", contentType));
            }
        }
        else
        {
            actions.Add(new RuleAction(RuleActionKind.Respond, "200", string.Empty));
        }

        return Add(new TrafficRule
        {
            Name = $"{session.Request.Method} {url.Host}{url.AbsolutePath}",
            MatchKind = RuleMatchKind.Exact,
            Pattern = url.AbsoluteUri,
            Method = session.Request.Method,
            Actions = actions,
        });
    }

    /// <summary>Adds a rule at the end and selects it.</summary>
    public RuleEditorViewModel Add(TrafficRule rule)
    {
        var editor = CreateEditor(rule);
        Items.Add(editor);
        SelectedIndex = Items.Count - 1;
        return editor;
    }

    private RuleEditorViewModel CreateEditor(TrafficRule rule)
    {
        var editor = new RuleEditorViewModel(rule, _files);
        editor.Changed += (_, _) => Apply(save: true);
        return editor;
    }

    private void RemoveSelected()
    {
        var index = _selectedIndex;
        if (Selected is null)
        {
            return;
        }

        Items.RemoveAt(index);
        SelectedIndex = Math.Min(index, Items.Count - 1);
    }

    private void DuplicateSelected()
    {
        if (Selected is { } selected)
        {
            var copy = selected.ToRule() with { Id = Guid.NewGuid(), Name = selected.Name + " (copy)" };
            var editor = CreateEditor(copy);
            Items.Insert(_selectedIndex + 1, editor);
            SelectedIndex = _selectedIndex + 1;
        }
    }

    private void Move(int delta)
    {
        var from = _selectedIndex;
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Items.Count)
        {
            return;
        }

        _moving = true;
        try
        {
            Items.Move(from, to);
        }
        finally
        {
            _moving = false;
        }

        SelectedIndex = to;
        OnPropertyChanged(nameof(SelectedIndex));
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasNoSelection));
        RefreshCommands();
        Apply(save: true);
    }

    private void Apply(bool save)
    {
        var rules = Items.Select(i => i.ToRule()).ToList();
        _ruleSet.Replace(rules);
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(IsEmpty));
        if (!save || _path is null)
        {
            return;
        }

        try
        {
            RulesFile.Save(rules, _path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _report($"Could not save rules: {ex.Message}", true);
        }
    }

    private void RefreshCommands()
    {
        RemoveCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    private string NextName()
    {
        for (var i = Items.Count + 1; ; i++)
        {
            var name = $"Rule {i.ToString(CultureInfo.InvariantCulture)}";
            if (!Items.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return name;
            }
        }
    }
}

/// <summary>Edits one rule. Raises <see cref="Changed"/> after every edit.</summary>
public sealed class RuleEditorViewModel : ObservableObject
{
    private readonly IFileOpenService? _files;
    private readonly Guid _id;
    private string _name;
    private bool _enabled;
    private RuleMatchKind _matchKind;
    private string _pattern;
    private string _method;
    private string? _error;

    public RuleEditorViewModel(TrafficRule rule, IFileOpenService? files = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _files = files;
        _id = rule.Id;
        _name = rule.Name;
        _enabled = rule.Enabled;
        _matchKind = rule.MatchKind;
        _pattern = rule.Pattern;
        _method = rule.Method;
        foreach (var action in rule.Actions)
        {
            Actions.Add(CreateAction(action));
        }

        Actions.CollectionChanged += (_, _) => OnEdited();
        AddActionCommand = new RelayCommand(() => Actions.Add(CreateAction(new RuleAction(RuleActionKind.SetResponseHeader))));
        Validate();
    }

    /// <summary>Raised after any edit, including edits to actions.</summary>
    public event EventHandler? Changed;

    /// <summary>Display names for <see cref="RuleMatchKind"/>, in enum order.</summary>
    public static IReadOnlyList<string> MatchKindNames { get; } = new[] { "URL contains", "URL matches wildcard (*)", "URL matches regex", "URL is exactly" };

    public ObservableCollection<RuleActionViewModel> Actions { get; } = [];

    public RelayCommand AddActionCommand { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value ?? string.Empty))
            {
                OnEdited();
            }
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                OnEdited();
            }
        }
    }

    public int MatchKindIndex
    {
        get => (int)_matchKind;
        set
        {
            if (value >= 0 && value < MatchKindNames.Count && SetProperty(ref _matchKind, (RuleMatchKind)value, nameof(MatchKindIndex)))
            {
                OnEdited();
            }
        }
    }

    public string Pattern
    {
        get => _pattern;
        set
        {
            if (SetProperty(ref _pattern, value ?? string.Empty))
            {
                OnEdited();
            }
        }
    }

    /// <summary>Only this method matches; empty means any.</summary>
    public string Method
    {
        get => _method;
        set
        {
            if (SetProperty(ref _method, value ?? string.Empty))
            {
                OnEdited();
            }
        }
    }

    /// <summary>Why the rule cannot run, or null.</summary>
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

    /// <summary>One line for the rule list, e.g. "URL contains /api → Respond 200".</summary>
    public string Summary
    {
        get
        {
            var match = string.IsNullOrWhiteSpace(_pattern) ? "(no pattern)" : _pattern;
            var method = string.IsNullOrWhiteSpace(_method) ? string.Empty : $"{_method.Trim().ToUpperInvariant()} ";
            var actions = Actions.Count == 0 ? "(no actions)" : string.Join(", ", Actions.Select(a => a.ToAction().ToString()));
            return $"{method}{match} → {actions}";
        }
    }

    public TrafficRule ToRule() => new()
    {
        Id = _id,
        Name = _name,
        Enabled = _enabled,
        MatchKind = _matchKind,
        Pattern = _pattern.Trim(),
        Method = _method.Trim(),
        Actions = [.. Actions.Select(a => a.ToAction())],
    };

    private RuleActionViewModel CreateAction(RuleAction action)
    {
        var editor = new RuleActionViewModel(action, _files);
        editor.PropertyChanged += OnActionPropertyChanged;
        editor.RemoveRequested += (_, _) =>
        {
            editor.PropertyChanged -= OnActionPropertyChanged;
            Actions.Remove(editor);
        };
        return editor;
    }

    private void OnActionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RuleActionViewModel.KindIndex) or nameof(RuleActionViewModel.Name) or nameof(RuleActionViewModel.Value))
        {
            OnEdited();
        }
    }

    private void OnEdited()
    {
        Validate();
        OnPropertyChanged(nameof(Summary));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Validate() => Error = RuleSet.Validate(ToRule());
}

/// <summary>Edits one action of a rule. What Name and Value mean depends on the kind.</summary>
public sealed class RuleActionViewModel : ObservableObject
{
    private readonly IFileOpenService? _files;
    private RuleActionKind _kind;
    private string _name;
    private string _value;

    public RuleActionViewModel(RuleAction action, IFileOpenService? files = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        _files = files;
        _kind = action.Kind;
        _name = action.Name;
        _value = action.Value;
        RemoveCommand = new RelayCommand(() => RemoveRequested?.Invoke(this, EventArgs.Empty));
        BrowseCommand = new AsyncRelayCommand(BrowseAsync, _ => { }, () => _files is not null);
    }

    public event EventHandler? RemoveRequested;

    /// <summary>Display names for <see cref="RuleActionKind"/>, in enum order.</summary>
    public static IReadOnlyList<string> KindNames { get; } = new[]
    {
        "Respond with text",
        "Respond with a file",
        "Delay",
        "Block (close connection)",
        "Rewrite URL",
        "Set request header",
        "Remove request header",
        "Set response header",
        "Remove response header",
        "Pause request (breakpoint)",
        "Pause response (breakpoint)",
    };

    public RelayCommand RemoveCommand { get; }

    public AsyncRelayCommand BrowseCommand { get; }

    public int KindIndex
    {
        get => (int)_kind;
        set
        {
            if (value < 0 || value >= KindNames.Count || !SetProperty(ref _kind, (RuleActionKind)value, nameof(KindIndex)))
            {
                return;
            }

            if (_kind is RuleActionKind.Respond or RuleActionKind.RespondWithFile && string.IsNullOrWhiteSpace(_name))
            {
                _name = "200";
                OnPropertyChanged(nameof(Name));
            }

            OnPropertyChanged(nameof(NameLabel));
            OnPropertyChanged(nameof(ValueLabel));
            OnPropertyChanged(nameof(ShowName));
            OnPropertyChanged(nameof(ShowValue));
            OnPropertyChanged(nameof(IsMultilineValue));
            OnPropertyChanged(nameof(IsSingleLineValue));
            OnPropertyChanged(nameof(CanBrowse));
        }
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
    }

    public string Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(SingleLineValue));
                OnPropertyChanged(nameof(MultilineValue));
            }
        }
    }

    /// <summary>
    /// <see cref="Value"/> for the one-line box. Ignores writes unless that box is the one in use, so the hidden
    /// box can never cut a multi-line body to its first line.
    /// </summary>
    public string SingleLineValue
    {
        get => _value;
        set
        {
            if (IsSingleLineValue)
            {
                Value = value;
            }
        }
    }

    /// <summary><see cref="Value"/> for the multi-line body box. Line-ending-only changes (the text box uses \r) are ignored.</summary>
    public string MultilineValue
    {
        get => _value;
        set
        {
            if (IsMultilineValue && MessageText.NormalizeNewlines(value ?? string.Empty) != MessageText.NormalizeNewlines(_value))
            {
                Value = value ?? string.Empty;
            }
        }
    }

    public string NameLabel => _kind switch
    {
        RuleActionKind.Respond or RuleActionKind.RespondWithFile => "Status",
        RuleActionKind.RewriteUrl => "Find",
        _ => "Header",
    };

    public string ValueLabel => _kind switch
    {
        RuleActionKind.Respond => "Body",
        RuleActionKind.RespondWithFile => "File",
        RuleActionKind.Delay => "Milliseconds",
        RuleActionKind.RewriteUrl => "Replace with",
        _ => "Value",
    };

    public bool ShowName => _kind is not (RuleActionKind.Delay or RuleActionKind.Block or RuleActionKind.BreakOnRequest or RuleActionKind.BreakOnResponse);

    public bool ShowValue => _kind is not (RuleActionKind.Block or RuleActionKind.RemoveRequestHeader or RuleActionKind.RemoveResponseHeader
        or RuleActionKind.BreakOnRequest or RuleActionKind.BreakOnResponse);

    /// <summary>The Value box is a multi-line body editor.</summary>
    public bool IsMultilineValue => _kind == RuleActionKind.Respond;

    public bool IsSingleLineValue => ShowValue && !IsMultilineValue;

    public bool CanBrowse => _kind == RuleActionKind.RespondWithFile && _files is not null;

    public RuleAction ToAction() => new(
        _kind,
        ShowName ? _name.Trim() : string.Empty,
        !ShowValue ? string.Empty : _kind == RuleActionKind.Respond ? MessageText.NormalizeNewlines(_value) : _value.Trim());

    private async Task BrowseAsync()
    {
        if (_files is null)
        {
            return;
        }

        var path = await _files.PickFileAsync("Response file", "*").ConfigureAwait(true);
        if (path is not null)
        {
            Value = path;
        }
    }
}
