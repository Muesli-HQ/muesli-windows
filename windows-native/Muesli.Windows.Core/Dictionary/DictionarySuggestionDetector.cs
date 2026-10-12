using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Muesli.Windows.Services;

/// <summary>
/// A correction Muesli noticed after a dictation was pasted (macOS <c>DictionarySuggestion</c>):
/// the transcribed word, what the user changed it to, and where.
/// </summary>
public sealed record DictionarySuggestion(string Observed, string Replacement)
{
    /// <summary>"App name|app id", as macOS stores "name|bundle id".</summary>
    public string AppContext { get; init; } = "";
    public int OccurrenceCount { get; init; } = 1;

    [JsonIgnore] public string Display => $"{Observed} → {Replacement}";
    [JsonIgnore] public string Key => KeyFor(Observed, Replacement);
    [JsonIgnore] public string AppName => AppContext.Split('|')[0].Trim();
    /// <summary>macOS row detail: "Seen 2x | Notepad".</summary>
    [JsonIgnore] public string Detail => AppName.Length == 0 ? $"Seen {OccurrenceCount}x" : $"Seen {OccurrenceCount}x | {AppName}";

    public static string KeyFor(string observed, string replacement) =>
        $"{Normalize(observed)}->{Normalize(replacement)}";

    private static string Normalize(string value) =>
        string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}

/// <summary>
/// Port of macOS <c>DictionaryCorrectionDetector</c>: finds word-scoped corrections between the text
/// Muesli pasted and what the field holds after the user edited it, rejecting ordinary rewording.
/// </summary>
public static class DictionaryCorrectionDetector
{
    private const double MinimumCorrectionSimilarity = 0.64;
    private const int MaxObservedTokensPerDictionaryWord = 2;
    private const int MaxAlignmentTokens = 512;
    private const int MaxAlignmentCellCount = 300_000;

    private sealed record Candidate(string Observed, string Replacement);
    private sealed record Token(string Text, string Normalized);
    private sealed record Run(List<Token> Observed, List<Token> Replacement);

    private enum OpKind { Match, Insertion, Deletion, Substitution }
    private readonly record struct Op(OpKind Kind, Token? Observed = null, Token? Replacement = null);

    public static IReadOnlyList<DictionarySuggestion> Suggestions(
        string originalText,
        string editedText,
        string appContext = "",
        int maxSuggestions = int.MaxValue,
        IReadOnlySet<string>? recognizedEnglishWords = null) =>
        Suggestions(originalText, originalText, editedText, appContext, maxSuggestions, recognizedEnglishWords);

    public static IReadOnlyList<DictionarySuggestion> Suggestions(
        string originalText,
        string baselineText,
        string currentText,
        string appContext,
        int maxSuggestions,
        IReadOnlySet<string>? recognizedEnglishWords)
    {
        if (maxSuggestions <= 0 || string.IsNullOrWhiteSpace(originalText) || baselineText == currentText ||
            !HasSufficientSharedContext(originalText, currentText))
        {
            return [];
        }

        var english = recognizedEnglishWords is null
            ? new HashSet<string>()
            : new HashSet<string>(recognizedEnglishWords.Select(word => word.ToLowerInvariant()));
        var results = new List<DictionarySuggestion>();
        var seen = new HashSet<string>();
        void Append(Candidate candidate)
        {
            if (results.Count >= maxSuggestions) return;
            var suggestion = new DictionarySuggestion(candidate.Observed, candidate.Replacement) { AppContext = appContext };
            if (seen.Add(suggestion.Key)) results.Add(suggestion);
        }

        if (FragmentCandidate(originalText, baselineText, currentText, english) is { } fragment) Append(fragment);
        if (results.Count < maxSuggestions)
        {
            foreach (var candidate in TokenAlignedCandidates(originalText, currentText, maxSuggestions - results.Count, english))
            {
                Append(candidate);
            }
        }
        return results;
    }

