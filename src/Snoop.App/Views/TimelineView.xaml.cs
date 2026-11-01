using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Snoop.Presentation.ViewModels;

namespace Snoop.App.Views;

/// <summary>The Timeline tab: a waterfall of the shown sessions. Clicking a bar selects that session.</summary>
public sealed partial class TimelineView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(MainViewModel), typeof(TimelineView), new PropertyMetadata(null));

    public TimelineView() => InitializeComponent();

    public MainViewModel? ViewModel
    {
        get => (MainViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TimelineRow row)
        {
            ViewModel?.SelectSession(row.Id);
        }
    }
}
