using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Snoop.Presentation.ViewModels;

namespace Snoop.App.Views;

/// <summary>The Settings tab. Bound to the main view model for its commands (apply, certificate actions).</summary>
public sealed partial class SettingsView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(MainViewModel), typeof(SettingsView), new PropertyMetadata(null));

    public SettingsView() => InitializeComponent();

    public MainViewModel? ViewModel
    {
        get => (MainViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
}
