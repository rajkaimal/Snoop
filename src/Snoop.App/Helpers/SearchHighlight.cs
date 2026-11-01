using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Snoop.Inspection.Filtering;

namespace Snoop.App.Helpers;

/// <summary>
/// Highlights the search words in a TextBlock: <c>helpers:SearchHighlight.Query="{x:Bind ...}"</c>.
/// Follows changes to both the query and the TextBlock's text (rows are recycled).
/// </summary>
public static class SearchHighlight
{
    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query", typeof(string), typeof(SearchHighlight), new PropertyMetadata(string.Empty, OnQueryChanged));

    /// <summary>Set once the TextBlock's Text is being watched.</summary>
    private static readonly DependencyProperty WatchingProperty = DependencyProperty.RegisterAttached(
        "Watching", typeof(bool), typeof(SearchHighlight), new PropertyMetadata(false));

    private static SolidColorBrush? _background;

    public static string GetQuery(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string)element.GetValue(QueryProperty);
    }

    public static void SetQuery(DependencyObject element, string value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(QueryProperty, value ?? string.Empty);
    }

    private static void OnQueryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text)
        {
            return;
        }

        if (!(bool)text.GetValue(WatchingProperty))
        {
            text.SetValue(WatchingProperty, true);
            text.RegisterPropertyChangedCallback(TextBlock.TextProperty, (sender, _) => Apply((TextBlock)sender));
            text.ActualThemeChanged += (sender, _) => Apply((TextBlock)sender); // the highlight copies the text colour
        }

        Apply(text);
    }

    private static void Apply(TextBlock block)
    {
        block.TextHighlighters.Clear();
        var words = SessionFilter.SearchWords(GetQuery(block));
        var text = block.Text;
        if (words.Count == 0 || string.IsNullOrEmpty(text))
        {
            return;
        }

        // Mark every matched character, then turn runs of marks into ranges (words may overlap).
        var marked = new bool[text.Length];
        var any = false;
        foreach (var word in words)
        {
            var start = 0;
            while (start < text.Length)
            {
                var index = text.IndexOf(word, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    break;
                }

                Array.Fill(marked, true, index, word.Length);
                any = true;
                start = index + word.Length;
            }
        }

        if (!any)
        {
            return;
        }

        // A soft yellow that reads on both light and dark backgrounds; the text keeps its own colour.
        _background ??= new SolidColorBrush(Windows.UI.Color.FromArgb(0x70, 0xFF, 0xC8, 0x00));
        var highlighter = new TextHighlighter { Background = _background, Foreground = block.Foreground };
        for (var i = 0; i < marked.Length;)
        {
            if (!marked[i])
            {
                i++;
                continue;
            }

            var end = i;
            while (end < marked.Length && marked[end])
            {
                end++;
            }

            highlighter.Ranges.Add(new TextRange { StartIndex = i, Length = end - i });
            i = end;
        }

        block.TextHighlighters.Add(highlighter);
    }
}
