namespace Muesli.Windows.Core.Services;

/// <summary>
/// The visual states the floating dictation indicator publishes. These mirror the compact pill
/// states of the retired WPF reference (<c>ToastState</c>) without pulling any WPF dependency in.
/// </summary>
public enum FloatingIndicatorState
{
    Idle,
    Preparing,
    Recording,
    Transcribing,
    Success,
    Error
}

/// <summary>
/// Maps a dictation <c>Status</c> string plus the live capture flags onto one of the
/// <see cref="FloatingIndicatorState"/> values. Kept in the neutral core so the WinUI indicator
/// window (and any future host) share one truthful classification and it can be unit-tested
/// without a UI runtime.
/// </summary>
public static class FloatingIndicatorStateClassifier
{
    /// <summary>
    /// Markers that mean the user cancelled the active dictation, which returns the pill to idle
    /// rather than flashing a red error.
    /// </summary>
    private static readonly string[] CancelMarkers = ["cancelled", "cancel"];

    private static readonly string[] ErrorMarkers = ["failed", "error", "unavailable", "no speech", "no text remained"];

    /// <summary>
    /// A dictation that landed in the focused app is the normal outcome: macOS returns straight to
    /// idle (its floating pill has no completion toast), so "inserted" is deliberately absent.
    /// </summary>
    private static readonly string[] SuccessMarkers = ["saved", "captured", "pasted", "copied"];

    private static readonly string[] PreparingMarkers = ["preparing", "hold to dictate", "listening", "arming", "shortcut"];

    /// <summary>Cancellation is a user-requested return to idle, never an error toast.</summary>
    public const string Cancelled = "cancelled";

    public static FloatingIndicatorState Classify(string status, bool isRecording, bool isTranscribing)
    {
        if (isRecording)
        {
            return FloatingIndicatorState.Recording;
        }

        if (isTranscribing)
        {
            return FloatingIndicatorState.Transcribing;
        }

        if (ContainsAny(status, CancelMarkers))
        {
            return FloatingIndicatorState.Idle;
        }

        if (ContainsAny(status, ErrorMarkers))
        {
            return FloatingIndicatorState.Error;
        }

        if (ContainsAny(status, SuccessMarkers))
        {
            return FloatingIndicatorState.Success;
        }

        if (ContainsAny(status, PreparingMarkers))
        {
            return FloatingIndicatorState.Preparing;
        }

        return FloatingIndicatorState.Idle;
    }

    /// <summary>True while the pill is showing a live capture/preparation/transcription surface.</summary>
    public static bool IsActive(FloatingIndicatorState state) =>
        state is FloatingIndicatorState.Preparing
            or FloatingIndicatorState.Recording
            or FloatingIndicatorState.Transcribing;

    /// <summary>
    /// A status is either a hard failure (error), a finished-ok report (success), or a neutral
    /// outcome that should return to idle without emphasising it. Cancellation and short/no-speech
    /// outcomes are neutral.
    /// </summary>
    public static bool IsTransientOutcome(FloatingIndicatorState state) =>
        state is FloatingIndicatorState.Success or FloatingIndicatorState.Error;

    /// <summary>
    /// True when an outcome pill has already had its moment on screen and must not be shown again.
    /// </summary>
    /// <remarks>
    /// The indicator derives its state from the dictation <c>Status</c> string, and that string is
    /// sticky: it keeps its last value until the next dictation changes it. Without this check the
    /// success/error dwell timer re-classifies the same text when it fires, re-enters the same
    /// outcome, and re-arms itself — so the pill never returns to idle. Comparing against the
    /// status that was actually dismissed means a genuinely new outcome still shows, even when it
    /// repeats an earlier message.
    /// </remarks>
    public static bool IsAlreadyDismissed(
        FloatingIndicatorState state,
        string? status,
        string? dismissedStatus) =>
        IsTransientOutcome(state) &&
        dismissedStatus is not null &&
        string.Equals(status ?? "", dismissedStatus, StringComparison.Ordinal);

    private static bool ContainsAny(string? value, string[] markers) =>
        markers.Any(marker => (value ?? "").Contains(marker, StringComparison.OrdinalIgnoreCase));
}
