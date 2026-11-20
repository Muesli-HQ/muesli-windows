namespace Muesli.Windows.Cotypist;

/// <summary>How much Cotypist suggests at once (macOS CotypistSuggestionLength).</summary>
public enum CotypistSuggestionLength { Word, Phrase, Sentence }

/// <summary>How sure the model must be before Cotypist shows anything (macOS CotypistFrequency).</summary>
public enum CotypistFrequency { Fewer, Balanced, More }

/// <summary>How long a pause inside a word before Cotypist completes it (macOS CotypistWordPause).</summary>
public enum CotypistWordPause { Fast, Normal, Relaxed }

public static class CotypistOptions
{
    /// <summary>A persisted raw value, or the default when it isn't one of <paramref name="known"/>.</summary>
    public static string Normalize(string? value, string[] known, string fallback) =>
        known.FirstOrDefault(option => string.Equals(option, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? fallback;

    public static T Parse<T>(string value) where T : struct, Enum => Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : default;

    public static int MaxWords(this CotypistSuggestionLength length) => length switch
    {
        CotypistSuggestionLength.Word => 1,
        CotypistSuggestionLength.Phrase => 3,
        _ => 6
    };

    public static string Label(this CotypistSuggestionLength length) => length switch
    {
        CotypistSuggestionLength.Word => "One word",
        CotypistSuggestionLength.Phrase => "Short phrase",
        _ => "Up to a sentence"
    };

    public static string Label(this CotypistFrequency frequency) => frequency switch
    {
        CotypistFrequency.Fewer => "Fewer, more accurate",
        CotypistFrequency.Balanced => "Balanced",
        _ => "More often"
    };

    public static double Seconds(this CotypistWordPause pause) => pause switch
    {
        CotypistWordPause.Fast => 0.3,
        CotypistWordPause.Normal => 0.5,
        _ => 0.8
    };

    public static string Label(this CotypistWordPause pause) => pause switch
    {
        CotypistWordPause.Fast => "Short (0.3 s)",
        CotypistWordPause.Normal => "Normal (0.5 s)",
        _ => "Long (0.8 s)"
    };

    /// <summary>Minimum first-word log-probability. Calibrated on 600 cut points of real dictations;
    /// all three macOS models land on the same values.</summary>
    public static double FirstWordGate(CotypistFrequency frequency, bool midWord) => (frequency, midWord) switch
    {
        (CotypistFrequency.Fewer, _) => -1.0,
        (CotypistFrequency.Balanced, false) => -1.5,
        (CotypistFrequency.Balanced, true) => -2.0,
        (CotypistFrequency.More, false) => -2.5,
        _ => -3.0
    };
}

public readonly record struct CotypistToken(string Text, double LogProb);

public enum CotypistStop { EndOfText, Newline, Limit, HealFailed }

public sealed record CotypistCompletion(IReadOnlyList<CotypistToken> Tokens, CotypistStop Stop, double PromptMs = 0, double DecodeMs = 0, int ReusedTokens = 0)
{
    public string Content => string.Concat(Tokens.Select(token => token.Text));
}

public enum CotypistRejection { LineBreak, HealFailed, NothingBeyondTypedWord, ContinuesPreviousWord, LowConfidence, FirstWordIncomplete, Empty }

public sealed record CotypistShaped(string? Text, double FirstWordLogProb, CotypistRejection? Rejection = null)
{
    public bool IsShown => Rejection is null;
}

/// <summary>
/// Port of macOS CotypistShaper (2f10b23d): turns a raw continuation into what Cotypist shows, or a
/// reason to stay quiet. After a typed space the continuation must start a new word; the first word
/// must clear the gate; later words are kept while their average token log-probability clears the
/// trim threshold, up to <c>maxWords</c>, stopping after a sentence end; a final word is kept only
/// when known complete; with <c>heal</c>, tokens inside the half-typed word were forced, so they
/// don't count, and the shown text is what follows it.
/// </summary>
public static class CotypistShaper
{
    public sealed record Thresholds(double FirstWord = -3.0, double Trim = -2.5, int MaxWords = 6);

    public static CotypistShaped Shape(CotypistCompletion completion, bool afterSpace, string? heal, bool firstWordOnly, Thresholds? thresholds = null)
    {
        thresholds ??= new Thresholds();
        var tokens = completion.Tokens;
        if (completion.Stop == CotypistStop.HealFailed) return Reject(CotypistRejection.HealFailed);
        if (tokens.Count == 0) return Reject(CotypistRejection.LineBreak);
        // From the stop reason, not the token count: a character split across byte-level tokens is
        // emitted as one piece, so a budget-limited completion can have fewer pieces than maxTokens.
        var stoppedByModel = completion.Stop is CotypistStop.EndOfText or CotypistStop.Newline;
        var healLength = heal?.Length ?? 0;

        var words = new List<Word>();
        var consumed = 0;
        foreach (var token in tokens)
        {
            if (words.Count == 0 || token.Text.StartsWith(' ')) words.Add(new Word());
            var word = words[^1];
            word.Text += token.Text;
            consumed += token.Text.Length;
            if (consumed <= healLength) continue; // forced by the typed word, not the model's choice
            word.LogProb += token.LogProb;
            word.Count++;
        }
        if (healLength > 0 && consumed <= healLength) return Reject(CotypistRejection.NothingBeyondTypedWord);
        // A typed word the model left exactly as-is: the suggestion starts at the next word.
        if (healLength > 0 && words[0].Count == 0 && words.Count > 1)
        {
            healLength -= words[0].Text.Length;
            words.RemoveAt(0);
        }
        if (afterSpace && healLength == 0 && !words[0].Text.StartsWith(' ')) return Reject(CotypistRejection.ContinuesPreviousWord);
        var firstWordLogProb = words[0].LogProb;
        if (firstWordLogProb < thresholds.FirstWord) return Reject(CotypistRejection.LowConfidence, firstWordLogProb);
        if (firstWordOnly && words.Count < 2 && !stoppedByModel) return Reject(CotypistRejection.FirstWordIncomplete, firstWordLogProb);

        var text = "";
        var limit = firstWordOnly ? 1 : thresholds.MaxWords;
        for (var index = 0; index < Math.Min(limit, words.Count); index++)
        {
            var word = words[index];
            // The last generated word may be cut off mid-word unless the model stopped by itself,
            // the first one included: Tab must never insert half a word.
            if (index == words.Count - 1 && !stoppedByModel && !firstWordOnly) break;
            if (index > 0 && word.LogProb / Math.Max(word.Count, 1) < thresholds.Trim) break;
            text += word.Text;
            if (word.Text.Length > 0 && ".!?".Contains(word.Text[^1])) break;
        }
        var shown = healLength > 0 ? text[Math.Min(healLength, text.Length)..] : text;
        if (afterSpace && healLength == 0 && shown.StartsWith(' ')) shown = shown[1..];
        return shown.Trim(' ').Length == 0
            ? Reject(CotypistRejection.Empty, firstWordLogProb)
            : new CotypistShaped(shown, firstWordLogProb);
    }

    private static CotypistShaped Reject(CotypistRejection rejection, double logProb = 0) => new(null, logProb, rejection);

    private sealed class Word
    {
        public string Text = "";
        public double LogProb;
        public int Count;
    }
}

/// <summary>Prompt text helpers from macOS CotypistService.</summary>
public static class CotypistText
{
    /// <summary>The half-typed word to heal: after a space (kept with the word), at a line start, or at
    /// the start of the text. Needs two characters; null when there's nothing to complete.</summary>
    public static (string Before, string Heal)? SplitPartialWord(string text)
    {
        var start = text.Length;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        var partial = text[start..];
        if (partial.Length < 2) return null;
        return start > 0 && text[start - 1] == ' ' ? (text[..(start - 1)], " " + partial) : (text[..start], partial);
    }

    /// <summary>OCR comes back as a column of short lines; keep lines of 3+ words nearest the bottom
    /// of the window, as flowing text, so the base model doesn't predict a line break.</summary>
    public static string PromptReadyScreenText(string ocr, int maxCharacters = 600)
    {
        var lines = ocr.Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .Select(line => line.Trim(' ', '\t'))
            .Where(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3)
            .ToList();
        var kept = new List<string>();
        var length = 0;
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            if (length + lines[index].Length > maxCharacters) break;
            kept.Insert(0, lines[index]);
            length += lines[index].Length + 1;
        }
        return string.Join(' ', kept);
    }
}
