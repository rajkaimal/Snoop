using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Snoop.App.Services;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Snoop.Hosting;
using Snoop.Hosting.Rules;
using Snoop.Presentation.Settings;
using Snoop.Presentation.ViewModels;

namespace Snoop.App;

/// <summary>The main window. Only view wiring lives here; behaviour is in <see cref="MainViewModel"/>.</summary>
public sealed partial class MainWindow : Window
{
    private bool _closingHandled;
    private bool _rebindingRows;
    private bool _syncingFromList;
    private SessionRowViewModel? _draggedRow;
    private bool _syncingTabs;
    private bool _settingsOpen;
    private double? _listWidth;

    public MainWindow(CaptureHost host)
    {
        ViewModel = new MainViewModel(
            host,
            new WinUiDispatcher(DispatcherQueue),
            new WinUiClipboard(),
            new WinUiFileSaveService(this),
            AppSettings.Load(),
            AppSettings.DefaultPath,
            new WinUiFileOpenService(this),
            RulesFile.DefaultPath,
            Snoop.Core.SnoopPaths.AppDataDirectory);

        InitializeComponent();

        AppWindow.Title = "Snoop";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1500, 900));
        AppWindow.Closing += OnClosing;

        ViewModel.Sessions.ScrollRequested += (_, row) => SessionList.ScrollIntoView(row);
        ViewModel.Sessions.SelectionRestoreRequested += (_, _) =>
        {
            // Sorting moved rows; the list may have dropped its selection while they moved.
            // In Extended mode SelectedItem is the first selected row, not necessarily the inspected one.
            var rows = ViewModel.Sessions.SelectedRows;
            var stale = rows.Count > 1
                ? SessionList.SelectedItems.Count != rows.Count
                : !ReferenceEquals(SessionList.SelectedItem, ViewModel.Sessions.SelectedRow);
            if (stale)
            {
                RestoreListSelection();
            }
        };
        ViewModel.Sessions.PropertyChanged += (_, e) =>
        {
            // A new filter replaces the row collection; keep the selection highlighted if it is still shown.
            // While the list swaps collections it reports "nothing selected"; ignore that, then re-select.
            if (e.PropertyName == nameof(SessionListViewModel.SelectedRow) && !_syncingFromList)
            {
                // Selected from code (e.g. the timeline): make the list show it.
                var row = ViewModel.Sessions.SelectedRow;

                // The inspected row went away but others are still selected: keep them selected.
                if (row is null && ViewModel.Sessions.SelectedRows.Count > 0)
                {
                    return;
                }

                if (!ReferenceEquals(SessionList.SelectedItem, row) || SessionList.SelectedItems.Count > 1)
                {
                    _rebindingRows = true;
                    SessionList.SelectedItem = row;
                    _rebindingRows = false;
                    if (row is not null)
                    {
                        SessionList.ScrollIntoView(row);
                    }

                    // Selecting one row from code replaces a multiple selection.
                    if (ViewModel.Sessions.SelectedRows is not [var only] || !ReferenceEquals(only, row))
                    {
                        ViewModel.Sessions.SelectedRows = row is null ? [] : [row];
                    }
                }
            }

            if (e.PropertyName == nameof(SessionListViewModel.Rows))
            {
                _rebindingRows = true;
                DispatcherQueue.TryEnqueue(() =>
                {
                    _rebindingRows = false;
                    RestoreListSelection();
                });
            }
        };

        ApplyBackdrop();
        ConfigureWindow();
        ViewModel.SettingsRequested += OnSettingsRequested;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.SelectedTabIndex) or nameof(MainViewModel.InspectViewIndex))
            {
                SyncTabs();
            }
        };

        StartAsync();
    }

    // ------------------------------------------------------------ Tabs

    /// <summary>The Inspect switcher changed: tell the view model (which decides what shows).</summary>
    private void OnInspectTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncingTabs || sender.SelectedItem is null)
        {
            return;
        }

        ViewModel.InspectViewIndex = sender.Items.IndexOf(sender.SelectedItem);
        SyncTabs(); // the view model may refuse (WebSocket without a WebSocket session)
    }

    /// <summary>The view model changed the Inspect view (session changed, tool closed): move the switcher.</summary>
    private void SyncTabs()
    {
        var index = ViewModel.InspectViewIndex;
        if (index < 0 || index >= InspectTabs.Items.Count || ReferenceEquals(InspectTabs.SelectedItem, InspectTabs.Items[index]))
        {
            return;
        }

        _syncingTabs = true;
        InspectTabs.SelectedItem = InspectTabs.Items[index];
        _syncingTabs = false;
    }

    /// <summary>Ctrl+1 to Ctrl+6 pick a tool.</summary>
    private void OnToolShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.SelectedToolIndex = (int)sender.Key - (int)Windows.System.VirtualKey.Number1;
        args.Handled = true;
    }

    /// <summary>Double-clicking a session shows it, whichever tool is open.</summary>
    private void OnSessionDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is SessionRowViewModel)
        {
            ViewModel.ShowInspector();
        }
    }

    /// <summary>
    /// The window's title bar becomes part of the content (one surface, Mica behind both), and the
    /// window cannot shrink below the size at which the list and the details card fit.
    /// </summary>
    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // The minimum is in physical pixels, so it follows the display scale (150%, 200%...).
        Root.Loaded += (_, _) =>
        {
            ApplyMinimumSize();
            Root.XamlRoot.Changed += (_, _) => ApplyMinimumSize();
        };
    }

    private void ApplyMinimumSize()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter && Root.XamlRoot is { } root)
        {
            presenter.PreferredMinimumWidth = (int)Math.Ceiling(1000 * root.RasterizationScale);
            presenter.PreferredMinimumHeight = (int)Math.Ceiling(620 * root.RasterizationScale);
        }
    }

    /// <summary>The rail always shows a selection (Ctrl+click would otherwise clear it).</summary>
    private void OnToolRailSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ToolRail.SelectedIndex < 0)
        {
            DispatcherQueue.TryEnqueue(() => ToolRail.SelectedIndex = ViewModel.SelectedToolIndex);
        }
    }

    // ------------------------------------------------------------ Settings, shortcuts, splitter

    private async void OnSettingsRequested(object? sender, EventArgs e)
    {
        if (_settingsOpen || Content.XamlRoot is null)
        {
            return;
        }

        _settingsOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
                Title = "Settings",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                Content = new Views.SettingsView { ViewModel = ViewModel, Height = 560, Width = 640 },
            };
            dialog.Resources["ContentDialogMaxWidth"] = 720.0;
            dialog.Resources["ContentDialogMaxHeight"] = 760.0;
            await dialog.ShowAsync();
        }
        finally
        {
            _settingsOpen = false;
        }
    }

    private void OnFindInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
        args.Handled = true;
    }

    private void OnSaveInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.SaveSnapshotCommand.Execute(null);
        args.Handled = true;
    }

    private void OnOpenInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.LoadSnapshotCommand.Execute(null);
        args.Handled = true;
    }

    /// <summary>Dragging the divider gives the list a fixed width and the details the rest.</summary>
    private void OnSplitterDrag(object sender, DragDeltaEventArgs e)
    {
        // Both columns stay proportional, so shrinking the window later never pushes the details off screen.
        var total = SplitArea.ActualWidth - 6;
        if (total <= 0)
        {
            return;
        }

        _listWidth ??= ListColumn.ActualWidth;
        var width = Math.Clamp(_listWidth.Value + e.HorizontalChange, ListColumn.MinWidth, Math.Max(ListColumn.MinWidth, total - DetailColumn.MinWidth));
        _listWidth = width;
        ListColumn.Width = new GridLength(width, GridUnitType.Star);
        DetailColumn.Width = new GridLength(Math.Max(1, total - width), GridUnitType.Star);
    }

    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e) => _listWidth = null;

    /// <summary>Mica where Windows supports it (Windows 11); a plain theme background elsewhere.</summary>
    private void ApplyBackdrop()
    {
        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
        {
            SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        }
        else if (Application.Current.Resources.TryGetValue("SolidBackgroundFillColorBaseBrush", out var brush) && brush is Microsoft.UI.Xaml.Media.Brush background)
        {
            Root.Background = background;
        }
    }

    public MainViewModel ViewModel { get; }

    private async void StartAsync()
    {
        try
        {
            await ViewModel.InitializeAsync();
        }
#pragma warning disable CA1031 // Shown in the info bar by the view model; never crash on startup.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void OnSessionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebindingRows || ViewModel.Sessions.IsUpdating)
        {
            return;
        }

        var selected = SessionList.SelectedItems.OfType<SessionRowViewModel>().ToList();
        ViewModel.Sessions.SelectedRows = selected;

        // With several rows selected, inspect the one just clicked when it is one of them.
        var primary = e.AddedItems.Count > 0 && e.AddedItems[^1] is SessionRowViewModel added ? added : SessionList.SelectedItem as SessionRowViewModel;
        if (primary is null && selected.Count > 0)
        {
            primary = selected.Contains(ViewModel.Sessions.SelectedRow!) ? ViewModel.Sessions.SelectedRow : selected[^1];
        }

        _syncingFromList = true; // the view model's SelectedRow change must not reset the list's selection
        try
        {
            ViewModel.Sessions.SelectedRow = primary;
        }
        finally
        {
            _syncingFromList = false;
        }
    }

    /// <summary>Re-selects the view model's selection in the list (after rows moved or the filter changed).</summary>
    private void RestoreListSelection()
    {
        _rebindingRows = true;
        try
        {
            var rows = ViewModel.Sessions.SelectedRows;
            if (rows.Count <= 1)
            {
                SessionList.SelectedItem = ViewModel.Sessions.SelectedRow;
                return;
            }

            SessionList.SelectedItems.Clear();
            var shown = ViewModel.Sessions.Rows.ToHashSet();
            foreach (var row in rows)
            {
                if (shown.Contains(row))
                {
                    SessionList.SelectedItems.Add(row);
                }
            }
        }
        finally
        {
            _rebindingRows = false;
        }
    }

    /// <summary>In the search box, Enter selects the next shown session and Shift+Enter the previous one.</summary>
    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var row = ViewModel.Sessions.SelectAdjacent(shift ? -1 : 1);
        if (row is not null)
        {
            SessionList.ScrollIntoView(row);
            if (ViewModel.SelectedTabIndex != MainViewModel.InspectorsTab && ViewModel.SelectedTabIndex != MainViewModel.OverviewTab)
            {
                ViewModel.SelectedTabIndex = MainViewModel.InspectorsTab;
            }
        }

        e.Handled = true;
    }

    /// <summary>Opening the context menu (right-click, Shift+F10, menu key) selects the row it was opened on.</summary>
    private void OnSessionContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        // Keep a multiple selection when the menu opens on one of its rows (Export selected, Remove).
        if ((e.OriginalSource as FrameworkElement)?.DataContext is SessionRowViewModel row && !SessionList.SelectedItems.Contains(row))
        {
            SessionList.SelectedItem = row;
        }
    }

    /// <summary>
    /// Dragging a session: the drag image is a compact pill (method + host and path) rather than the
    /// whole row. Other apps get the URL as text; a drop on the details card opens it in the Composer.
    /// </summary>
    private async void OnRowDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not SessionRowViewModel row)
        {
            args.Cancel = true;
            return;
        }

        _draggedRow = row;
        args.Data.SetText(row.Url);
        args.Data.RequestedOperation = DataPackageOperation.Copy;

        var deferral = args.GetDeferral();
        try
        {
            DragTokenMethod.Text = row.Method;
            DragTokenText.Text = row.Host + (row.Session.Kind == Snoop.Core.Sessions.SessionKind.Connect ? string.Empty : row.Path);
            DragToken.UpdateLayout();

            var snapshot = new RenderTargetBitmap();
            await snapshot.RenderAsync(DragToken);
            var pixels = await snapshot.GetPixelsAsync();
            var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels, BitmapPixelFormat.Bgra8, snapshot.PixelWidth, snapshot.PixelHeight, BitmapAlphaMode.Premultiplied);
            args.DragUI.SetContentFromSoftwareBitmap(bitmap, new Windows.Foundation.Point(14, 12));
        }
