namespace Muesli.Windows.Services;

/// <summary>
/// macOS <c>addDictionarySuggestion</c> / accept / dismiss: the rules for the suggestion list in
/// settings, kept pure so the page, the post-paste prompt and the tests share them.
/// </summary>
public static class DictionarySuggestionBook
{
    public const int MaxSuggestions = 50;
    public const int MaxDismissedKeys = 200;
    /// <summary>Learned from one observed edit, so stricter than a hand-made entry (macOS 0.92).</summary>
    public const double AcceptedThreshold = 0.92;

    /// <summary>
    /// Records a detected correction. Returns the stored suggestion to prompt for, or null when the
    /// feature is off, the pair was ignored before, or it is already a dictionary entry.
    /// </summary>
    public static (MuesliSettings Settings, DictionarySuggestion? Prompt) Add(
        MuesliSettings settings, IEnumerable<DictionaryEntryRecord> words, DictionarySuggestion suggestion)
    {
        var observed = suggestion.Observed.Trim();
        var replacement = suggestion.Replacement.Trim();
        if (!settings.EnableDictionaryCorrectionPrompts || observed.Length == 0 || replacement.Length == 0 || observed == replacement)
        {
            return (settings, null);
        }

        var key = DictionarySuggestion.KeyFor(observed, replacement);
        if (settings.DismissedDictionarySuggestionKeys.Contains(key) || words.Any(word => KeyOf(word) == key))
        {
            return (settings, null);
        }

        var list = settings.DictionarySuggestions.ToList();
        var index = list.FindIndex(item => item.Key == key);
        DictionarySuggestion stored;
        if (index >= 0)
        {
            var existing = list[index];
            stored = existing with
            {
                OccurrenceCount = existing.OccurrenceCount + 1,
                AppContext = existing.AppContext.Length == 0 ? suggestion.AppContext : existing.AppContext
            };
            list.RemoveAt(index);
        }
        else
        {
            stored = new DictionarySuggestion(observed, replacement) { AppContext = suggestion.AppContext };
        }
        list.Insert(0, stored);
        return (settings with { DictionarySuggestions = list.Take(MaxSuggestions).ToList() }, stored);
    }

    /// <summary>Adds the correction as a dictionary entry (once) and clears it from the list.</summary>
    public static (MuesliSettings Settings, IReadOnlyList<DictionaryEntryRecord> Words) Accept(
        MuesliSettings settings, IReadOnlyList<DictionaryEntryRecord> words, DictionarySuggestion suggestion)
    {
        var key = suggestion.Key;
        var updated = words.Any(word => KeyOf(word) == key)
            ? words
            : [.. words, new DictionaryEntryRecord
            {
                Id = $"dict_{Guid.NewGuid():N}",
                Phrase = suggestion.Observed,
                Replacement = suggestion.Replacement,
                MatchingThreshold = AcceptedThreshold
            }];
        return (settings with
        {
            DictionarySuggestions = settings.DictionarySuggestions.Where(item => item.Key != key).ToList(),
            DismissedDictionarySuggestionKeys = settings.DismissedDictionarySuggestionKeys.Where(item => item != key).ToList()
        }, updated);
    }

    /// <summary>Ignore: removes the suggestion and never offers the same pair again.</summary>
    public static MuesliSettings Dismiss(MuesliSettings settings, DictionarySuggestion suggestion)
    {
        var key = suggestion.Key;
        var dismissed = settings.DismissedDictionarySuggestionKeys.Where(item => item != key).Append(key).TakeLast(MaxDismissedKeys).ToList();
        return settings with
        {
            DictionarySuggestions = settings.DictionarySuggestions.Where(item => item.Key != key).ToList(),
            DismissedDictionarySuggestionKeys = dismissed
        };
    }

    /// <summary>Whether a queued prompt is still worth showing.</summary>
    public static DictionarySuggestion? Pending(MuesliSettings settings, IEnumerable<DictionaryEntryRecord> words, string key) =>
        !settings.EnableDictionaryCorrectionPrompts || settings.DismissedDictionarySuggestionKeys.Contains(key) || words.Any(word => KeyOf(word) == key)
            ? null
            : settings.DictionarySuggestions.FirstOrDefault(item => item.Key == key);

    /// <summary>macOS keys an entry by its word and target (the replacement, or the word itself).</summary>
    private static string KeyOf(DictionaryEntryRecord word) =>
        DictionarySuggestion.KeyFor(word.Phrase, string.IsNullOrWhiteSpace(word.Replacement) ? word.Phrase : word.Replacement);
}
