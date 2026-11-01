using System.Globalization;
using Snoop.Presentation.Mvvm;
using Snoop.Presentation.Settings;

namespace Snoop.Presentation.ViewModels;

/// <summary>A column of the session grid.</summary>
public enum SessionColumn
{
    Id,
    Type,
    Result,
    Method,
    Protocol,
    Host,
    Path,
    Size,
    Time,
    Process,
}

/// <summary>How the session grid is sorted.</summary>
public readonly record struct SessionSort(SessionColumn Column, bool Descending)
{
    /// <summary>Arrival order, oldest first.</summary>
    public static SessionSort Default { get; } = new(SessionColumn.Id, false);

    public bool IsDefault => this == Default;
}

/// <summary>
/// Widths, visibility and sort marks of the session grid columns. One instance is shared by the
/// header and every row, so resizing a column moves the whole column. Path takes the remaining width.
/// </summary>
public sealed class SessionColumnsViewModel : ObservableObject
{
    public const double MinWidth = 28;
    public const double MaxWidth = 900;

    private static readonly Dictionary<SessionColumn, double> Defaults = new()
    {
        [SessionColumn.Id] = 76,
        [SessionColumn.Type] = 44,
        [SessionColumn.Result] = 48,
        [SessionColumn.Method] = 64,
        [SessionColumn.Protocol] = 72,
        [SessionColumn.Host] = 180,
        [SessionColumn.Size] = 72,
        [SessionColumn.Time] = 68,
        [SessionColumn.Process] = 130,
    };

    private static readonly Dictionary<SessionColumn, string> Labels = new()
    {
        [SessionColumn.Id] = "#",
        [SessionColumn.Type] = "Type",
        [SessionColumn.Result] = "Result",
        [SessionColumn.Method] = "Method",
        [SessionColumn.Protocol] = "Protocol",
        [SessionColumn.Host] = "Host",
        [SessionColumn.Path] = "Path",
        [SessionColumn.Size] = "Size",
        [SessionColumn.Time] = "Time",
        [SessionColumn.Process] = "Process",
    };

    private readonly Dictionary<SessionColumn, double> _widths = new(Defaults);
    private readonly Dictionary<SessionColumn, bool> _visible = Defaults.Keys.ToDictionary(c => c, _ => true);
    private SessionSort _sort = SessionSort.Default;
    private bool _resized;
    private string _highlightText = string.Empty;

    /// <summary>Raised after a user change (resize, show/hide), so it can be saved.</summary>
    public event EventHandler? Changed;

    /// <summary>Columns that can be hidden, with their labels, for menus.</summary>
    public static IReadOnlyList<SessionColumn> Hideable { get; } = Defaults.Keys.Where(c => c != SessionColumn.Id).ToArray();

    public static string LabelOf(SessionColumn column) => Labels[column];

    /// <summary>The search text; every row highlights its words in the host, path and process cells.</summary>
    public string HighlightText
    {
        get => _highlightText;
        set => SetProperty(ref _highlightText, value ?? string.Empty);
    }

    public SessionSort Sort
    {
        get => _sort;
        internal set
        {
            if (_sort != value)
            {
                _sort = value;
                OnPropertyChanged(string.Empty);
            }
        }
    }

    /// <summary>Width of the # column; 0 when hidden.</summary>
    public double IdWidth => WidthOf(SessionColumn.Id);

    public bool IdVisible
    {
        get => _visible[SessionColumn.Id];
        set => SetVisible(SessionColumn.Id, value);
    }

    public string IdHeader => HeaderFor(SessionColumn.Id, "#");
    /// <summary>Width of the Type column; 0 when hidden.</summary>
    public double TypeWidth => WidthOf(SessionColumn.Type);

    public bool TypeVisible
    {
        get => _visible[SessionColumn.Type];
        set => SetVisible(SessionColumn.Type, value);
    }

    public string TypeHeader => HeaderFor(SessionColumn.Type, "Type");
    /// <summary>Width of the Result column; 0 when hidden.</summary>
    public double ResultWidth => WidthOf(SessionColumn.Result);

    public bool ResultVisible
    {
        get => _visible[SessionColumn.Result];
        set => SetVisible(SessionColumn.Result, value);
    }

    public string ResultHeader => HeaderFor(SessionColumn.Result, "Result");
    /// <summary>Width of the Method column; 0 when hidden.</summary>
    public double MethodWidth => WidthOf(SessionColumn.Method);