#pragma warning disable CA1031 // Any failure just means the standard drag image.
        catch (Exception)
#pragma warning restore CA1031
        {
            args.DragUI.SetContentFromDataPackage();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnRowDropCompleted(UIElement sender, DropCompletedEventArgs args) => _draggedRow = null;

    private void OnInspectorDragOver(object sender, DragEventArgs e)
    {
        if (MainViewModel.CanOpenInComposer(_draggedRow))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Open in Composer";
            e.DragUIOverride.IsCaptionVisible = true;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
            if (_draggedRow is not null)
            {
                e.DragUIOverride.Caption = "Tunnels cannot be replayed";
                e.DragUIOverride.IsCaptionVisible = true;
            }
        }

        e.Handled = true;
    }

    private void OnInspectorDrop(object sender, DragEventArgs e)
    {
        ViewModel.OpenInComposer(_draggedRow);
        _draggedRow = null;
        e.Handled = true;
    }

    /// <summary>A header click sorts by that column.</summary>
    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<SessionColumn>(tag, out var column))
        {
            ViewModel.Sessions.SortBy(column);
        }
    }

    /// <summary>Dragging a header edge resizes the column. Columns right of Path have their grip on the left.</summary>
    private void OnColumnResize(object sender, DragDeltaEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<SessionColumn>(tag, out var column))
        {
            var delta = column is SessionColumn.Size or SessionColumn.Time or SessionColumn.Process ? -e.HorizontalChange : e.HorizontalChange;
            ViewModel.Sessions.Columns.Resize(column, delta);
        }
    }

    /// <summary>Saves the column layout once, when a resize drag ends.</summary>
    private void OnColumnResizeCompleted(object sender, DragCompletedEventArgs e) => ViewModel.Sessions.Columns.CommitResize();

    private void OnSavedFilterClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SavedFilter filter)
        {
            ViewModel.ApplySavedFilter(filter);
        }
    }

    private void OnDeleteSavedFilter(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SavedFilter filter })
        {
            ViewModel.DeleteSavedFilter(filter);
        }
    }

    /// <summary>Delete removes the selected session, but only while the session list has focus.</summary>
    private void OnSessionListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Delete && ViewModel.RemoveSelectedCommand.CanExecute(null))
        {
            ViewModel.RemoveSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Stops capture and restores the system proxy before the window really closes.</summary>
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closingHandled)
        {
            return;
        }

        args.Cancel = true;
        _closingHandled = true;
        try
        {
            await ViewModel.ShutdownAsync();
        }
        finally
        {
            Close();
        }
    }
}
