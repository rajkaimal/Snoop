using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Snoop.Presentation.ViewModels;

namespace Snoop.App.Views;

/// <summary>The Compare tab: a line diff of two sessions.</summary>
public sealed partial class CompareView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(CompareViewModel), typeof(CompareView), new PropertyMetadata(null));

    public CompareView() => InitializeComponent();

    public CompareViewModel? ViewModel
    {
        get => (CompareViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
}
