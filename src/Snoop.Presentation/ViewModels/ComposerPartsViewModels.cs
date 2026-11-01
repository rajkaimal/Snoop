using System.Collections.ObjectModel;
using Snoop.Presentation.Composer;
using Snoop.Presentation.Mvvm;

namespace Snoop.Presentation.ViewModels;

/// <summary>One row of the Composer's headers table. Unticked rows are kept but not sent.</summary>
public sealed class HeaderRowViewModel : ObservableObject
{
    private bool _enabled;
    private string _name;
    private string _value;

    public HeaderRowViewModel(string name = "", string value = "", bool enabled = true)
    {
        _name = name ?? string.Empty;
        _value = value ?? string.Empty;
        _enabled = enabled;
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value ?? string.Empty);
    }

    /// <summary>True for a row nobody filled in (left out when sending and saving).</summary>
    public bool IsBlank => _name.Trim().Length == 0 && _value.Trim().Length == 0;
}

/// <summary>A folder or saved request in the Composer's library tree.</summary>
public sealed class LibraryItemViewModel : ObservableObject
{
    private bool _isExpanded;

    public LibraryItemViewModel(LibraryEntry entry, ISet<string>? expanded = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Path = entry.Path;
        Name = entry.Name;
        IsFolder = entry.IsFolder;
        _isExpanded = entry.IsFolder && (expanded?.Contains(entry.Path) ?? false);
        foreach (var child in entry.Children)
        {
            Children.Add(new LibraryItemViewModel(child, expanded));
        }
    }

    /// <summary>For folders: shown open in the tree (kept when the tree is rebuilt).</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value && IsFolder);
    }

    public string Path { get; }

    public string Name { get; }

    public bool IsFolder { get; }

    public ObservableCollection<LibraryItemViewModel> Children { get; } = [];

    /// <summary>Segoe Fluent icon: folder or page.</summary>
    public string Glyph => IsFolder ? "" : "";

    /// <summary>The folder a new request goes into when this item is chosen: itself, or the folder it is in.</summary>
    public string? FolderForNew => IsFolder ? Path : RequestLibrary.ParentOf(Path);
}

/// <summary>
/// The saved-requests tree beside the Composer. Changes go straight to disk; the tree is rebuilt after
/// each one. Without a library folder (tests) everything is a no-op.
/// </summary>
public sealed class RequestLibraryViewModel : ObservableObject
{
    private readonly RequestLibrary? _library;
    private readonly Action<string, bool> _report;
    private ObservableCollection<LibraryItemViewModel> _items = [];

    public RequestLibraryViewModel(RequestLibrary? library, Action<string, bool> report)
    {
        _library = library;
        _report = report ?? throw new ArgumentNullException(nameof(report));
        Refresh();
    }

    public bool IsAvailable => _library is not null;

    public RequestLibrary? Library => _library;

