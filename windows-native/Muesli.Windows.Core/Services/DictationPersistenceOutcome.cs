namespace Muesli.Windows.Core.Services;

/// <summary>
/// macOS 9edaca3d: the completion message mentions History only after the History insert succeeded, and
/// says whether the paste did. Null when the save succeeded and the normal status stands.
/// </summary>
public static class DictationPersistenceOutcome
{
    public static string? Status(bool persisted, bool pasted) => persisted
        ? null
        : pasted
            ? "Dictation was pasted, but could not be saved to History."
            // Nothing else holds the text, so the caller has put it on the clipboard.
            : "Dictation wasn't pasted and could not be saved to History; transcript copied";
}
