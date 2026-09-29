namespace Muesli.Windows.Services;

/// <summary>
/// Stage of a file transcription. Reported so a long import shows movement instead of a frozen
/// window, and so the user can tell which part of the pipeline is still holding things up.
/// </summary>
public enum TranscriptionStage
{
    /// <summary>Windows is decoding and resampling the media file to 16 kHz mono.</summary>
    Decoding,

    /// <summary>The recognizer is running inference over the decoded samples.</summary>
    Transcribing
}

/// <summary>A progress report from a file transcription.</summary>
/// <param name="Fraction">
/// Completion within the stage, clamped to 0..1, or null when the stage runs as a single
/// uninterruptible native call and no honest fraction exists. Callers should show an
/// indeterminate indicator for null rather than inventing a number.
/// </param>
public readonly record struct TranscriptionProgress(TranscriptionStage Stage, double? Fraction)
{
    public static TranscriptionProgress Decoding(double fraction) =>
        new(TranscriptionStage.Decoding, Normalize(fraction));

    public static TranscriptionProgress Transcribing(double fraction) =>
        new(TranscriptionStage.Transcribing, Normalize(fraction));

    /// <summary>
    /// A non-finite fraction means the measurement failed — a zero-length source, a bad duration
    /// estimate — so it degrades to "unmeasured" rather than propagating NaN into the UI.
    /// Math.Clamp does not filter NaN.
    /// </summary>
    private static double? Normalize(double fraction) =>
        double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : null;

    /// <summary>Inference that cannot be subdivided, so no fraction is reported.</summary>
    public static TranscriptionProgress TranscribingUnmeasured() =>
        new(TranscriptionStage.Transcribing, null);
}
