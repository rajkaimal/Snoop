using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Snoop.App.Helpers;
using Snoop.Presentation.ViewModels;

namespace Snoop.App.Views;

/// <summary>Shows one side of an exchange: headers, query parameters, raw text and body.</summary>
public sealed partial class MessageView : UserControl
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(MessageViewModel), typeof(MessageView), new PropertyMetadata(null));

    /// <summary>Search text to highlight in header and parameter rows.</summary>
    public static readonly DependencyProperty HighlightTextProperty = DependencyProperty.Register(
        nameof(HighlightText), typeof(string), typeof(MessageView), new PropertyMetadata(string.Empty, (d, _) => ((MessageView)d).HighlightRealizedRows()));

    public MessageView()
    {
        InitializeComponent();
        HeadersPanel.ContainerContentChanging += OnRowContentChanging;
        ParamsPanel.ContainerContentChanging += OnRowContentChanging;
        Loaded += (_, _) =>
        {
            // Query parameters only make sense for requests.
            ParamsTab.Visibility = ShowParameters ? Visibility.Visible : Visibility.Collapsed;
        };
    }

    /// <summary>Shows the panel for the selected tab.</summary>
    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        // Can run while the page is still being built, before the panels exist.
        if (HeadersPanel is null || ParamsPanel is null || RawPanel is null || BodyPanel is null)
        {
            return;
        }

        var selected = sender.SelectedItem;
        HeadersPanel.Visibility = ReferenceEquals(selected, HeadersTab) ? Visibility.Visible : Visibility.Collapsed;
        ParamsPanel.Visibility = ReferenceEquals(selected, ParamsTab) ? Visibility.Visible : Visibility.Collapsed;
        RawPanel.Visibility = ReferenceEquals(selected, RawTab) ? Visibility.Visible : Visibility.Collapsed;
        BodyPanel.Visibility = ReferenceEquals(selected, BodyTab) ? Visibility.Visible : Visibility.Collapsed;
    }

    public MessageViewModel? Message
    {
        get => (MessageViewModel?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>Show the Params tab (requests).</summary>
    public bool ShowParameters { get; set; }

    public string HighlightText
    {
        get => (string)GetValue(HighlightTextProperty);
        set => SetValue(HighlightTextProperty, value ?? string.Empty);
    }

    private void OnRowContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue)
        {
            Highlight(args.ItemContainer?.ContentTemplateRoot);
        }
    }

    private void HighlightRealizedRows()
    {
        foreach (var panel in new[] { HeadersPanel, ParamsPanel })
        {
            for (var i = 0; i < panel.Items.Count; i++)
            {
                if (panel.ContainerFromIndex(i) is ListViewItem item)
                {
                    Highlight(item.ContentTemplateRoot);
                }
            }
        }
    }

    /// <summary>The row template is a Grid of two TextBlocks (name, value).</summary>
    private void Highlight(UIElement? root)
    {
        if (root is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is TextBlock text)
                {
                    SearchHighlight.SetQuery(text, HighlightText);
                }
            }
        }
    }
}
