using System.Globalization;
using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Services;

/// <summary>An immutable UTF-16 selection, captured before the editor takes focus. Creating or
/// abandoning this value never writes. Saving rechecks the complete source to reject stale selections.</summary>
public sealed record DictationCorrectionSelection(string DictationId, string SourceText, int Start, int Length)
{
    public string Original => SourceText.Substring(Start, Length);
}

public enum DictationCorrectionOutcome { Saved, Invalid, Conflict, Stale, Failed }

public sealed record DictationCorrectionResult(
    DictationCorrectionOutcome Outcome, string Message,
    IReadOnlyList<DictionaryEntryRecord>? Conflicts = null)
{
    public bool Succeeded => Outcome == DictationCorrectionOutcome.Saved;
}

/// <summary>Explicit saved-history corrections. No clipboard, capture, or recognition side effects.
/// The dictation text and an optional dictionary rule are saved in one SQLite transaction, so a
/// correction is either fully saved or not saved at all.</summary>
public sealed class DictationCorrectionService(ILibraryHistoryAdapter history)
{
    /// <summary>WinUI's text editor normalizes line endings. Translate its UTF-16 boundaries back
    /// to the saved text without searching for the phrase (which may occur more than once).</summary>
    public static DictationCorrectionSelection CaptureSelection(string id, string source, string displayed, int start, int length)
    {
        var sourceStart = -1;
        var sourceEnd = -1;
        var sourceOffset = 0;
        var displayOffset = 0;
        while (true)
        {
            if (displayOffset == start) sourceStart = sourceOffset;
            if (displayOffset == start + length) sourceEnd = sourceOffset;
            if (sourceOffset == source.Length || displayOffset == displayed.Length) break;
            if (source[sourceOffset] is '\r' or '\n' && displayed[displayOffset] is '\r' or '\n')
            {
                sourceOffset += source[sourceOffset] == '\r' && sourceOffset + 1 < source.Length && source[sourceOffset + 1] == '\n' ? 2 : 1;
                displayOffset += displayed[displayOffset] == '\r' && displayOffset + 1 < displayed.Length && displayed[displayOffset + 1] == '\n' ? 2 : 1;
            }
            else
            {
                if (source[sourceOffset++] != displayed[displayOffset++]) return new(id, source, 0, 0);
            }
        }
        return sourceOffset == source.Length && displayOffset == displayed.Length && sourceStart >= 0 && sourceEnd >= sourceStart
            ? TrimSelection(new(id, source, sourceStart, sourceEnd - sourceStart)) : new(id, source, 0, 0);
    }

    /// <summary>Exclude incidental whitespace from the edit range, leaving it untouched in history.</summary>
    public static DictationCorrectionSelection TrimSelection(DictationCorrectionSelection selection)
    {
        if (selection.Start < 0 || selection.Length <= 0 || selection.Start > selection.SourceText.Length - selection.Length)
            return selection;
        var start = selection.Start;
        var end = start + selection.Length;
        while (start < end && char.IsWhiteSpace(selection.SourceText[start])) start++;
        while (end > start && char.IsWhiteSpace(selection.SourceText[end - 1])) end--;
        return selection with { Start = start, Length = end - start };
    }

    public static string? ValidateSelection(DictationCorrectionSelection selection)
    {
        selection = TrimSelection(selection);
        var text = selection.SourceText;
        if (selection.Start < 0 || selection.Length <= 0 || selection.Start > text.Length - selection.Length)
            return "Select a word or short phrase in the dictation first.";
        var boundaries = StringInfo.ParseCombiningCharacters(text);
        var end = selection.Start + selection.Length;
        if (!boundaries.Contains(selection.Start) || (end != text.Length && !boundaries.Contains(end)))
            return "Select complete characters, including their accents or emoji.";
        return ValidatePhrase(selection.Original, "Select");
    }

    private static string? ValidatePhrase(string text, string verb)
    {
        if (string.IsNullOrWhiteSpace(text)) return $"{verb} a word or short phrase.";
        if (text.Length > 200 || text.Any(char.IsControl) || text.Contains('\u2028') || text.Contains('\u2029'))
            return $"{verb} a single-line phrase of at most 200 characters.";
        // Reject malformed UTF-16, while preserving valid surrogate pairs and grapheme clusters.
        for (var i = 0; i < text.Length; i++)
            if (char.IsSurrogate(text[i]) &&
                (!char.IsHighSurrogate(text[i]) || ++i >= text.Length || !char.IsLowSurrogate(text[i])))
                return $"{verb} complete Unicode characters.";
        return null;
    }

    public DictationCorrectionResult Save(
        DictationCorrectionSelection selection, string replacement, bool remember,
        IReadOnlyList<DictionaryEntryRecord>? confirmedConflicts = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return new(DictationCorrectionOutcome.Failed, "Correction cancelled. Nothing was changed.");
        selection = TrimSelection(selection);
        replacement = replacement.Trim();
        var validation = ValidateSelection(selection) ?? ValidatePhrase(replacement, "Enter");
        if (validation is not null) return new(DictationCorrectionOutcome.Invalid, validation);
        if (selection.Original == replacement)
            return new(DictationCorrectionOutcome.Invalid, "Enter a replacement different from the selected text.");
        if (remember && !DictionaryCorrectionService.CanRememberSelection(selection.SourceText, selection.Start, selection.Length))
            return new(DictationCorrectionOutcome.Invalid,
                "To remember a correction, select whole words (up to 12), without surrounding punctuation. Uncheck remembering to make a one-off edit.");
        var updated = selection.SourceText.Remove(selection.Start, selection.Length).Insert(selection.Start, replacement);
        if (history is IAtomicDictationCorrectionStore atomicStore)
            return atomicStore.SaveCorrection(selection.DictationId, selection.SourceText, updated,
                remember ? selection.Original : null, remember ? replacement : null, confirmedConflicts);
        return new(DictationCorrectionOutcome.Failed, "This history store does not support corrections.");
    }
}
