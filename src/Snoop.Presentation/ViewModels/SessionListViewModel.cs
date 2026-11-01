using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Snoop.Core.Sessions;
using Snoop.Inspection.Filtering;
using Snoop.Presentation.Mvvm;
using Snoop.Presentation.Services;

namespace Snoop.Presentation.ViewModels;

/// <summary>
/// The live session grid. Store events arrive on proxy threads and are queued; the UI applies
/// them in batches (<see cref="ApplyPendingChanges"/>, a few times a second) so a busy browser
/// cannot flood the UI thread. Applies the current <see cref="Filter"/>.
/// </summary>
public sealed class SessionListViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(250);

    private readonly SessionStore _store;
    private readonly ConcurrentQueue<SessionChangedEventArgs> _pending = new();
    private readonly Dictionary<long, SessionRowViewModel> _rows = [];
    private readonly List<SessionRowViewModel> _ordered = [];
    private readonly HashSet<long> _visible = [];
    private readonly IDisposable? _timer;
    private ObservableCollection<SessionRowViewModel> _visibleRows = [];
    private SessionRowViewModel? _selectedRow;
    private IReadOnlyList<SessionRowViewModel> _selectedRows = [];
    private HashSet<long> _selectedIds = [];
    private bool _selectionPruned;
    private SessionFilter _filter = SessionFilter.None;
    private bool _autoScroll = true;
    private SessionSort _sort = SessionSort.Default;
    private bool _isUpdating;

    public SessionListViewModel(SessionStore store, IUiDispatcher? dispatcher)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _store.SessionChanged += OnSessionChanged;
        foreach (var session in store.Snapshot())
        {
            AddRow(session);
        }

        RebuildVisible();
        _timer = dispatcher?.StartTimer(BatchInterval, ApplyPendingChanges);
    }

    /// <summary>Raised when the selection changes, including to null.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised after a batch adds rows and auto-scroll is on; the view scrolls to this row.</summary>
    public event EventHandler<SessionRowViewModel>? ScrollRequested;

    /// <summary>Raised when the selected session changed (e.g. its response arrived).</summary>
    public event EventHandler? SelectedSessionUpdated;

    /// <summary>Raised after rows moved (sorting); the view re-selects <see cref="SelectedRow"/>.</summary>
    public event EventHandler? SelectionRestoreRequested;

    /// <summary>Column widths and visibility, shared by the header and every row.</summary>
    public SessionColumnsViewModel Columns { get; } = new();

    /// <summary>
    /// True while rows are being added, moved or removed. The list control reports interim selection
    /// changes then; the view ignores them.
    /// </summary>
    public bool IsUpdating => _isUpdating;

    /// <summary>The sort order. Changing it re-sorts the shown rows.</summary>
    public SessionSort Sort
    {
        get => _sort;
        set
        {
            if (_sort == value)
            {
                return;
            }

            _sort = value;
            Columns.Sort = value;
            OnPropertyChanged();
            RebuildVisible();
        }
    }

    /// <summary>Sorts by <paramref name="column"/>; again reverses; a third time goes back to arrival order.</summary>
    public void SortBy(SessionColumn column)
    {
        if (_sort.Column != column || _sort.IsDefault)
        {
            Sort = new SessionSort(column, column == SessionColumn.Id);
        }
        else if (!_sort.Descending == (column != SessionColumn.Id))
        {
            Sort = _sort with { Descending = !_sort.Descending };
        }
        else
        {
            Sort = SessionSort.Default;
        }
    }

    /// <summary>The rows the grid shows. Replaced (not cleared) when the filter changes, which is much faster for big lists.</summary>
    public ObservableCollection<SessionRowViewModel> Rows
    {
        get => _visibleRows;
        private set => SetProperty(ref _visibleRows, value);
    }

    public SessionRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Every selected row (the list allows Ctrl/Shift selection). <see cref="SelectedRow"/> is the one
    /// the inspectors show. Rows that are removed or hidden drop out.
    /// </summary>
    public IReadOnlyList<SessionRowViewModel> SelectedRows
    {
        get => _selectedRows;
        set
        {
            _selectedRows = value ?? [];
            _selectedIds = [.. _selectedRows.Select(r => r.Id)];
            _selectionPruned = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedCount));
        }
    }

    public int SelectedCount => _selectedRows.Count;

    /// <summary>The selected sessions in the order shown; the inspected one alone when the list reports none.</summary>
    public IReadOnlyList<Session> SelectedSessions
    {
        get
        {
            if (_selectedRows.Count == 0)
            {
                return _selectedRow is { } row ? [row.Session] : [];
            }

            if (_selectedRows.Count == 1)
            {
                return [_selectedRows[0].Session];
            }

            return [.. _visibleRows.Where(r => _selectedIds.Contains(r.Id)).Select(r => r.Session)];
        }
    }

    /// <summary>
    /// Selects the shown row <paramref name="step"/> places after the selected one, wrapping around
    /// (search: Enter for the next match, Shift+Enter for the previous). Returns it, or null when nothing is shown.
    /// </summary>
    public SessionRowViewModel? SelectAdjacent(int step)
    {
        if (_visibleRows.Count == 0)
        {
            return null;
        }

        var index = _selectedRow is null ? -1 : _visibleRows.IndexOf(_selectedRow);
        int next;
        if (index < 0)
        {
            next = step >= 0 ? 0 : _visibleRows.Count - 1;
        }
        else
        {
            next = ((index + step) % _visibleRows.Count + _visibleRows.Count) % _visibleRows.Count;
        }

        var row = _visibleRows[next];
        SelectedRows = [row];
        SelectedRow = row;
        return row;
    }

    public SessionFilter Filter
    {
        get => _filter;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _filter, value))
            {
                RebuildVisible();
            }
        }
    }

    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetProperty(ref _autoScroll, value);
    }

    public int TotalCount => _ordered.Count;

    public int VisibleCount => _visibleRows.Count;

    /// <summary>The sessions currently shown, in order.</summary>
    public IReadOnlyList<Session> VisibleSessions => [.. _visibleRows.Select(r => r.Session)];

    /// <summary>Applies queued store changes. Call on the UI thread.</summary>
    public void ApplyPendingChanges()
    {
        if (_pending.IsEmpty)
        {
            return;
        }

        var refreshed = new HashSet<long>();
        SessionRowViewModel? lastAdded = null;
        var selectedChanged = false;
        var moved = false;
        _isUpdating = true;
        try
        {
            moved = ApplyQueued(refreshed, ref lastAdded, ref selectedChanged);
        }
        finally
        {
            _isUpdating = false;
        }

        if (_selectionPruned)
        {
            SelectedRows = [.. _selectedRows.Where(r => _selectedIds.Contains(r.Id))];
        }

        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(VisibleCount));
        if (moved && _selectedRow is not null)
        {
            SelectionRestoreRequested?.Invoke(this, EventArgs.Empty);
        }

        if (selectedChanged)
        {
            SelectedSessionUpdated?.Invoke(this, EventArgs.Empty);
        }

        if (lastAdded is not null && _autoScroll)
        {
            ScrollRequested?.Invoke(this, lastAdded);
        }
    }

    private bool ApplyQueued(HashSet<long> refreshed, ref SessionRowViewModel? lastAdded, ref bool selectedChanged)
    {
        var moved = false;
        while (_pending.TryDequeue(out var change))
        {
            var session = change.Session;
            switch (change.Change)
            {
                case SessionChange.Added:
                    var row = AddRow(session);
                    if (row is not null && _filter.Matches(session))
                    {
                        _visible.Add(session.Id);
                        var index = FindInsertIndex(row);
                        _visibleRows.Insert(index, row);
                        moved |= index < _visibleRows.Count - 1;
                        lastAdded = row;
                    }

                    break;

                case SessionChange.Updated:
                case SessionChange.Finished:
                    refreshed.Add(session.Id);
                    break;

                case SessionChange.Removed:
                    RemoveRow(session.Id);
                    refreshed.Remove(session.Id);
                    break;
            }
        }

        foreach (var id in refreshed)
        {
            if (!_rows.TryGetValue(id, out var row))
            {
                continue;
            }

            row.Refresh();
            moved |= UpdateVisibility(row);
            selectedChanged |= ReferenceEquals(row, _selectedRow);
        }

        return moved;
    }

    /// <summary>Removes sessions from the store (and so from the grid).</summary>
    public void Remove(IEnumerable<SessionRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows.ToList())
        {
            _store.Remove(row.Id);
        }

        ApplyPendingChanges();
    }

    /// <summary>Clears every session.</summary>
    public void Clear()
    {
        _store.Clear();
        ApplyPendingChanges();
    }

    public void Dispose()
    {
        _store.SessionChanged -= OnSessionChanged;
        _timer?.Dispose();
    }

    private void OnSessionChanged(object? sender, SessionChangedEventArgs e) => _pending.Enqueue(e);

    private SessionRowViewModel? AddRow(Session session)
    {
        if (_rows.ContainsKey(session.Id))
        {
            return null;
        }

        var row = new SessionRowViewModel(session, Columns);
        _rows[session.Id] = row;
        _ordered.Add(row); // Ids increase over time, so appending keeps the list ordered.
        return row;
    }

    private void RemoveRow(long id)
    {
        if (!_rows.Remove(id, out var row))
        {
            return;
        }

        _ordered.Remove(row);
        if (_visible.Remove(id))
        {
            _visibleRows.Remove(row);
        }

        // Many rows can go at once (Delete on a big selection); prune the selection once afterwards.
        _selectionPruned |= _selectedIds.Remove(id);

        if (ReferenceEquals(row, _selectedRow))
        {
            SelectedRow = null;
        }
    }

    /// <summary>Shows, hides or (when sorted) moves a changed row. Returns true when rows moved.</summary>
    private bool UpdateVisibility(SessionRowViewModel row)
    {
        var shouldShow = _filter.Matches(row.Session);
        var isShown = _visible.Contains(row.Id);
        if (shouldShow && !isShown)
        {
            _visible.Add(row.Id);
            _visibleRows.Insert(FindInsertIndex(row), row);
            return true;
        }

        if (!shouldShow && isShown)
        {
            _visible.Remove(row.Id);
            _visibleRows.Remove(row);
            _selectionPruned |= _selectedIds.Remove(row.Id); // hidden rows leave the selection
            return true;
        }

        if (!isShown || _sort.Column == SessionColumn.Id)
        {
            return false;
        }

        // Sorted by something that may have changed (status, size, time): keep the row in order.
        var index = _visibleRows.IndexOf(row);
        var inOrder = (index == 0 || Compare(_visibleRows[index - 1], row) <= 0)
                      && (index == _visibleRows.Count - 1 || Compare(row, _visibleRows[index + 1]) <= 0);
        if (inOrder)
        {
            return false;
        }

        _visibleRows.RemoveAt(index);
        _visibleRows.Insert(FindInsertIndex(row), row);
        return true;
    }

    /// <summary>Where <paramref name="row"/> belongs in the shown rows (after equal ones).</summary>
    private int FindInsertIndex(SessionRowViewModel row)
    {
        int low = 0, high = _visibleRows.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (Compare(_visibleRows[mid], row) <= 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private int Compare(SessionRowViewModel a, SessionRowViewModel b)
    {
        var result = _sort.Column switch
        {
            SessionColumn.Id => 0,
            SessionColumn.Result => a.ResultSortKey.CompareTo(b.ResultSortKey),
            SessionColumn.Size => a.SizeSortKey.CompareTo(b.SizeSortKey),
            SessionColumn.Time => a.DurationSortKey.CompareTo(b.DurationSortKey),
            SessionColumn.Type => string.Compare(a.TypeBadge, b.TypeBadge, StringComparison.OrdinalIgnoreCase),
            SessionColumn.Method => string.Compare(a.Method, b.Method, StringComparison.OrdinalIgnoreCase),
            SessionColumn.Protocol => string.Compare(a.Protocol, b.Protocol, StringComparison.OrdinalIgnoreCase),
            SessionColumn.Host => string.Compare(a.Host, b.Host, StringComparison.OrdinalIgnoreCase),
            SessionColumn.Path => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase),
            SessionColumn.Process => string.Compare(a.Process, b.Process, StringComparison.OrdinalIgnoreCase),
            _ => 0,
        };

        if (result == 0)
        {
            result = a.Id.CompareTo(b.Id);
        }

        return _sort.Descending ? -result : result;
    }

    private void RebuildVisible()
    {
        _visible.Clear();
        var rows = new List<SessionRowViewModel>();
        foreach (var row in _ordered)
        {
            if (_filter.Matches(row.Session))
            {
                rows.Add(row);
                _visible.Add(row.Id);
            }
        }

        if (!_sort.IsDefault)
        {
            rows.Sort(Compare);
        }

        var selected = _selectedRow;
        Rows = new ObservableCollection<SessionRowViewModel>(rows);
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(TotalCount));
        if (_selectionPruned || _selectedRows.Any(r => !_visible.Contains(r.Id)))
        {
            SelectedRows = [.. _selectedRows.Where(r => _visible.Contains(r.Id))];
        }

        if (selected is not null && !_visible.Contains(selected.Id))
        {
            SelectedRow = null;
        }
    }
}