    public bool MethodVisible
    {
        get => _visible[SessionColumn.Method];
        set => SetVisible(SessionColumn.Method, value);
    }

    public string MethodHeader => HeaderFor(SessionColumn.Method, "Method");
    /// <summary>Width of the Protocol column; 0 when hidden.</summary>
    public double ProtocolWidth => WidthOf(SessionColumn.Protocol);

    public bool ProtocolVisible
    {
        get => _visible[SessionColumn.Protocol];
        set => SetVisible(SessionColumn.Protocol, value);
    }

    public string ProtocolHeader => HeaderFor(SessionColumn.Protocol, "Protocol");
    /// <summary>Width of the Host column; 0 when hidden.</summary>
    public double HostWidth => WidthOf(SessionColumn.Host);

    public bool HostVisible
    {
        get => _visible[SessionColumn.Host];
        set => SetVisible(SessionColumn.Host, value);
    }

    public string HostHeader => HeaderFor(SessionColumn.Host, "Host");
    public string PathHeader => HeaderFor(SessionColumn.Path, "Path");
    /// <summary>Width of the Size column; 0 when hidden.</summary>
    public double SizeWidth => WidthOf(SessionColumn.Size);

    public bool SizeVisible
    {
        get => _visible[SessionColumn.Size];
        set => SetVisible(SessionColumn.Size, value);
    }

    public string SizeHeader => HeaderFor(SessionColumn.Size, "Size");
    /// <summary>Width of the Time column; 0 when hidden.</summary>
    public double TimeWidth => WidthOf(SessionColumn.Time);

    public bool TimeVisible
    {
        get => _visible[SessionColumn.Time];
        set => SetVisible(SessionColumn.Time, value);
    }

    public string TimeHeader => HeaderFor(SessionColumn.Time, "Time");
    /// <summary>Width of the Process column; 0 when hidden.</summary>
    public double ProcessWidth => WidthOf(SessionColumn.Process);

    public bool ProcessVisible
    {
        get => _visible[SessionColumn.Process];
        set => SetVisible(SessionColumn.Process, value);
    }

    public string ProcessHeader => HeaderFor(SessionColumn.Process, "Process");

    /// <summary>Changes a column's width by <paramref name="delta"/> pixels. Call <see cref="CommitResize"/> when the drag ends.</summary>
    public void Resize(SessionColumn column, double delta)
    {
        if (column == SessionColumn.Path || !_widths.TryGetValue(column, out var width))
        {
            return;
        }

        var next = Math.Clamp(width + delta, MinWidth, MaxWidth);
        if (Math.Abs(next - width) < 0.5)
        {
            return;
        }

        _widths[column] = next;
        _resized = true;
        OnPropertyChanged($"{column}Width");
    }

    /// <summary>Call when a resize drag ends: raises <see cref="Changed"/> once so the layout is saved.</summary>
    public void CommitResize()
    {
        if (_resized)
        {
            _resized = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Puts every column back to its default width and shows it.</summary>
    public void Reset()
    {
        foreach (var (column, width) in Defaults)
        {
            _widths[column] = width;
            _visible[column] = true;
        }

        OnPropertyChanged(string.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Load(IEnumerable<ColumnSetting> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (var setting in settings)
        {
            if (Enum.TryParse<SessionColumn>(setting.Name, ignoreCase: true, out var column) && _widths.ContainsKey(column))
            {
                _widths[column] = double.IsFinite(setting.Width) ? Math.Clamp(setting.Width, MinWidth, MaxWidth) : Defaults[column];
                _visible[column] = setting.Visible || column == SessionColumn.Id;
            }
        }

        OnPropertyChanged(string.Empty);
    }

    public IReadOnlyList<ColumnSetting> ToSettings() =>
        [.. _widths.Select(p => new ColumnSetting(p.Key.ToString(), Math.Round(p.Value), _visible[p.Key]))];

    private double WidthOf(SessionColumn column) => _visible[column] ? _widths[column] : 0;

    private void SetVisible(SessionColumn column, bool visible)
    {
        if (column == SessionColumn.Id || _visible[column] == visible)
        {
            return;
        }

        _visible[column] = visible;
        OnPropertyChanged($"{column}Visible");
        OnPropertyChanged($"{column}Width");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string HeaderFor(SessionColumn column, string label) =>
        _sort.Column == column && !_sort.IsDefault ? $"{label} {(_sort.Descending ? "▼" : "▲")}" : label;
}
