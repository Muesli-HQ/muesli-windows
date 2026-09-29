namespace Muesli.Windows.Services;

public sealed record WindowsPermissionRow(string Name, string Status, bool Granted, string Help);

/// <summary>
/// Windows-native permission copy. macOS Accessibility, Input Monitoring, and Screen Recording
/// labels are not reused; each row names the actual Windows capability or an honest absence.
/// </summary>
public static class WindowsPermissionStatus
{
    public static IReadOnlyList<WindowsPermissionRow> Build(
        bool microphoneChecked,
        bool microphoneGranted,
        string microphoneStatus,
        string microphoneHelp)
    {
        return
        [
            new(
                "Microphone",
                microphoneChecked ? microphoneStatus : "Not checked yet",
                microphoneGranted,
                string.IsNullOrWhiteSpace(microphoneHelp)
                    ? "A short non-retained WASAPI capture is the real microphone check."
                    : microphoneHelp),
            new(
                "Active-app paste",
                "UIPI-aware",
                true,
                "Paste targets the previously focused window. Elevated apps can still refuse input."),
            new(
                "System audio",
                "Reported per meeting",
                true,
                "Meetings use process-tree loopback when Windows allows it, otherwise a disclosed render-endpoint fallback, or they record microphone only."),
            new(
                "Computer Use screenshots",
                "Unavailable",
                false,
                "Window text, page text, and screenshots stay off until scoped masking is qualified."),
            new(
                "Calendar / iPhone sync",
                "Not available",
                false,
                "EventKit, Google Calendar OAuth, and iCloud text sync are excluded on Windows.")
        ];
    }
}
