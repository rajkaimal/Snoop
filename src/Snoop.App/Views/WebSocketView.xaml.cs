using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Snoop.Presentation.ViewModels;

namespace Snoop.App.Views;

/// <summary>The WebSocket tab: messages of the selected connection and the selected message in full.</summary>
public sealed partial class WebSocketView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(WebSocketViewModel), typeof(WebSocketView), new PropertyMetadata(null));

    public WebSocketView() => InitializeComponent();

    public WebSocketViewModel? ViewModel
    {
        get => (WebSocketViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
}