    private static Candidate? FragmentCandidate(string originalText, string baselineText, string currentText, HashSet<string> english)
    {
        var (removed, inserted) = ChangedFragments(baselineText, currentText);
        if (NormalizedCandidate(removed) is not { } observed || NormalizedCandidate(inserted) is not { } replacement) return null;
        if (!IsWordScopedSuggestion(observed, replacement)) return null;
        if (!ContainsLoosely(originalText, observed)) return null;
        return IsLikelyDictionaryCorrection(observed, replacement, english) ? new Candidate(observed, replacement) : null;
    }

    private static List<Candidate> TokenAlignedCandidates(string originalText, string editedText, int maxCandidates, HashSet<string> english)
    {
        var results = new List<Candidate>();
        if (maxCandidates <= 0) return results;
        var originalTokens = WordTokens(originalText);
        var editedTokens = WordTokens(editedText);
        if (!IsAlignmentInputWithinBounds(originalTokens.Count, editedTokens.Count)) return results;

        var seen = new HashSet<string>();
        foreach (var run in TokenChangeRuns(AlignmentOperations(originalTokens, editedTokens)))
        {
            foreach (var candidate in CandidatesFromRun(run, originalText, maxCandidates - results.Count, english))
            {
                if (results.Count < maxCandidates && seen.Add(DictionarySuggestion.KeyFor(candidate.Observed, candidate.Replacement)))
                {
                    results.Add(candidate);
                }
            }
            if (results.Count >= maxCandidates) break;
        }
        return results;
    }

    private static bool IsAlignmentInputWithinBounds(int originalCount, int editedCount) =>
        originalCount > 0 && editedCount > 0 &&
        originalCount <= MaxAlignmentTokens && editedCount <= MaxAlignmentTokens &&
        (long)(originalCount + 1) * (editedCount + 1) <= MaxAlignmentCellCount;

    private static List<Run> TokenChangeRuns(List<Op> operations)
    {
        var runs = new List<Run>();
        var observed = new List<Token>();
        var replacement = new List<Token>();
        void Flush()
        {
            if (observed.Count > 0 && replacement.Count > 0) runs.Add(new Run(observed, replacement));
            observed = [];
            replacement = [];
        }

        foreach (var op in operations)
        {
            switch (op.Kind)
            {
                case OpKind.Match: Flush(); break;
                case OpKind.Deletion: observed.Add(op.Observed!); break;
                case OpKind.Insertion: replacement.Add(op.Replacement!); break;
                default: observed.Add(op.Observed!); replacement.Add(op.Replacement!); break;
            }
        }
        Flush();
        return runs;
    }

    private static List<Candidate> CandidatesFromRun(Run run, string originalText, int maxCandidates, HashSet<string> english)
    {
        var results = new List<Candidate>();
        var observedIndex = 0;
        foreach (var replacementToken in run.Replacement)
        {
            if (results.Count >= maxCandidates || observedIndex >= run.Observed.Count) break;
            if (OverlongCorrectionLength(run.Observed, observedIndex, replacementToken, originalText) is { } overlong)
            {
                observedIndex += overlong;
            }
            else if (BestTokenCandidate(run.Observed, observedIndex, replacementToken, originalText, english) is { } best)
            {
                results.Add(best.Candidate);
                observedIndex += best.Length;
            }
        }
        return results;
    }

    private static (Candidate Candidate, int Length)? BestTokenCandidate(
        List<Token> observedTokens, int observedIndex, Token replacementToken, string originalText, HashSet<string> english)
    {
        Candidate? best = null;
        var bestLength = 0;
        var bestSimilarity = 0.0;
        var maxLength = Math.Min(MaxObservedTokensPerDictionaryWord, observedTokens.Count - observedIndex);
        for (var length = 1; length <= maxLength; length++)
        {
            var observedText = string.Join(' ', observedTokens.Skip(observedIndex).Take(length).Select(token => token.Text));
            if (NormalizedCandidate(observedText) is not { } observed ||
                NormalizedCandidate(replacementToken.Text) is not { } replacement ||
                !IsWordScopedSuggestion(observed, replacement) ||
                !ContainsLoosely(originalText, observed) ||
                !IsLikelyDictionaryCorrection(observed, replacement, english))
            {
                continue;
            }

            var similarity = Similarity(observed.Replace(" ", ""), replacement);
            if (similarity > bestSimilarity)
            {
                best = new Candidate(observed, replacement);
                bestLength = length;
                bestSimilarity = similarity;
            }
        }
        return best is null ? null : (best, bestLength);
    }

