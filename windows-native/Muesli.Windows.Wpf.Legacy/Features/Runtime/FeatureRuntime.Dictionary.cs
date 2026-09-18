using System.Windows;
using Muesli.Windows.Services;

namespace Muesli.Windows;

public sealed partial class FeatureRuntime
{
private void AddDictionaryEntry_Click(object sender, RoutedEventArgs e)
{
    var phrase = DictionaryPhraseBox.Text.Trim();
    var replacement = DictionaryReplacementBox.Text.Trim();
    if (string.IsNullOrWhiteSpace(phrase) || string.IsNullOrWhiteSpace(replacement))
    {
        DictationStatus = "Dictionary entry needs both fields";
        return;
    }
    DictionaryEntries.Insert(0, new DictionaryEntryItem(
        $"dictentry_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
        phrase,
        replacement,
        DictionaryThresholdSlider.Value));
    DictionaryPhraseBox.Text = "";
    DictionaryReplacementBox.Text = "";
    SaveDictionary();
    OnPropertyChanged(nameof(HasDictionaryEntries));
}
private void SaveDictionaryEntry_Click(object sender, RoutedEventArgs e)
{
    SaveDictionary();
    DictationStatus = "Dictionary saved";
    OnPropertyChanged(nameof(HasDictionaryEntries));
}
private void DeleteDictionaryEntry_Click(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: DictionaryEntryItem item })
    {
        DictionaryEntries.Remove(item);
        SaveDictionary();
        OnPropertyChanged(nameof(HasDictionaryEntries));
    }
}

private void QueueDictionarySuggestion(DictionarySuggestion suggestion)
{
    if (DictionaryEntries.Any(entry =>
            entry.Phrase.Equals(suggestion.Observed, StringComparison.OrdinalIgnoreCase)
            && entry.Replacement.Equals(suggestion.Replacement, StringComparison.OrdinalIgnoreCase)))
    {
        return;
    }

    if (DictionarySuggestions.Any(existing =>
            existing.Observed.Equals(suggestion.Observed, StringComparison.OrdinalIgnoreCase)
            && existing.Replacement.Equals(suggestion.Replacement, StringComparison.OrdinalIgnoreCase)))
    {
        return;
    }

    DictionarySuggestions.Insert(0, suggestion);
    while (DictionarySuggestions.Count > 50)
    {
        DictionarySuggestions.RemoveAt(DictionarySuggestions.Count - 1);
    }
    SaveSettings();
    OnPropertyChanged(nameof(HasDictionarySuggestions));
}

private void AcceptDictionarySuggestion(DictionarySuggestion suggestion)
{
    if (!DictionaryEntries.Any(entry =>
            entry.Phrase.Equals(suggestion.Observed, StringComparison.OrdinalIgnoreCase)
            && entry.Replacement.Equals(suggestion.Replacement, StringComparison.OrdinalIgnoreCase)))
    {
        DictionaryEntries.Insert(0, new DictionaryEntryItem(new DictionaryEntryRecord
        {
            Id = $"dict_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            Phrase = suggestion.Observed,
            Replacement = suggestion.Replacement,
            MatchingThreshold = 0.90
        }));
        SaveDictionary();
        OnPropertyChanged(nameof(HasDictionaryEntries));
    }

    DismissDictionarySuggestion(suggestion);
}

private void DismissDictionarySuggestion(DictionarySuggestion suggestion)
{
    var existing = DictionarySuggestions.FirstOrDefault(item =>
        item.Observed.Equals(suggestion.Observed, StringComparison.OrdinalIgnoreCase)
        && item.Replacement.Equals(suggestion.Replacement, StringComparison.OrdinalIgnoreCase));
    if (existing is null)
    {
        return;
    }

    DictionarySuggestions.Remove(existing);
    SaveSettings();
    OnPropertyChanged(nameof(HasDictionarySuggestions));
}

private void AcceptDictionarySuggestion_Click(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: DictionarySuggestion suggestion })
    {
        AcceptDictionarySuggestion(suggestion);
    }
}

private void DismissDictionarySuggestion_Click(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: DictionarySuggestion suggestion })
    {
        DismissDictionarySuggestion(suggestion);
    }
}
}
