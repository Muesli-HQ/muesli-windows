namespace Muesli.Windows.Services.Persistence;

/// <summary>Compare-and-swap one dictation plus an optional rule in a single database transaction.</summary>
public interface IAtomicDictationCorrectionStore
{
    /// <param name="phraseToRemember">Null for a one-off edit; otherwise the selected original phrase.</param>
    /// <param name="confirmedConflicts">Exact conflicting records the user confirmed, rechecked inside the transaction.</param>
    DictationCorrectionResult SaveCorrection(string id, string expectedText, string replacementText,
        string? phraseToRemember, string? rememberedReplacement,
        IReadOnlyList<DictionaryEntryRecord>? confirmedConflicts = null);
}