    private static int? OverlongCorrectionLength(List<Token> observedTokens, int observedIndex, Token replacementToken, string originalText)
    {
        var maxLength = Math.Min(3, observedTokens.Count - observedIndex);
        if (maxLength <= MaxObservedTokensPerDictionaryWord) return null;
        for (var length = maxLength; length > MaxObservedTokensPerDictionaryWord; length--)
        {
            var observedText = string.Join(' ', observedTokens.Skip(observedIndex).Take(length).Select(token => token.Text));
            if (NormalizedCandidate(observedText) is { } observed &&
                NormalizedCandidate(replacementToken.Text) is { } replacement &&
                ContainsLoosely(originalText, observed) &&
                IsLikelyOverlongSplitCorrection(observed, replacement))
            {
                return length;
            }
        }
        return null;
    }

    private static bool IsLikelyOverlongSplitCorrection(string observed, string replacement)
    {
        var tokens = SplitWords(observed);
        if (tokens.Length <= MaxObservedTokensPerDictionaryWord || HasSeparator(replacement) ||
            IsAcronymLike(replacement) || CommonWords.Contains(replacement.ToLowerInvariant()))
        {
            return false;
        }
        return Similarity(string.Concat(tokens), replacement) >= MinimumCorrectionSimilarity;
    }

    /// <summary>Whether an edited snapshot still shares enough wording with the pasted text to be the same passage.</summary>
    public static bool HasSufficientSharedContext(string originalText, string editedText)
    {
        var original = WordTokens(originalText).Select(token => token.Normalized).ToList();
        var edited = WordTokens(editedText).Select(token => token.Normalized).ToList();
        if (original.Count == 0 || edited.Count == 0) return false;

        if (original.Count <= 3)
        {
            var editedSet = edited.ToHashSet();
            if (original.Any(token => token.Length >= 3 && !CommonWords.Contains(token) && editedSet.Contains(token))) return true;
            return Similarity(string.Concat(original), string.Concat(edited)) >= MinimumCorrectionSimilarity;
        }

        var anchor = original.Count == 4 ? 2 : Math.Min(4, original.Count - 2);
        if (anchor <= 0 || edited.Count < anchor) return false;
        var editedWindows = Windows(edited, anchor).ToHashSet();
        return Windows(original, anchor).Any(editedWindows.Contains);
    }

    private static IEnumerable<string> Windows(List<string> tokens, int length)
    {
        for (var index = 0; index + length <= tokens.Count; index++)
        {
            yield return string.Join('\u001f', tokens.Skip(index).Take(length));
        }
    }

    /// <summary>The changed words a spell checker should classify before <see cref="Suggestions(string,string,string,int,IReadOnlySet{string}?)"/>.</summary>
    public static HashSet<string> RequiredEnglishWordCandidates(string originalText, string editedText)
    {
        var candidates = new HashSet<string>();
        void Add(IEnumerable<Token> tokens)
        {
            foreach (var token in tokens)
            {
                if (token.Normalized.Length >= 2 && token.Normalized.All(char.IsLetter)) candidates.Add(token.Normalized);
            }
        }

        var (removed, inserted) = ChangedFragments(originalText, editedText);
        Add(WordTokens(removed));
        Add(WordTokens(inserted));
        var originalTokens = WordTokens(originalText);
        var editedTokens = WordTokens(editedText);
        if (!IsAlignmentInputWithinBounds(originalTokens.Count, editedTokens.Count)) return candidates;
        foreach (var run in TokenChangeRuns(AlignmentOperations(originalTokens, editedTokens)))
        {
            Add(run.Observed.Concat(run.Replacement));
        }
        return candidates;
    }

