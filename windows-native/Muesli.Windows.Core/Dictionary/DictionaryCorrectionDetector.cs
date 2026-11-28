using System.Globalization;

namespace Muesli.Windows.Services;

/// <summary>
/// Decides whether an edit the user made to a pasted dictation is a correction worth offering as
/// a dictionary entry. A line-by-line port of macOS <c>DictionaryCorrectionDetector</c>
/// (<c>DictationCorrectionMonitor.swift</c>): the same thresholds, word boundaries and filters, so a
/// correction that prompts on a Mac prompts here. Swift <c>Character</c> tests map to the matching
/// <see cref="char"/> tests; Jaro-Winkler is <see cref="DictionaryCorrectionService.JaroWinkler"/>,
/// which equals macOS <c>CustomWordMatcher.jaroWinklerSimilarity</c> on the lower-cased input every
/// call here passes.
/// </summary>
public static class DictionaryCorrectionDetector
{
    private const double MinimumCorrectionSimilarity = 0.64;
    private const int MaxObservedTokensPerDictionaryWord = 2;
    private const int MaxAlignmentTokens = 512;
    private const int MaxAlignmentCellCount = 300_000;

    private static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal)
    {
        "a", "about", "after", "all", "also", "am", "an", "and", "are", "as", "at",
        "be", "because", "but", "by", "can", "could", "did", "do", "does", "for",
        "from", "get", "go", "had", "has", "have", "he", "her", "here", "him", "his",
        "how", "i", "if", "in", "is", "it", "its", "just", "like", "me", "my", "not",
        "now", "of", "on", "or", "our", "out", "she", "so", "that", "the", "their",
        "them", "then", "there", "they", "this", "to", "up", "us", "was", "we", "were",
        "what", "when", "where", "which", "who", "will", "with", "would", "you", "your",
    };

    private sealed record Candidate(string Observed, string Replacement);

    private sealed record WordToken(string Text, string Normalized);

    private sealed record TokenChangeRun(IReadOnlyList<WordToken> Observed, IReadOnlyList<WordToken> Replacement);

    private enum OperationKind { Match, Insertion, Deletion, Substitution }

    private readonly record struct AlignmentOperation(OperationKind Kind, WordToken? Observed, WordToken? Replacement);

    /// <summary>
    /// Suggestions for an edit from <paramref name="originalText"/> (what was pasted) to
    /// <paramref name="editedText"/> (what the text box holds now). <paramref name="recognizedEnglishWords"/>
    /// are the lower-cased words the spell checker knows; an empty set treats every word as unknown.
    /// </summary>
    public static IReadOnlyList<DictionarySuggestion> Suggestions(
        string originalText,
        string editedText,
        int maxSuggestions = int.MaxValue,
        IReadOnlySet<string>? recognizedEnglishWords = null) =>
        Suggestions(originalText, originalText, editedText, maxSuggestions, recognizedEnglishWords);

    public static IReadOnlyList<DictionarySuggestion> Suggestions(
        string originalText,
        string baselineText,
        string currentText,
        int maxSuggestions,
        IReadOnlySet<string>? recognizedEnglishWords)
    {
        if (!IsDetectionRequestValid(originalText, baselineText, currentText, maxSuggestions)) return [];

        var recognized = recognizedEnglishWords ?? new HashSet<string>();
        var results = new List<DictionarySuggestion>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in ExtractCandidateCorrections(originalText, baselineText, currentText, maxSuggestions, recognized))
        {
            if (results.Count >= maxSuggestions ||
                !IsSubstitution(candidate.Observed, candidate.Replacement, baselineText, currentText)) continue;
            var suggestion = new DictionarySuggestion(candidate.Observed, candidate.Replacement);
            if (seenKeys.Add(DictionarySuggestionLedger.Key(suggestion.Observed, suggestion.Replacement)))
            {
                results.Add(suggestion);
            }
        }

        return results;
    }

    /// <summary>
    /// A correction replaces text: the observed form loses an occurrence and the replacement gains
    /// one. Text an app merely re-renders or reorders changes neither count.
    /// </summary>
    public static bool IsSubstitution(string observed, string replacement, string before, string after)
    {
        var observedTokens = SubstitutionTokens(observed);
        var replacementTokens = SubstitutionTokens(replacement);
        if (observedTokens.Count == 0 || replacementTokens.Count == 0) return false;
        var beforeTokens = SubstitutionTokens(before);
        var afterTokens = SubstitutionTokens(after);
        return Occurrences(observedTokens, afterTokens) < Occurrences(observedTokens, beforeTokens) &&
               Occurrences(replacementTokens, afterTokens) > Occurrences(replacementTokens, beforeTokens);
    }

    /// <summary>Case-sensitive so acronym fixes ("api" to "API") still count as substitutions.</summary>
    private static List<string> SubstitutionTokens(string text) => WordTokens(text).Select(token => token.Text).ToList();

    private static int Occurrences(IReadOnlyList<string> phrase, IReadOnlyList<string> tokens)
    {
        if (phrase.Count == 0 || tokens.Count < phrase.Count) return 0;
        var count = 0;
        for (var start = 0; start <= tokens.Count - phrase.Count; start++)
        {
            var matches = true;
            for (var offset = 0; offset < phrase.Count && matches; offset++)
            {
                matches = string.Equals(tokens[start + offset], phrase[offset], StringComparison.Ordinal);
            }
            if (matches) count++;
        }
        return count;
    }

    private static bool IsDetectionRequestValid(string originalText, string baselineText, string currentText, int maxSuggestions)
    {
        if (maxSuggestions <= 0) return false;
        if (string.IsNullOrWhiteSpace(originalText)) return false;
        if (string.Equals(baselineText, currentText, StringComparison.Ordinal)) return false;
        // Windows passes the box as pasted for baselineText; an edit that removed a short dictation's
        // only distinctive word still shares the rest of that box. With baselineText equal to the
        // dictation this is exactly the macOS rule.
        return HasSufficientSharedContext(originalText, currentText) ||
               (!string.Equals(baselineText, originalText, StringComparison.Ordinal) && HasSufficientSharedContext(baselineText, currentText));
    }

    private static List<Candidate> ExtractCandidateCorrections(
        string originalText,
        string baselineText,
        string currentText,
        int maxCandidates,
        IReadOnlySet<string> recognized)
    {
        var candidates = new List<Candidate>();
        if (FragmentCandidate(originalText, baselineText, currentText, recognized) is { } fragment && candidates.Count < maxCandidates)
        {
            candidates.Add(fragment);
        }

        if (candidates.Count < maxCandidates)
        {
            foreach (var candidate in TokenAlignedCandidates(originalText, currentText, maxCandidates - candidates.Count, recognized))
            {
                if (candidates.Count >= maxCandidates) break;
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    private static Candidate? FragmentCandidate(string originalText, string baselineText, string currentText, IReadOnlySet<string> recognized)
    {
        var (removed, inserted) = ChangedFragments(baselineText, currentText);
        if (NormalizedCandidate(removed) is not { } observed || NormalizedCandidate(inserted) is not { } replacement) return null;
        if (!IsWordScopedSuggestion(observed, replacement)) return null;
        if (!ContainsIgnoringCaseAndDiacritics(originalText, observed)) return null;
        if (!IsLikelyDictionaryCorrection(observed, replacement, recognized)) return null;
        return new Candidate(observed, replacement);
    }

    private static List<Candidate> TokenAlignedCandidates(string originalText, string editedText, int maxCandidates, IReadOnlySet<string> recognized)
    {
        if (maxCandidates <= 0) return [];
        var originalTokens = WordTokens(originalText);
        var editedTokens = WordTokens(editedText);
        if (originalTokens.Count == 0 || editedTokens.Count == 0) return [];
        if (!IsAlignmentInputWithinBounds(originalTokens.Count, editedTokens.Count)) return [];

        var candidates = new List<Candidate>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var run in TokenChangeRuns(AlignmentOperations(originalTokens, editedTokens)))
        {
            foreach (var candidate in CandidatesFromTokenRun(run, originalText, maxCandidates - candidates.Count, recognized))
            {
                if (candidates.Count >= maxCandidates) break;
                if (seenKeys.Add(DictionarySuggestionLedger.Key(candidate.Observed, candidate.Replacement))) candidates.Add(candidate);
            }
            if (candidates.Count >= maxCandidates) break;
        }
        return candidates;
    }

    private static bool IsAlignmentInputWithinBounds(int originalCount, int editedCount)
    {
        if (originalCount <= 0 || editedCount <= 0) return false;
        if (originalCount > MaxAlignmentTokens || editedCount > MaxAlignmentTokens) return false;
        return (long)(originalCount + 1) * (editedCount + 1) <= MaxAlignmentCellCount;
    }

    private static List<TokenChangeRun> TokenChangeRuns(IReadOnlyList<AlignmentOperation> operations)
    {
        var runs = new List<TokenChangeRun>();
        var observed = new List<WordToken>();
        var replacement = new List<WordToken>();

        void Flush()
        {
            if (observed.Count > 0 && replacement.Count > 0)
            {
                runs.Add(new TokenChangeRun(observed.ToList(), replacement.ToList()));
            }
            observed.Clear();
            replacement.Clear();
        }

        foreach (var operation in operations)
        {
            switch (operation.Kind)
            {
                case OperationKind.Match:
                    Flush();
                    break;
                case OperationKind.Deletion:
                    observed.Add(operation.Observed!);
                    break;
                case OperationKind.Insertion:
                    replacement.Add(operation.Replacement!);
                    break;
                case OperationKind.Substitution:
                    observed.Add(operation.Observed!);
                    replacement.Add(operation.Replacement!);
                    break;
            }
        }
        Flush();
        return runs;
    }

    private static List<Candidate> CandidatesFromTokenRun(TokenChangeRun run, string originalText, int maxCandidates, IReadOnlySet<string> recognized)
    {
        if (maxCandidates <= 0) return [];
        var results = new List<Candidate>();
        var observedIndex = 0;
        foreach (var replacementToken in run.Replacement)
        {
            if (results.Count >= maxCandidates || observedIndex >= run.Observed.Count) break;
            if (OverlongCorrectionLength(run.Observed, observedIndex, replacementToken, originalText) is { } overlong)
            {
                observedIndex += overlong;
            }
            else if (BestTokenCandidate(run.Observed, observedIndex, replacementToken, originalText, recognized) is { } best)
            {
                results.Add(best.Candidate);
                observedIndex += best.ObservedLength;
            }
        }
        return results;
    }

    private static (Candidate Candidate, int ObservedLength)? BestTokenCandidate(
        IReadOnlyList<WordToken> observedTokens,
        int observedIndex,
        WordToken replacementToken,
        string originalText,
        IReadOnlySet<string> recognized)
    {
        Candidate? best = null;
        var bestLength = 0;
        var bestSimilarity = 0.0;
        var maxObservedLength = Math.Min(MaxObservedTokensPerDictionaryWord, observedTokens.Count - observedIndex);
        for (var length = 1; length <= maxObservedLength; length++)
        {
            var observedText = string.Join(' ', observedTokens.Skip(observedIndex).Take(length).Select(token => token.Text));
            if (NormalizedCandidate(observedText) is not { } observed ||
                NormalizedCandidate(replacementToken.Text) is not { } replacement ||
                !IsWordScopedSuggestion(observed, replacement) ||
                !ContainsIgnoringCaseAndDiacritics(originalText, observed) ||
                !IsLikelyDictionaryCorrection(observed, replacement, recognized)) continue;

            var similarity = Similarity(observed.Replace(" ", "").ToLowerInvariant(), replacement.ToLowerInvariant());
            if (similarity > bestSimilarity)
            {
                best = new Candidate(observed, replacement);
                bestLength = length;
                bestSimilarity = similarity;
            }
        }
        return best is null ? null : (best, bestLength);
    }

    private static int? OverlongCorrectionLength(IReadOnlyList<WordToken> observedTokens, int observedIndex, WordToken replacementToken, string originalText)
    {
        var maxOverlong = Math.Min(3, observedTokens.Count - observedIndex);
        if (maxOverlong <= MaxObservedTokensPerDictionaryWord) return null;
        for (var length = maxOverlong; length >= MaxObservedTokensPerDictionaryWord + 1; length--)
        {
            var observedText = string.Join(' ', observedTokens.Skip(observedIndex).Take(length).Select(token => token.Text));
            if (NormalizedCandidate(observedText) is not { } observed ||
                NormalizedCandidate(replacementToken.Text) is not { } replacement ||
                !ContainsIgnoringCaseAndDiacritics(originalText, observed) ||
                !IsLikelyOverlongSplitCorrection(observed, replacement)) continue;
            return length;
        }
        return null;
    }

    private static bool IsLikelyOverlongSplitCorrection(string observed, string replacement)
    {
        var observedTokens = SplitWhitespace(observed);
        if (observedTokens.Length <= MaxObservedTokensPerDictionaryWord ||
            replacement.Contains('-') || replacement.Contains('_') || replacement.Contains('/') ||
            IsAcronymLike(replacement) || CommonWords.Contains(replacement.ToLowerInvariant())) return false;
        return Similarity(string.Concat(observedTokens).ToLowerInvariant(), replacement.ToLowerInvariant()) >= MinimumCorrectionSimilarity;
    }

    /// <summary>
    /// The text box still holds enough of the dictation for its content to be compared: a short
    /// dictation shares a distinctive word or most of its letters; a longer one shares a run of
    /// two to four words in order.
    /// </summary>
    public static bool HasSufficientSharedContext(string originalText, string editedText)
    {
        var originalTokens = WordTokens(originalText).Select(token => token.Normalized).ToList();
        var editedTokens = WordTokens(editedText).Select(token => token.Normalized).ToList();
        if (originalTokens.Count == 0 || editedTokens.Count == 0) return false;

        if (originalTokens.Count <= 3)
        {
            var editedSet = editedTokens.ToHashSet(StringComparer.Ordinal);
            if (originalTokens.Any(token => token.Length >= 3 && !CommonWords.Contains(token) && editedSet.Contains(token))) return true;
            return Similarity(string.Concat(originalTokens), string.Concat(editedTokens)) >= MinimumCorrectionSimilarity;
        }

        var anchorLength = originalTokens.Count == 4 ? 2 : Math.Min(4, originalTokens.Count - 2);
        if (anchorLength <= 0 || editedTokens.Count < anchorLength) return false;
        var editedWindows = Windows(editedTokens, anchorLength).ToHashSet(StringComparer.Ordinal);
        return Windows(originalTokens, anchorLength).Any(editedWindows.Contains);
    }

    private static IEnumerable<string> Windows(IReadOnlyList<string> tokens, int length)
    {
        if (length <= 0 || tokens.Count < length) yield break;
        for (var index = 0; index <= tokens.Count - length; index++)
        {
            yield return string.Join('\u001f', tokens.Skip(index).Take(length));
        }
    }

    /// <summary>
    /// Words keep dictionary-significant separators (sc-domain, API_v2, foo/bar, C++); everything
    /// else splits.
    /// </summary>
    private static List<WordToken> WordTokens(string text)
    {
        var tokens = new List<WordToken>();
        var current = new System.Text.StringBuilder();
        void Flush()
        {
            var candidate = current.ToString().Trim();
            if (candidate.Length > 0) tokens.Add(new WordToken(candidate, candidate.ToLowerInvariant()));
            current.Clear();
        }

        foreach (var character in text ?? "")
        {
            if (char.IsLetter(character) || char.IsNumber(character) || char.IsSurrogate(character) || char.GetUnicodeCategory(character) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark ||
                character is '-' or '_' or '/' or '+')
            {
                current.Append(character);
            }
            else
            {
                Flush();
            }
        }
        Flush();
        return tokens;
    }

    private static List<AlignmentOperation> AlignmentOperations(IReadOnlyList<WordToken> oldTokens, IReadOnlyList<WordToken> newTokens)
    {
        var rows = oldTokens.Count + 1;
        var columns = newTokens.Count + 1;
        var costs = new int[rows, columns];
        for (var row = 0; row < rows; row++) costs[row, 0] = row;
        for (var column = 0; column < columns; column++) costs[0, column] = column;
        for (var row = 1; row < rows; row++)
        {
            for (var column = 1; column < columns; column++)
            {
                var substitution = oldTokens[row - 1].Text == newTokens[column - 1].Text ? 0 : 1;
                costs[row, column] = Math.Min(
                    Math.Min(costs[row - 1, column] + 1, costs[row, column - 1] + 1),
                    costs[row - 1, column - 1] + substitution);
            }
        }

        var operations = new List<AlignmentOperation>();
        var r = oldTokens.Count;
        var c = newTokens.Count;
        while (r > 0 || c > 0)
        {
            if (r > 0 && c > 0)
            {
                var substitution = oldTokens[r - 1].Text == newTokens[c - 1].Text ? 0 : 1;
                if (costs[r, c] == costs[r - 1, c - 1] + substitution)
                {
                    operations.Add(substitution == 0
                        ? new AlignmentOperation(OperationKind.Match, null, null)
                        : new AlignmentOperation(OperationKind.Substitution, oldTokens[r - 1], newTokens[c - 1]));
                    r--;
                    c--;
                    continue;
                }
            }
            if (r > 0 && costs[r, c] == costs[r - 1, c] + 1)
            {
                operations.Add(new AlignmentOperation(OperationKind.Deletion, oldTokens[r - 1], null));
                r--;
                continue;
            }
            if (c > 0)
            {
                operations.Add(new AlignmentOperation(OperationKind.Insertion, null, newTokens[c - 1]));
                c--;
            }
        }
        operations.Reverse();
        return operations;
    }

    private static (string Removed, string Inserted) ChangedFragments(string oldText, string newText)
    {
        var old = oldText ?? "";
        var current = newText ?? "";
        var prefix = 0;
        while (prefix < old.Length && prefix < current.Length && old[prefix] == current[prefix]) prefix++;

        var oldSuffix = old.Length;
        var newSuffix = current.Length;
        while (oldSuffix > prefix && newSuffix > prefix && old[oldSuffix - 1] == current[newSuffix - 1])
        {
            oldSuffix--;
            newSuffix--;
        }

        while (prefix > 0 && !IsBoundary(old[prefix - 1]) && !IsBoundary(current[prefix - 1])) prefix--;
        while (oldSuffix < old.Length && !IsBoundary(old[oldSuffix])) oldSuffix++;
        while (newSuffix < current.Length && !IsBoundary(current[newSuffix])) newSuffix++;

        return (old[prefix..oldSuffix], current[prefix..newSuffix]);
    }

    private static bool IsBoundary(char character) =>
        char.IsWhiteSpace(character) || (char.IsPunctuation(character) && character is not ('-' or '_' or '/' or '+'));

    private static bool IsTrimmedBoundary(char character) =>
        char.IsWhiteSpace(character) || (char.IsPunctuation(character) && character is not ('-' or '_' or '/' or '+'));

    private static string? NormalizedCandidate(string value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0 || trimmed.Contains('\n')) return null;
        var start = 0;
        var end = trimmed.Length;
        while (start < end && IsTrimmedBoundary(trimmed[start])) start++;
        while (end > start && IsTrimmedBoundary(trimmed[end - 1])) end--;
        var cleaned = trimmed[start..end];
        if (cleaned.Length == 0 || cleaned.Length > 60) return null;

        var tokens = SplitWhitespace(cleaned);
        if (tokens.Length is < 1 or > 3) return null;
        if (!tokens.All(token => token.Any(character => char.IsLetter(character) || char.IsNumber(character)))) return null;
        return string.Join(' ', tokens);
    }

    private static bool IsWordScopedSuggestion(string observed, string replacement)
    {
        var observedTokens = SplitWhitespace(observed);
        var replacementTokens = SplitWhitespace(replacement);
        if (replacementTokens.Length != 1 || observedTokens.Length is < 1 or > MaxObservedTokensPerDictionaryWord) return false;
        if (observedTokens.Length == 1)
        {
            if (IsAcronymLike(replacement))
            {
                return string.Equals(observed.ToLowerInvariant(), replacement.ToLowerInvariant(), StringComparison.Ordinal);
            }
            if (replacement.Contains('-') && !observed.Contains('-'))
            {
                return Similarity(observed.ToLowerInvariant(), replacement.ToLowerInvariant()) >= 0.82;
            }
            return true;
        }

        if (replacement.Contains('-') || replacement.Contains('_') || replacement.Contains('/') || IsAcronymLike(replacement)) return false;
        return Similarity(string.Concat(observedTokens).ToLowerInvariant(), replacement.ToLowerInvariant()) >= MinimumCorrectionSimilarity;
    }

    private static bool IsLikelyDictionaryCorrection(string observed, string replacement, IReadOnlySet<string> recognized)
    {
        if (string.Equals(observed, replacement, StringComparison.Ordinal)) return false;
        if (observed.Length < 2 || replacement.Length < 2) return false;

        var observedTokens = SplitWhitespace(observed);
        var replacementTokens = SplitWhitespace(replacement);
        var normalizedReplacement = replacement.ToLowerInvariant();
        var normalizedObserved = observed.ToLowerInvariant();
        var hasFormattingSignal = HasStrongDictionarySignal(replacement) || replacement.Contains('-');
        var similarity = Similarity(normalizedObserved, normalizedReplacement);

        if (observedTokens.Length != replacementTokens.Length)
        {
            if (observedTokens.Any(token => CommonWords.Contains(token.ToLowerInvariant()))) return false;
            var compactSimilarity = Similarity(string.Concat(observedTokens).ToLowerInvariant(), string.Concat(replacementTokens).ToLowerInvariant());
            return compactSimilarity >= 0.82 ||
                   IsLikelySingleWordSplitCorrection(replacement, replacementTokens, compactSimilarity, recognized) ||
                   IsLikelyStrongDictionaryCorrection(observed, replacement, observedTokens, compactSimilarity);
        }

        if (CommonWords.Contains(normalizedObserved))
        {
            if (normalizedObserved == normalizedReplacement) return hasFormattingSignal || replacement.Any(char.IsUpper);
            return similarity >= 0.82 && hasFormattingSignal;
        }

        if (CommonWords.Contains(normalizedReplacement) && !hasFormattingSignal) return false;
        if (normalizedObserved == normalizedReplacement) return hasFormattingSignal || replacement.Any(char.IsUpper);
        if (IsCommonWordTruncation(normalizedObserved, normalizedReplacement, recognized)) return false;

        return similarity >= MinimumCorrectionSimilarity ||
               IsLikelyStrongDictionaryCorrection(observed, replacement, observedTokens, similarity);
    }

    private static bool HasStrongDictionarySignal(string value) =>
        HasInternalCapital(value) || value.Any(char.IsNumber) || value.Contains('_') || value.Contains('/') || IsAcronymLike(value);

    private static bool IsLikelyStrongDictionaryCorrection(string observed, string replacement, IReadOnlyList<string> observedTokens, double similarity)
    {
        if (!HasStrongDictionarySignal(replacement)) return false;
        if (IsNumericShorthand(observed, replacement)) return true;
        if (IsAcronymLike(replacement) && IsAcronymCorrection(observedTokens, replacement)) return true;
        if (replacement.Contains('_') || replacement.Contains('/') || HasInternalCapital(replacement)) return similarity >= 0.55;
        return false;
    }

    private static bool IsLikelySingleWordSplitCorrection(string replacement, IReadOnlyList<string> replacementTokens, double similarity, IReadOnlySet<string> recognized)
    {
        if (replacementTokens.Count != 1 || replacement.Contains('-') || replacement.Contains('_') || replacement.Contains('/') ||
            IsAcronymLike(replacement) || CommonWords.Contains(replacement.ToLowerInvariant()) ||
            similarity < MinimumCorrectionSimilarity) return false;
        return replacement.Any(char.IsUpper) || !recognized.Contains(replacement.ToLowerInvariant());
    }

    private static bool IsNumericShorthand(string observed, string replacement)
    {
        var observedLetters = observed.ToLowerInvariant().Where(char.IsLetter).ToArray();
        var replacementCharacters = replacement.ToLowerInvariant().ToCharArray();
        if (observedLetters.Length < 4 || replacementCharacters.Length < 3 ||
            replacementCharacters.Length >= observedLetters.Length / 2 ||
            replacementCharacters[0] != observedLetters[0] ||
            replacementCharacters[^1] != observedLetters[^1]) return false;

        var middle = new string(replacementCharacters[1..^1]);
        if (middle.Length == 0 || !middle.All(char.IsNumber) || !int.TryParse(middle, NumberStyles.None, CultureInfo.InvariantCulture, out var omitted)) return false;
        return omitted == observedLetters.Length - 2;
    }

    private static bool IsAcronymCorrection(IReadOnlyList<string> observedTokens, string replacement)
    {
        if (observedTokens.Count <= 1) return false;
        var initials = string.Concat(observedTokens.Where(token => token.Length > 0).Select(token => char.ToLowerInvariant(token[0])));
        var replacementLetters = new string(replacement.ToLowerInvariant().Where(char.IsLetter).ToArray());
        return initials.Length > 0 && replacementLetters.Length > 0 && initials == replacementLetters;
    }

    private static bool IsCommonWordTruncation(string observed, string replacement, IReadOnlySet<string> recognized) =>
        SplitWhitespace(observed).Length == 1 &&
        SplitWhitespace(replacement).Length == 1 &&
        observed.All(char.IsLower) &&
        replacement.All(char.IsLower) &&
        observed != replacement &&
        (observed.StartsWith(replacement, StringComparison.Ordinal) || replacement.StartsWith(observed, StringComparison.Ordinal)) &&
        recognized.Contains(observed) &&
        recognized.Contains(replacement);

    private static bool HasInternalCapital(string value) => value.Length > 1 && value.Skip(1).Any(char.IsUpper);

    private static bool IsAcronymLike(string value)
    {
        var letters = value.Where(char.IsLetter).ToArray();
        return letters.Length >= 2 && letters.All(char.IsUpper);
    }

    /// <summary>
    /// The words, lower-cased, whose spelling decides a correction: those in the changed fragment and
    /// in every aligned change. The spell checker is asked about these before detection.
    /// </summary>
    public static IReadOnlySet<string> RequiredEnglishWordCandidates(string originalText, string editedText)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        void AddWords(string value)
        {
            foreach (var token in WordTokens(value))
            {
                if (token.Normalized.Length >= 2 && token.Normalized.All(char.IsLetter)) candidates.Add(token.Normalized);
            }
        }

        var (removed, inserted) = ChangedFragments(originalText, editedText);
        AddWords(removed);
        AddWords(inserted);

        var originalTokens = WordTokens(originalText);
        var editedTokens = WordTokens(editedText);
        if (!IsAlignmentInputWithinBounds(originalTokens.Count, editedTokens.Count)) return candidates;
        foreach (var run in TokenChangeRuns(AlignmentOperations(originalTokens, editedTokens)))
        {
            foreach (var token in run.Observed.Concat(run.Replacement))
            {
                if (token.Normalized.Length >= 2 && token.Normalized.All(char.IsLetter)) candidates.Add(token.Normalized);
            }
        }
        return candidates;
    }

    private static bool ContainsIgnoringCaseAndDiacritics(string text, string value) =>
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, value, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    private static string[] SplitWhitespace(string value) =>
        value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static double Similarity(string left, string right) => DictionaryCorrectionService.JaroWinkler(left, right);
}
