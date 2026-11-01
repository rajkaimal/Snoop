using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Snoop.Presentation.ViewModels;

namespace Snoop.App.Views;

/// <summary>
/// The Composer tab: saved requests on the left, the request editor and its response on the right.
/// Dialogs (save, rename, import cURL, variables) are built here; the view model does the work.
/// </summary>
public sealed partial class ComposerView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(ComposerViewModel), typeof(ComposerView), new PropertyMetadata(null, OnViewModelChanged));

    private bool _dialogOpen;

    public ComposerView() => InitializeComponent();

    public ComposerViewModel? ViewModel
    {
        get => (ComposerViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (ComposerView)d;
        if (e.OldValue is ComposerViewModel old)
        {
            old.SaveAsRequested -= view.OnSaveAsRequested;
        }

        if (e.NewValue is ComposerViewModel model)
        {
            model.SaveAsRequested += view.OnSaveAsRequested;
        }
    }

    /// <summary>Ctrl+Enter sends from anywhere in the Composer, including the multi-line boxes.</summary>
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        if (ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) && ViewModel?.SendCommand.CanExecute(null) == true)
        {
            e.Handled = true;
            ViewModel.SendCommand.Execute(null);
        }
    }

    private void OnSaveInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel?.SaveCommand.Execute(null);
    }

    private void OnRemoveHeader(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: HeaderRowViewModel row })
        {
            ViewModel?.RemoveHeader(row);
        }
    }

    // ------------------------------------------------------------ Library

    private async void OnLibraryItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (ViewModel is not { } vm || args.InvokedItem is not LibraryItemViewModel item || item.IsFolder)
        {
            return; // folders expand and collapse by themselves
        }

        if (item.Path == vm.CurrentPath && !vm.IsModified)
        {
            return;
        }

        if (await ConfirmDiscardAsync())
        {
            vm.Open(item);
        }
    }

    private async void OnNewRequest(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && await ConfirmDiscardAsync())
        {
            vm.NewRequestCommand.Execute(null);
        }
    }

    private async void OnNewFolder(object sender, RoutedEventArgs e) => await NewFolderAsync(null);

    private async void OnNewFolderHere(object sender, RoutedEventArgs e) => await NewFolderAsync(ItemOf(sender));

    private async Task NewFolderAsync(LibraryItemViewModel? parent)
    {
        var name = await AskForTextAsync("New folder", "Folder name", "New folder", "Create");
        if (name is not null)
        {
            ViewModel?.Library.NewFolder(parent, name);
        }
    }

    private async void OnRenameItem(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item)
        {
            return;
        }

        var name = await AskForTextAsync(item.IsFolder ? "Rename folder" : "Rename request", "Name", item.Name, "Rename");
        if (name is not null && name != item.Name)
        {
            ViewModel?.Library.Rename(item, name);
        }
    }

    private void OnDuplicateItem(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            ViewModel?.Library.Duplicate(item);
        }
    }

    private async void OnDeleteItem(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item)
        {
            return;
        }

        var message = item.IsFolder
            ? $"Delete the folder \"{item.Name}\" and every request in it? This cannot be undone."
            : $"Delete \"{item.Name}\"? This cannot be undone.";
        var dialog = NewDialog(item.IsFolder ? "Delete folder" : "Delete request");
        dialog.Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        dialog.PrimaryButtonText = "Delete";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Close;
        if (await ShowAsync(dialog) == ContentDialogResult.Primary)
        {
            ViewModel?.Library.Delete(item);
        }
    }

    private static LibraryItemViewModel? ItemOf(object sender) => (sender as FrameworkElement)?.Tag as LibraryItemViewModel;

    // ------------------------------------------------------------ Save

    private async void OnSaveAs(object sender, RoutedEventArgs e) => await SaveAsAsync();

    private async void OnSaveAsRequested(object? sender, EventArgs e) => await SaveAsAsync();

    /// <summary>Asks for a name and folder, then saves. False when cancelled.</summary>
    private async Task<bool> SaveAsAsync()
    {
        if (ViewModel is not { } vm)
        {
            return false;
        }

        if (!vm.Library.IsAvailable)
        {
            return false;
        }

        var name = new TextBox { Header = "Name", Text = vm.SuggestedName(), IsSpellCheckEnabled = false };
        name.Loaded += (_, _) =>
        {
            name.Focus(FocusState.Programmatic);
            name.SelectAll();
        };
        var choices = vm.Library.FolderChoices.ToList();
        var current = vm.CurrentPath is { } path ? Snoop.Presentation.Composer.RequestLibrary.ParentOf(path) : null;
        var folder = new ComboBox
        {
            Header = "Folder",
            ItemsSource = choices,
            SelectedIndex = Math.Max(0, current is null ? 0 : choices.IndexOf(current)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var dialog = NewDialog("Save request");
        dialog.Content = new StackPanel { Spacing = 12, MinWidth = 340, Children = { name, folder } };
        dialog.PrimaryButtonText = "Save";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        if (await ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return false;
        }

        return vm.SaveAs(name.Text, Snoop.Presentation.ViewModels.RequestLibraryViewModel.FolderFromChoice(folder.SelectedItem as string));
    }

    /// <summary>When there are unsaved edits, asks whether to save them first. False means stay.</summary>
    private async Task<bool> ConfirmDiscardAsync()
    {
        if (ViewModel is not { } vm || !vm.IsModified || IsEmptyRequest(vm))
        {
            return true;
        }

        var dialog = NewDialog("Save changes?");
        dialog.Content = new TextBlock
        {
            Text = vm.CurrentPath is null
                ? "This request has not been saved. Save it before opening another one?"
                : $"\"{vm.CurrentName}\" has changes that are not saved.",
            TextWrapping = TextWrapping.Wrap,
        };
        dialog.PrimaryButtonText = "Save";
        dialog.SecondaryButtonText = "Don't save";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        switch (await ShowAsync(dialog))
        {
            case ContentDialogResult.Primary:
                if (vm.CurrentPath is not null)
                {
                    vm.SaveCommand.Execute(null);
                    return !vm.IsModified;
                }

                return await SaveAsAsync();
            case ContentDialogResult.Secondary:
                return true;
            default:
                return false;
        }
    }

    private static bool IsEmptyRequest(ComposerViewModel vm) =>
        vm.CurrentPath is null && vm.Url.Trim() is "" or "https://" or "http://" && vm.BodyText.Length == 0 && vm.Headers.All(h => h.IsBlank);

    // ------------------------------------------------------------ cURL and variables

    private async void OnImportCurl(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || !await ConfirmDiscardAsync())
        {
            return;
        }

        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 220,
            MinWidth = 480,
            FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
            FontSize = 12,
            IsSpellCheckEnabled = false,
            PlaceholderText = "curl 'https://api.example.com/items' -H 'Accept: application/json'",
        };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        var help = new TextBlock
        {
            Text = "Paste a command from your browser's developer tools (Copy as cURL, bash or cmd) or from API docs.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Snoop.App.Helpers.UiHelpers.MessageBrush(isError: false),
        };

        var dialog = NewDialog("Import cURL");
        dialog.Content = new StackPanel { Spacing = 8, Children = { help, box } };
        dialog.PrimaryButtonText = "Import";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        if (await ShowAsync(dialog) == ContentDialogResult.Primary && box.Text.Trim().Length > 0)
        {
            vm.ImportCurl(box.Text);
        }
    }

    private async void OnEditVariables(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var environments = vm.Environments;
        environments.BeginEdit();
        var box = new TextBox
        {
            Text = environments.EditText,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 280,
            MinWidth = 480,
            FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
            FontSize = 12,
            IsSpellCheckEnabled = false,
            PlaceholderText = "token = abc123\n\n[Local]\nbaseUrl = http://localhost:5000\n\n[Staging]\nbaseUrl = https://staging.example.com",
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        var help = new TextBlock
        {
            Text = Snoop.Presentation.ViewModels.EnvironmentsViewModel.EditorHelp,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Snoop.App.Helpers.UiHelpers.MessageBrush(isError: false),
        };
        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Snoop.App.Helpers.UiHelpers.MessageBrush(isError: true),
            Visibility = Visibility.Collapsed,
        };

        var dialog = NewDialog("Variables and environments");
        dialog.Content = new StackPanel { Spacing = 8, MaxWidth = 560, Children = { help, box, error } };
        dialog.PrimaryButtonText = "Save";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            environments.EditText = box.Text;
            if (!environments.ApplyEdit())
            {
                error.Text = environments.EditErrorText;
                error.Visibility = Visibility.Visible;
                args.Cancel = true; // keep the dialog open to fix it
            }
        };
        await ShowAsync(dialog);
    }

    // ------------------------------------------------------------ Dialog helpers

    private async Task<string?> AskForTextAsync(string title, string header, string initial, string action)
    {
        var box = new TextBox { Header = header, Text = initial, MinWidth = 320, IsSpellCheckEnabled = false };
        box.Loaded += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        var dialog = NewDialog(title);
        dialog.Content = box;
        dialog.PrimaryButtonText = action;
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        return await ShowAsync(dialog) == ContentDialogResult.Primary && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }

    private ContentDialog NewDialog(string title) => new()
    {
        Title = title,
        XamlRoot = XamlRoot,
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
    };

    /// <summary>Shows one dialog at a time (WinUI allows only one open dialog).</summary>
    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (_dialogOpen || XamlRoot is null)
        {
            return ContentDialogResult.None;
        }

        _dialogOpen = true;
        try
        {
            return await dialog.ShowAsync();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return ContentDialogResult.None; // another dialog (e.g. Settings) is already open
        }
        finally
        {
            _dialogOpen = false;
        }
    }
}
