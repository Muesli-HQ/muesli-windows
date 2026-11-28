namespace Muesli.Windows.Services;

/// <summary>
/// How a dictation reaches the app it was spoken into: macOS "Paste shortcut" (Automatic, or a
/// recorded chord). Windows offers the chords apps actually use instead of a recorder: Ctrl+V for
/// most apps, Ctrl+Shift+V and Shift+Insert for terminals and some editors, and typing the text for
/// apps that block pasting.
/// </summary>
public static class PasteShortcuts
{
    public const string Automatic = "ctrl_v";
    public const string CtrlShiftV = "ctrl_shift_v";
    public const string ShiftInsert = "shift_insert";
    public const string TypeText = "type";

    public static readonly IReadOnlyList<string> All = [Automatic, CtrlShiftV, ShiftInsert, TypeText];

    public static string Normalize(string? value)
    {
        var candidate = value?.Trim().ToLowerInvariant();
        return All.FirstOrDefault(item => item == candidate) ?? Automatic;
    }

    public static string Label(string value) => Normalize(value) switch
    {
        CtrlShiftV => "Ctrl+Shift+V",
        ShiftInsert => "Shift+Insert",
        TypeText => "Type the text",
        _ => "Automatic (Ctrl+V)"
    };

    /// <summary>The chord to send for a paste, or null when the text is typed instead.</summary>
    public static PasteChord? Chord(string value) => Normalize(value) switch
    {
        CtrlShiftV => PasteChord.CtrlShiftV,
        ShiftInsert => PasteChord.ShiftInsert,
        TypeText => null,
        _ => PasteChord.CtrlV
    };
}

/// <summary>A keyboard chord that pastes the clipboard in the target app.</summary>
public enum PasteChord
{
    CtrlV,
    CtrlShiftV,
    ShiftInsert
}
