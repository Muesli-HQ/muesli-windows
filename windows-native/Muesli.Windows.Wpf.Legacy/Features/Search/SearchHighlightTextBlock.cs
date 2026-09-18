using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Features.Search;

public sealed class SearchHighlightTextBlock : TextBlock
{
    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(
        nameof(SourceText),
        typeof(string),
        typeof(SearchHighlightTextBlock),
        new PropertyMetadata(string.Empty, OnContentChanged));

    public static readonly DependencyProperty QueryProperty = DependencyProperty.Register(
        nameof(Query),
        typeof(string),
        typeof(SearchHighlightTextBlock),
        new PropertyMetadata(string.Empty, OnContentChanged));

    public SearchHighlightTextBlock()
    {
        TextWrapping = TextWrapping.Wrap;
        TextTrimming = TextTrimming.CharacterEllipsis;
        Loaded += (_, _) => Rebuild();
        DataContextChanged += (_, _) => Rebuild();
    }

    public string SourceText
    {
        get => (string)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public string Query
    {
        get => (string)GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SearchHighlightTextBlock)d).Rebuild();

    private void Rebuild()
    {
        Inlines.Clear();
        var text = SourceText ?? "";
        var query = (Query ?? "").Trim();
        var secondary = TryBrush("TextSecondaryBrush") ?? Foreground;
        var accent = TryBrush("AccentBlueBrush") ?? Foreground;

        if (string.IsNullOrEmpty(text))
            return;

        if (string.IsNullOrEmpty(query))
        {
            Inlines.Add(new Run(Truncate(text, 160)) { Foreground = secondary });
            return;
        }

        var matchIndex = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (matchIndex < 0)
        {
            Inlines.Add(new Run(Truncate(text, 120)) { Foreground = secondary });
            return;
        }

        const int context = 60;
        var matchEnd = matchIndex + query.Length;
        var start = Math.Max(0, matchIndex - context);
        var end = Math.Min(text.Length, matchEnd + context);
        var prefix = start > 0 ? "..." : "";
        var suffix = end < text.Length ? "..." : "";
        var before = text[start..matchIndex];
        var match = text[matchIndex..matchEnd];
        var after = text[matchEnd..end];

        Inlines.Add(new Run(prefix + before) { Foreground = secondary });
        Inlines.Add(new Run(match) { Foreground = accent, FontWeight = FontWeights.SemiBold });
        Inlines.Add(new Run(after + suffix) { Foreground = secondary });
    }

    private System.Windows.Media.Brush? TryBrush(string key) =>
        TryFindResource(key) as System.Windows.Media.Brush
        ?? System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush;

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
}

public sealed class MeetingSearchSnippetConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var title = values.ElementAtOrDefault(0) as string ?? "";
        var transcript = values.ElementAtOrDefault(1) as string ?? "";
        var notes = values.ElementAtOrDefault(2) as string ?? "";
        var summary = values.ElementAtOrDefault(3) as string ?? "";
        var query = values.ElementAtOrDefault(4) as string ?? "";
        return ProductionInMemorySearchMatch.MeetingSnippet(title, summary, transcript, notes, query);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