    private static List<Token> WordTokens(string text)
    {
        // Dictionary-significant separators stay inside tokens (sc-domain, API_v2, foo/bar, C++).
        var tokens = new List<Token>();
        var current = new StringBuilder();
        void Flush()
        {
            if (current.Length > 0)
            {
                var value = current.ToString();
                tokens.Add(new Token(value, value.ToLowerInvariant()));
            }
            current.Clear();
        }

        foreach (var character in text ?? "")
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_' or '/' or '+') current.Append(character);
            else Flush();
        }
        Flush();
        return tokens;
    }

    private static List<Op> AlignmentOperations(List<Token> old, List<Token> @new)
    {
        var rows = old.Count + 1;
        var columns = @new.Count + 1;
        var costs = new int[rows, columns];
        for (var row = 0; row < rows; row++) costs[row, 0] = row;
        for (var column = 0; column < columns; column++) costs[0, column] = column;
        for (var row = 1; row < rows; row++)
        {
            for (var column = 1; column < columns; column++)
            {
                var substitution = old[row - 1].Text == @new[column - 1].Text ? 0 : 1;
                costs[row, column] = Math.Min(
                    Math.Min(costs[row - 1, column] + 1, costs[row, column - 1] + 1),
                    costs[row - 1, column - 1] + substitution);
            }
        }

        var operations = new List<Op>();
        int r = old.Count, c = @new.Count;
        while (r > 0 || c > 0)
        {
            if (r > 0 && c > 0)
            {
                var substitution = old[r - 1].Text == @new[c - 1].Text ? 0 : 1;
                if (costs[r, c] == costs[r - 1, c - 1] + substitution)
                {
                    operations.Add(substitution == 0
                        ? new Op(OpKind.Match)
                        : new Op(OpKind.Substitution, old[r - 1], @new[c - 1]));
                    r--;
                    c--;
                    continue;
                }
            }
            if (r > 0 && costs[r, c] == costs[r - 1, c] + 1)
            {
                operations.Add(new Op(OpKind.Deletion, Observed: old[r - 1]));
                r--;
                continue;
            }
            if (c > 0)
            {
                operations.Add(new Op(OpKind.Insertion, Replacement: @new[c - 1]));
                c--;
            }
        }
        operations.Reverse();
        return operations;
    }

    private static (string Removed, string Inserted) ChangedFragments(string oldText, string newText)
    {
        var old = oldText ?? "";
        var @new = newText ?? "";
        var prefix = 0;
        while (prefix < old.Length && prefix < @new.Length && old[prefix] == @new[prefix]) prefix++;
        int oldSuffix = old.Length, newSuffix = @new.Length;
        while (oldSuffix > prefix && newSuffix > prefix && old[oldSuffix - 1] == @new[newSuffix - 1])
        {
            oldSuffix--;
            newSuffix--;
        }
        while (prefix > 0 && !IsBoundary(old[prefix - 1]) && !IsBoundary(@new[prefix - 1])) prefix--;
        while (oldSuffix < old.Length && !IsBoundary(old[oldSuffix])) oldSuffix++;
        while (newSuffix < @new.Length && !IsBoundary(@new[newSuffix])) newSuffix++;
        return (old[prefix..Math.Max(prefix, oldSuffix)], @new[prefix..Math.Max(prefix, newSuffix)]);
    }

    private static bool IsBoundary(char character) =>
        char.IsWhiteSpace(character) || (char.IsPunctuation(character) && character is not ('-' or '_' or '/' or '+'));

    private static string? NormalizedCandidate(string value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0 || trimmed.Contains('\n')) return null;
        var cleaned = trimmed.Trim().Trim(BoundaryCharacters(trimmed));
        if (cleaned.Length == 0 || cleaned.Length > 60) return null;
        var tokens = SplitWords(cleaned);
        if (tokens.Length is < 1 or > 3 || !tokens.All(token => token.Any(char.IsLetterOrDigit))) return null;
        return string.Join(' ', tokens);
    }

    /// <summary>Whitespace and punctuation at the edges, except the separators dictionary words keep.</summary>
    private static char[] BoundaryCharacters(string value) =>
        value.Where(character => (char.IsWhiteSpace(character) || char.IsPunctuation(character)) &&
                                 character is not ('_' or '-' or '+' or '/'))
            .Distinct()
            .ToArray();

    private static bool IsWordScopedSuggestion(string observed, string replacement)
    {
        var observedTokens = SplitWords(observed);
        if (SplitWords(replacement).Length != 1 || observedTokens.Length is < 1 or > MaxObservedTokensPerDictionaryWord) return false;
        if (observedTokens.Length == 1)
        {
            if (IsAcronymLike(replacement)) return string.Equals(observed, replacement, StringComparison.OrdinalIgnoreCase);
            if (replacement.Contains('-') && !observed.Contains('-')) return Similarity(observed, replacement) >= 0.82;
            return true;
        }
        if (HasSeparator(replacement) || IsAcronymLike(replacement)) return false;
        return Similarity(string.Concat(observedTokens), replacement) >= MinimumCorrectionSimilarity;
    }

    private static bool IsLikelyDictionaryCorrection(string observed, string replacement, HashSet<string> english)
    {
        if (observed == replacement || observed.Length < 2 || replacement.Length < 2) return false;
        var observedTokens = SplitWords(observed);
        var replacementTokens = SplitWords(replacement);
        var normalizedObserved = observed.ToLowerInvariant();
        var normalizedReplacement = replacement.ToLowerInvariant();
        var formattingSignal = HasStrongDictionarySignal(replacement) || replacement.Contains('-');
        var similarity = Similarity(normalizedObserved, normalizedReplacement);

        if (observedTokens.Length != replacementTokens.Length)
        {
            if (observedTokens.Any(token => CommonWords.Contains(token.ToLowerInvariant()))) return false;
            var compact = Similarity(string.Concat(observedTokens), string.Concat(replacementTokens));
            return compact >= 0.82 ||
                   IsLikelySingleWordSplitCorrection(replacement, replacementTokens, compact, english) ||
                   IsLikelyStrongDictionaryCorrection(observed, replacement, observedTokens, compact);
        }

        var sameLetters = normalizedObserved == normalizedReplacement;
        if (CommonWords.Contains(normalizedObserved))
        {
            return sameLetters
                ? formattingSignal || replacement.Any(char.IsUpper)
                : similarity >= 0.82 && formattingSignal;
        }
        if (CommonWords.Contains(normalizedReplacement) && !formattingSignal) return false;
        if (sameLetters) return formattingSignal || replacement.Any(char.IsUpper);
        if (IsCommonWordTruncation(normalizedObserved, normalizedReplacement, english)) return false;
        return similarity >= MinimumCorrectionSimilarity ||
               IsLikelyStrongDictionaryCorrection(observed, replacement, observedTokens, similarity);
    }

    private static bool HasStrongDictionarySignal(string value) =>
        HasInternalCapital(value) || value.Any(char.IsDigit) || value.Contains('_') || value.Contains('/') || IsAcronymLike(value);

    private static bool IsLikelyStrongDictionaryCorrection(string observed, string replacement, string[] observedTokens, double similarity)
    {
        if (!HasStrongDictionarySignal(replacement)) return false;
        if (IsNumericShorthand(observed, replacement)) return true;
        if (IsAcronymLike(replacement) && IsAcronymCorrection(observedTokens, replacement)) return true;
        return (replacement.Contains('_') || replacement.Contains('/') || HasInternalCapital(replacement)) && similarity >= 0.55;
    }

    private static bool IsLikelySingleWordSplitCorrection(string replacement, string[] replacementTokens, double similarity, HashSet<string> english) =>
        replacementTokens.Length == 1 && !HasSeparator(replacement) && !IsAcronymLike(replacement) &&
        !CommonWords.Contains(replacement.ToLowerInvariant()) && similarity >= MinimumCorrectionSimilarity &&
        (replacement.Any(char.IsUpper) || !english.Contains(replacement.ToLowerInvariant()));

    /// <summary>"internationalization" → "i18n".</summary>
    private static bool IsNumericShorthand(string observed, string replacement)
    {
        var letters = observed.ToLowerInvariant().Where(char.IsLetter).ToArray();
        var characters = replacement.ToLowerInvariant();
        if (letters.Length < 4 || characters.Length < 3 || characters.Length >= letters.Length / 2 ||
            characters[0] != letters[0] || characters[^1] != letters[^1])
        {
            return false;
        }
        var middle = characters[1..^1];
        return middle.Length > 0 && middle.All(char.IsDigit) &&
               int.TryParse(middle, NumberStyles.None, CultureInfo.InvariantCulture, out var omitted) &&
               omitted == letters.Length - 2;
    }

    private static bool IsAcronymCorrection(string[] observedTokens, string replacement)
    {
        if (observedTokens.Length <= 1) return false;
        var initials = string.Concat(observedTokens.Select(token => char.ToLowerInvariant(token[0])));
        var letters = string.Concat(replacement.ToLowerInvariant().Where(char.IsLetter));
        return initials.Length > 0 && letters == initials;
    }

    private static bool IsCommonWordTruncation(string observed, string replacement, HashSet<string> english) =>
        SplitWords(observed).Length == 1 && SplitWords(replacement).Length == 1 &&
        observed.All(char.IsLower) && replacement.All(char.IsLower) && observed != replacement &&
        (observed.StartsWith(replacement, StringComparison.Ordinal) || replacement.StartsWith(observed, StringComparison.Ordinal)) &&
        english.Contains(observed) && english.Contains(replacement);

    private static bool HasInternalCapital(string value) => value.Length > 1 && value.Skip(1).Any(char.IsUpper);

    private static bool IsAcronymLike(string value)
    {
        var letters = value.Where(char.IsLetter).ToArray();
        return letters.Length >= 2 && letters.All(char.IsUpper);
    }

    private static bool HasSeparator(string value) => value.Contains('-') || value.Contains('_') || value.Contains('/');

    private static string[] SplitWords(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>macOS matches case- and diacritic-insensitively.</summary>
    private static bool ContainsLoosely(string text, string value) =>
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, value, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    private static double Similarity(string left, string right) =>
        DictionaryCorrectionService.JaroWinkler(left.ToLowerInvariant(), right.ToLowerInvariant());

    private static readonly HashSet<string> CommonWords =
    [
        "a", "about", "after", "all", "also", "am", "an", "and", "are", "as", "at",
        "be", "because", "but", "by", "can", "could", "did", "do", "does", "for",
        "from", "get", "go", "had", "has", "have", "he", "her", "here", "him", "his",
        "how", "i", "if", "in", "is", "it", "its", "just", "like", "me", "my", "not",
        "now", "of", "on", "or", "our", "out", "she", "so", "that", "the", "their",
        "them", "then", "there", "they", "this", "to", "up", "us", "was", "we", "were",
        "what", "when", "where", "which", "who", "will", "with", "would", "you", "your",
    ];
}

