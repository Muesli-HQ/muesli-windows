namespace Muesli.Windows.Services;

/// <summary>
/// What happens to a detected correction, ported from macOS <c>MuesliController</c>
/// (<c>addDictionarySuggestion</c>, <c>acceptDictionarySuggestion</c>, <c>dismissDictionarySuggestion</c>).
/// Pure functions over the settings record and the dictionary, so the rules are testable without
/// a window. Showing a prompt or letting it time out is not an answer: only Add or Ignore stops a
/// correction from being offered again.
/// </summary>
public static class DictionarySuggestionLedger
{
    public const int MaxSuggestions = 50;
    public const int MaxDismissedKeys = 200;

    /// <summary>
    /// Auto-learned corrections come from one observed edit, so they match more strictly than a
    /// word the user typed into the dictionary (macOS <c>DictionarySuggestion.customWord</c>).
    /// </summary>
    public const double AcceptedMatchingThreshold = 0.92;

    /// <summary>macOS <c>DictionarySuggestion.key</c>: trimmed, whitespace-collapsed, lower-cased.</summary>
    public static string Key(string observed, string replacement) => $"{Normalize(observed)}->{Normalize(replacement)}";

    /// <summary>
    /// Records a detected correction. Returns the settings to save (null when nothing changes) and
    /// the suggestion to prompt for (null when it must not be offered).
    /// </summary>
    public static (MuesliSettings? Settings, DictionarySuggestion? Prompt) Add(
        MuesliSettings settings,
        DictionarySuggestion suggestion,
        IEnumerable<DictionaryEntryRecord> dictionary)
    {
        if (!settings.EnableDictionaryCorrectionPrompts) return (null, null);
        var observed = (suggestion.Observed ?? "").Trim();
        var replacement = (suggestion.Replacement ?? "").Trim();
        if (observed.Length == 0 || replacement.Length == 0 || observed == replacement) return (null, null);

        var key = Key(observed, replacement);
        if ((settings.DismissedDictionarySuggestionKeys ?? []).Contains(key)) return (null, null);
        if (dictionary.Any(entry => Key(entry.Phrase, entry.Replacement) == key)) return (null, null);

        var existing = (settings.DictionarySuggestions ?? []).FirstOrDefault(item => Key(item.Observed, item.Replacement) == key);
        var prompt = existing ?? new DictionarySuggestion(observed, replacement);
        var list = new List<DictionarySuggestion> { prompt };
        list.AddRange((settings.DictionarySuggestions ?? []).Where(item => Key(item.Observed, item.Replacement) != key));
        return (settings with { DictionarySuggestions = list.Take(MaxSuggestions).ToArray() }, prompt);
    }

    /// <summary>Add: the correction becomes a dictionary entry and leaves the suggestion list.</summary>
    public static (MuesliSettings Settings, IReadOnlyList<DictionaryEntryRecord>? Dictionary) Accept(
        MuesliSettings settings,
        DictionarySuggestion suggestion,
        IReadOnlyList<DictionaryEntryRecord> dictionary)
    {
        var key = Key(suggestion.Observed, suggestion.Replacement);
        IReadOnlyList<DictionaryEntryRecord>? updated = null;
        if (!dictionary.Any(entry => Key(entry.Phrase, entry.Replacement) == key))
        {
            updated = dictionary.Append(new DictionaryEntryRecord
            {
                Id = $"dict_{Guid.NewGuid():N}",
                Phrase = suggestion.Observed.Trim(),
                Replacement = suggestion.Replacement.Trim(),
                MatchingThreshold = AcceptedMatchingThreshold
            }).ToArray();
        }

        return (settings with
        {
            DictionarySuggestions = (settings.DictionarySuggestions ?? []).Where(item => Key(item.Observed, item.Replacement) != key).ToArray(),
            DismissedDictionarySuggestionKeys = (settings.DismissedDictionarySuggestionKeys ?? []).Where(item => item != key).ToArray()
        }, updated);
    }

    /// <summary>Ignore: the correction leaves the list and is never offered again.</summary>
    public static MuesliSettings Ignore(MuesliSettings settings, DictionarySuggestion suggestion)
    {
        var key = Key(suggestion.Observed, suggestion.Replacement);
        var dismissed = (settings.DismissedDictionarySuggestionKeys ?? []).Where(item => item != key).Append(key);
        return settings with
        {
            DictionarySuggestions = (settings.DictionarySuggestions ?? []).Where(item => Key(item.Observed, item.Replacement) != key).ToArray(),
            DismissedDictionarySuggestionKeys = dismissed.TakeLast(MaxDismissedKeys).ToArray()
        };
    }

