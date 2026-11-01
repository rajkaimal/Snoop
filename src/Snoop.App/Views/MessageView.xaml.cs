using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Snoop.App.Helpers;
using Snoop.Presentation.ViewModels;

namespace Snoop.App.Views;

/// <summary>Shows one side of an exchange: headers, query parameters, raw text and body.</summary>
public sealed partial class MessageView : UserControl
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(MessageViewModel), typeof(MessageView), new PropertyMetadata(null, OnMessageChanged));

    /// <summary>Search text to highlight in header and parameter rows.</summary>
    public static readonly DependencyProperty HighlightTextProperty = DependencyProperty.Register(
        nameof(HighlightText), typeof(string), typeof(MessageView), new PropertyMetadata(string.Empty, (d, _) => ((MessageView)d).HighlightRealizedRows()));

    public MessageView()
    {
        InitializeComponent();
        HeadersPanel.ContainerContentChanging += OnRowContentChanging;
        ParamsPanel.ContainerContentChanging += OnRowContentChanging;
        ActualThemeChanged += (_, _) => RenderColoredBody();
        Loaded += (_, _) => RenderColoredBody(); // in case the message arrived before the view was built
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

    private static void OnMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (MessageView)d;
        if (e.OldValue is MessageViewModel old)
        {
            old.PropertyChanged -= view.OnMessagePropertyChanged;
        }

        if (e.NewValue is MessageViewModel message)
        {
            message.PropertyChanged += view.OnMessagePropertyChanged;
        }

        view.RenderColoredBody();
    }

    private void OnMessagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MessageViewModel.BodySpans) or "" or null)
        {
            RenderColoredBody();
        }
    }

    /// <summary>Fills the coloured body view: one run per span, coloured for the current theme.</summary>
    private void RenderColoredBody()
    {
        if (ColoredBody is null)
        {
            return;
        }

        ColoredBody.Blocks.Clear();
        if (Message is not { } message || message.BodySpans is not { } spans)
        {
            return;
        }

        var text = message.BodyText;
        var dark = ActualTheme == ElementTheme.Dark;
        var paragraph = new Paragraph();
        foreach (var span in spans)
        {
            if (span.Start + span.Length > text.Length)
            {
                break;
            }

            var run = new Run { Text = text.Substring(span.Start, span.Length).Replace("\r\n", "\n", StringComparison.Ordinal) };
            if (SyntaxColors.For(span.Kind, dark) is { } brush)
            {
                run.Foreground = brush;
            }

            paragraph.Inlines.Add(run);
        }

        ColoredBody.Blocks.Add(paragraph);
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