/// <summary>
/// macOS <c>DictationCorrectionSnapshotStabilizer</c>: a field's text counts only once it has stayed
/// the same for the quiet window, and each exact text is evaluated once.
/// </summary>
public sealed class DictationCorrectionSnapshotStabilizer
{
    private Dictionary<string, DateTime> _changedAt = new(StringComparer.Ordinal);
    private readonly HashSet<string> _evaluated = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Observe(IReadOnlyList<string> snapshots, DateTime now, TimeSpan quietWindow)
    {
        var current = snapshots.ToHashSet(StringComparer.Ordinal);
        _changedAt = _changedAt.Where(pair => current.Contains(pair.Key)).ToDictionary(StringComparer.Ordinal);
        foreach (var snapshot in snapshots) _changedAt.TryAdd(snapshot, now);
        var stable = new List<string>();
        foreach (var snapshot in snapshots)
        {
            if (!_evaluated.Contains(snapshot) && now - _changedAt[snapshot] >= quietWindow)
            {
                _evaluated.Add(snapshot);
                stable.Add(snapshot);
            }
        }
        return stable;
    }

    /// <summary>
    /// macOS <c>normalizedSnapshot</c>: collapses whitespace and drops text that is unchanged, too large,
    /// or too different in length to be the pasted passage.
    /// </summary>
    public static string? Normalize(string? value, string originalText, int maxCharacters = 2_000)
    {
        if (value is null) return null;
        var normalized = string.Join(' ', value.Replace(' ', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0 || normalized.Length > maxCharacters || normalized == originalText) return null;
        var lengthDelta = Math.Abs(normalized.Length - originalText.Length);
        var allowed = Math.Max(240, originalText.Length * 2);
        return normalized.Length >= Math.Max(2, originalText.Length / 2) && lengthDelta <= allowed ? normalized : null;
    }
}
