namespace Muesli.Windows.Services;

/// <summary>
/// Shared feature-tour copy for WPF and WinUI. Version 2 is the shipping beat list; bump it when
/// a new required beat is added so both shells can replay the tour.
/// </summary>
public static class FeatureTourCatalog
{
    public const int CurrentVersion = 2;

    public static IReadOnlyList<FeatureTourStep> Steps { get; } =
    [
        new(
            "Dictation and real history",
            "Hold your shortcut to dictate and release it to transcribe. Only successful real dictations appear in history and drive your statistics.",
            "dictations"),
        new(
            "Floating indicator",
            "The pill on the edge of the screen is the live recording control. Hold to arm, release to transcribe, and click × to cancel. Status toasts never leave it stuck on Recording.",
            "FloatingIndicator"),
        new(
            "Separate model roles",
            "Dictation and final meeting/import transcription use independent offline role selections. Live preview is optional; selecting a model never starts a download.",
            "models"),
        new(
            "Meetings: detected now",
            "Detected now is based on current Windows meeting evidence. Upcoming meetings stay unavailable until Muesli has a real calendar source; it never fabricates calendar entries.",
            "meetings"),
        new(
            "Tray and startup",
            "The tray exposes real recent metadata, setup resume, pause/stop for a live meeting, and detected-now state. Launch at login is read from Windows and can be repaired only when you choose.",
            "settings"),
        new(
            "Privacy, diagnostics, accessibility",
            "Audio and models are local by default. Optional cloud summaries disclose their destination. About provides privacy, logs and safe diagnostics; keyboard focus and Escape work throughout Muesli.",
            "about")
    ];
}

public sealed record FeatureTourStep(string Title, string Body, string TargetName);