    /// <summary>Top-level folders and requests. Replaced on every change.</summary>
    public ObservableCollection<LibraryItemViewModel> Items
    {
        get => _items;
        private set
        {
            if (SetProperty(ref _items, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public bool IsEmpty => _items.Count == 0;

    /// <summary>"Top level" plus every folder, for the Save dialog.</summary>
    public IReadOnlyList<string> FolderChoices => [TopLevel, .. _library?.FolderPaths() ?? []];

    public const string TopLevel = "(top level)";

    /// <summary>Raised after a rename or delete, with the old path and the new one (null when deleted), so the Composer can follow.</summary>
    public event EventHandler<(string OldPath, string? NewPath, bool IsFolder)>? Moved;

    /// <summary>Rebuilds the tree from disk, keeping open folders open; <paramref name="reveal"/>'s folders are opened too.</summary>
    public void Refresh(string? reveal = null)
    {
        if (_library is null)
        {
            return;
        }

        var expanded = new HashSet<string>(StringComparer.Ordinal);
        void Collect(IEnumerable<LibraryItemViewModel> items)
        {
            foreach (var item in items.Where(i => i.IsFolder))
            {
                if (item.IsExpanded)
                {
                    expanded.Add(item.Path);
                }

                Collect(item.Children);
            }
        }

        Collect(_items);
        for (var folder = reveal is null ? null : RequestLibrary.ParentOf(reveal); folder is not null; folder = RequestLibrary.ParentOf(folder))
        {
            expanded.Add(folder);
        }

        try
        {
            Items = new ObservableCollection<LibraryItemViewModel>(_library.List().Select(e => new LibraryItemViewModel(e, expanded)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _report($"Could not read the saved requests: {ex.Message}", true);
        }

        OnPropertyChanged(nameof(FolderChoices));
    }

    /// <summary>Turns a Save-dialog folder choice back into a folder path (null for the top level).</summary>
    public static string? FolderFromChoice(string? choice) =>
        string.IsNullOrEmpty(choice) || choice == TopLevel ? null : choice;

    public bool Rename(LibraryItemViewModel item, string newName) => Change(() =>
    {
        ArgumentNullException.ThrowIfNull(item);
        var wasOpen = item.IsExpanded;
        var newPath = _library!.Rename(item.Path, item.IsFolder, newName);
        Moved?.Invoke(this, (item.Path, newPath, item.IsFolder));
        return wasOpen ? newPath + "/_" : newPath; // reveal opens the parents of a path; "/_" makes that the folder itself
    });

    public bool Duplicate(LibraryItemViewModel item) => Change(() =>
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.IsFolder ? null : _library!.Duplicate(item.Path);
    });

    public bool Delete(LibraryItemViewModel item) => Change(() =>
    {
        ArgumentNullException.ThrowIfNull(item);
        _library!.Delete(item.Path, item.IsFolder);
        Moved?.Invoke(this, (item.Path, null, item.IsFolder));
        return null;
    });

    /// <summary>Creates a folder inside <paramref name="parent"/> (or at the top level).</summary>
    public bool NewFolder(LibraryItemViewModel? parent, string name) => Change(() => _library!.CreateFolder(parent?.FolderForNew, name));

    /// <summary>Runs a change, then rebuilds the tree with the changed item's folders open.</summary>
    private bool Change(Func<string?> change)
    {
        if (_library is null)
        {
            return false;
        }

        string? reveal = null;
        try
        {
            reveal = change();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _report(ex.Message, true);
            return false;
        }
        finally
        {
            Refresh(reveal);
        }
    }
}

/// <summary>The environment picker and the variables editor.</summary>
public sealed class EnvironmentsViewModel : ObservableObject
{
    public const string NoEnvironment = "No environment";

    private readonly string? _path;
    private ComposerEnvironments _current;
    private int _selectedIndex;
    private string _editText = string.Empty;
    private string? _editError;

    public EnvironmentsViewModel(string? path)
    {
        _path = path;
        _current = path is null ? ComposerEnvironments.Empty : ComposerEnvironments.Load(path);
        RebuildChoices();
    }

    /// <summary>"No environment" followed by every environment's name.</summary>
    public ObservableCollection<string> Choices { get; } = [];

    /// <summary>Index into <see cref="Choices"/>; 0 is none.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value < 0 || value >= Choices.Count || value == _selectedIndex)
            {
                return;
            }

            _selectedIndex = value;
            _current = _current with { Active = value == 0 ? null : Choices[value] };
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveName));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public string ActiveName => _current.Active ?? NoEnvironment;

    public ComposerEnvironments Current => _current;

    /// <summary>Raised when the active environment or the variables change.</summary>
    public event EventHandler? Changed;

    /// <summary>The variables the next request uses.</summary>
    public IReadOnlyDictionary<string, string> Variables => _current.ActiveVariables();

    /// <summary>The text in the editor dialog.</summary>
    public string EditText
    {
        get => _editText;
        set => SetProperty(ref _editText, value ?? string.Empty);
    }

    public string? EditError
    {
        get => _editError;
        private set
        {
            if (SetProperty(ref _editError, value))
            {
                OnPropertyChanged(nameof(HasEditError));
                OnPropertyChanged(nameof(EditErrorText));
            }
        }
    }

    public bool HasEditError => _editError is not null;

    public string EditErrorText => _editError ?? string.Empty;

    public static string EditorHelp =>
        "One variable per line: name = value. Lines before any [Environment] are used everywhere. " +
        "Use them as {{name}} in the URL, headers or body. Built in: {{$guid}}, {{$timestamp}}, {{$isoTimestamp}}, {{$randomInt}}.";

    /// <summary>Fills the editor with the saved variables.</summary>
    public void BeginEdit()
    {
        EditText = _current.ToText();
        EditError = null;
    }

    /// <summary>Applies the editor's text. False (with <see cref="EditError"/>) when it has mistakes.</summary>
    public bool ApplyEdit()
    {
        if (!ComposerEnvironments.TryParse(_editText, _current.Active, out var parsed, out var error))
        {
            EditError = error;
            return false;
        }

        // A new single environment becomes active straight away, which is what people expect.
        if (parsed!.Active is null && _current.Active is null && parsed.Environments.Count == 1 && _current.Environments.Count == 0)
        {
            parsed = parsed with { Active = parsed.Environments[0].Name };
        }

        _current = parsed;
        EditError = null;
        Save();
        RebuildChoices();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void RebuildChoices()
    {
        Choices.Clear();
        Choices.Add(NoEnvironment);
        foreach (var environment in _current.Environments)
        {
            Choices.Add(environment.Name);
        }

        var index = _current.Active is null ? 0 : Choices.IndexOf(_current.Active);
        _selectedIndex = index < 0 ? 0 : index;
        OnPropertyChanged(nameof(SelectedIndex));
        OnPropertyChanged(nameof(ActiveName));
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            _current.Save(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EditError = $"Could not save the variables: {ex.Message}";
        }
    }
}