    private static string Normalize(string? value) =>
        string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}

/// <summary>
/// Timing and snapshot rules of the macOS <c>DictationCorrectionMonitor</c>: poll quickly for the
/// first ten seconds after a paste, then once a second until 45 seconds; judge a text box's content
/// only after it has stayed the same for 1.5 seconds; at most three suggestions per dictation.
/// </summary>
public static class DictationCorrectionMonitorPolicy
{
    public static readonly TimeSpan InitialPollDelay = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan FastPollInterval = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan SteadyPollInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan FastPollingWindow = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MonitoringWindow = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan SnapshotQuietWindow = TimeSpan.FromSeconds(1.5);
    public const int MaxSuggestionsPerSession = 3;
    public const int MaxCandidateCharacters = 2_000;
    public const int MaxEnglishWordCandidates = 128;

    /// <summary>
    /// The text box's content in a comparable form, or null when it cannot be an edit of this
    /// dictation: unchanged, empty, over 2,000 characters, or a very different length.
    /// </summary>
    public static string? NormalizedSnapshot(string? value, string originalText)
    {
        if (value is null) return null;
        var normalized = CollapsedWhitespace(value);
        if (normalized.Length == 0 || normalized.Length > MaxCandidateCharacters) return null;
        // A multi-line paste read back unchanged must not look edited because its line breaks collapsed.
        if (normalized == CollapsedWhitespace(originalText)) return null;

        var originalLength = originalText.Length;
        var allowedDelta = Math.Max(240, originalLength * 2);
        if (normalized.Length < Math.Max(2, originalLength / 2) || Math.Abs(normalized.Length - originalLength) > allowedDelta) return null;
        return normalized;
    }

    /// <summary>
    /// The words to ask the spell checker about: every word the decision needs, then the shortest
    /// other words of both texts, up to 128 in all.
    /// </summary>
    public static IReadOnlySet<string> EnglishWordCandidates(string originalText, string editedText)
    {
        var required = DictionaryCorrectionDetector.RequiredEnglishWordCandidates(originalText, editedText);
        var extra = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in new[] { originalText, editedText })
        {
            foreach (var token in SplitNonLetters(text.ToLowerInvariant()))
            {
                if (token.Length >= 2 && !required.Contains(token)) extra.Add(token);
            }
        }

        var budget = Math.Max(0, MaxEnglishWordCandidates - required.Count);
        var result = new HashSet<string>(required, StringComparer.Ordinal);
        foreach (var word in extra.OrderBy(word => word.Length).ThenBy(word => word, StringComparer.Ordinal).Take(budget)) result.Add(word);
        return result;
    }

    private static IEnumerable<string> SplitNonLetters(string text)
    {
        var start = -1;
        for (var index = 0; index <= text.Length; index++)
        {
            var isLetter = index < text.Length && char.IsLetter(text[index]);
            if (isLetter && start < 0) start = index;
            else if (!isLetter && start >= 0)
            {
                yield return text[start..index];
                start = -1;
            }
        }
    }

    private static string CollapsedWhitespace(string value) =>
        string.Join(' ', value.Replace(' ', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>
/// macOS <c>DictationCorrectionSnapshotStabilizer</c>: a snapshot is judged once, after it has been
/// seen unchanged for the quiet window, so a word being typed is not judged half-written.
/// </summary>
public sealed class DictationCorrectionSnapshotStabilizer
{
    private readonly Dictionary<string, DateTime> _firstSeen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _evaluated = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Observe(IReadOnlyList<string> snapshots, DateTime now, TimeSpan quietWindow)
    {
        var current = snapshots.ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _firstSeen.Keys.Where(key => !current.Contains(key)).ToList()) _firstSeen.Remove(stale);
        foreach (var snapshot in snapshots) _firstSeen.TryAdd(snapshot, now);

        var stable = new List<string>();
        foreach (var snapshot in snapshots)
        {
            if (_evaluated.Contains(snapshot) || !_firstSeen.TryGetValue(snapshot, out var seen) || now - seen < quietWindow) continue;
            _evaluated.Add(snapshot);
            stable.Add(snapshot);
        }
        return stable;
    }
}
