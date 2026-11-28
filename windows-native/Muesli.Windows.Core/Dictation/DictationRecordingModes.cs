using Muesli.Windows.Core.Contracts;

namespace Muesli.Windows.Services;

/// <summary>
/// macOS "Dictation recording mode" (<c>HotkeyMonitor.CombinationActivation</c>): hold the shortcut
/// to talk, or press it once to start and again to stop. As on macOS, press to toggle needs a
/// modifier + key shortcut, so a single key such as F8 or Right Ctrl is never left without a way to
/// end a recording by releasing it.
/// </summary>
public static class DictationRecordingModes
{
    public const string HoldToTalk = "push_to_talk";
    public const string PressToToggle = "toggle";

    public const string ToggleNeedsCombination = "Choose a modifier + key before enabling Press to toggle.";

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), PressToToggle, StringComparison.OrdinalIgnoreCase) ? PressToToggle : HoldToTalk;

    public static string Label(string mode) => mode == PressToToggle ? "Press to toggle" : "Hold to talk";

    /// <summary>A shortcut made of at least one modifier and one key, such as Ctrl+Shift+D.</summary>
    public static bool IsCombination(string? hotkey)
    {
        try
        {
            return HotkeyGestureParser.Parse(hotkey).Modifiers != HotkeyModifiers.None;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Whether the shortcut toggles right now: the mode is chosen and the shortcut allows it.</summary>
    public static bool IsToggleActive(MuesliSettings settings) =>
        Normalize(settings.DictationRecordingMode) == PressToToggle && IsCombination(settings.Hotkey);

    /// <summary>macOS <c>dictationStartPrompt</c>: "Press Ctrl+Shift+D to dictate" or "Hold F8 to dictate".</summary>
    public static string StartVerb(bool toggle) => toggle ? "Press" : "Hold";
}
