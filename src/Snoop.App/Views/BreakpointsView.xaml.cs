using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Snoop.Presentation.ViewModels;

namespace Snoop.App.Views;

/// <summary>The Breakpoints tab: pause switches, paused exchanges and their editor.</summary>
public sealed partial class BreakpointsView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(BreakpointsViewModel), typeof(BreakpointsView), new PropertyMetadata(null));

    public BreakpointsView() => InitializeComponent();

    public BreakpointsViewModel? ViewModel
    {
        get => (BreakpointsViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
}
