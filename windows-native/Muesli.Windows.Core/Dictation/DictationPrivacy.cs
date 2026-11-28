namespace Muesli.Windows.Services;

/// <summary>
/// macOS "Privacy mode" (<c>ContentPrivacy</c>) for dictation: a private dictation is pasted as
/// usual but is not saved to history, keeps no audio and feeds no dictionary suggestions.
/// </summary>
public static class DictationPrivacy
{
    /// <summary>
    /// macOS captures the mode when a dictation starts and revokes saving as soon as it is turned on,
    /// so a dictation is private if the mode was on at the start or is on now. Turning it off part
    /// way through does not make a private dictation saveable.
    /// </summary>
    public static bool IsPrivate(bool onAtStart, bool onNow) => onAtStart || onNow;

    /// <summary>
    /// Status after a private dictation was pasted. It carries none of the pill's success or error
    /// words, so the pill returns to idle as it does after any inserted dictation.
    /// </summary>
    public const string InsertedStatus = "Dictation inserted (privacy mode)";

    /// <summary>Status when a private dictation had no app to paste into and went to the clipboard.</summary>
    public const string CopiedStatus = "Dictation copied (privacy mode, not saved)";
}
