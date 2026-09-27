namespace Muesli.Windows.Core.Services;

/// <summary>
/// Decides whether Escape (or another cancelling key) should be treated as cancelling an active
/// dictation, rather than being passed through to the focused application.
/// </summary>
/// <remarks>
/// The low-level hook asks this before swallowing the key. The WinUI host previously asked only
/// "is the coordinator recording or busy", which left three real cases uncancellable: a gesture
/// that is armed or preparing but has not started capture yet, a dictation that has stopped
/// recording and is transcribing, and one whose operation token is still live while the pipeline
/// finishes. The retired WPF host tested all five conditions; this is that predicate, in one
/// testable place.
/// </remarks>
public static class DictationCancellationPolicy
{
    /// <param name="hotkeyIsLive">The gesture is armed, preparing, holding, or hands-free.</param>
    /// <param name="isRecording">The coordinator is capturing audio.</param>
    /// <param name="isTranscribing">Capture has stopped and transcription is running.</param>
    /// <param name="isBusy">The coordinator is otherwise mid-operation.</param>
    /// <param name="hasActiveOperation">A dictation cancellation token is still alive.</param>
    public static bool CanCancel(
        bool hotkeyIsLive,
        bool isRecording,
        bool isTranscribing,
        bool isBusy,
        bool hasActiveOperation) =>
        hotkeyIsLive || isRecording || isTranscribing || isBusy || hasActiveOperation;
}
