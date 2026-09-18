namespace Muesli.Windows.Services;

/// <summary>Pipeline stages a media import passes through, in the order they run.</summary>
public enum MeetingImportStage
{
    Preparing,
    Decoding,
    Transcribing,
    CleaningUp,
    GeneratingNotes
}

/// <summary>What the import progress row should display right now.</summary>
public readonly record struct MeetingImportProgress(string Label, double Percent, bool IsIndeterminate);

/// <summary>
/// Maps a pipeline stage and its stage-local fraction onto one overall 0-100 bar.
///
/// The band widths are estimates, not measurements — what matters is that the overall percent is
/// monotonic as the import advances, so the bar never travels backwards. The label carries the
/// stage, which is the part the user actually reads.
/// </summary>
public static class MeetingImportProgressMapper
{
    private static readonly IReadOnlyDictionary<MeetingImportStage, (double Floor, double Ceiling, string Label)> Bands =
        new Dictionary<MeetingImportStage, (double, double, string)>
        {
            [MeetingImportStage.Preparing] = (0, 0, "Preparing"),
            [MeetingImportStage.Decoding] = (0, 20, "Decoding audio"),
            [MeetingImportStage.Transcribing] = (20, 80, "Transcribing"),
            [MeetingImportStage.CleaningUp] = (80, 88, "Cleaning up transcript"),
            [MeetingImportStage.GeneratingNotes] = (88, 100, "Generating notes")
        };

    public static MeetingImportStage From(TranscriptionStage stage) => stage switch
    {
        TranscriptionStage.Decoding => MeetingImportStage.Decoding,
        _ => MeetingImportStage.Transcribing
    };

    /// <param name="fraction">
    /// Stage-local completion, or null when the stage reports none. A null fraction parks the bar at
    /// the floor of its band and marks it indeterminate rather than inventing a number.
    /// </param>
    public static MeetingImportProgress Map(MeetingImportStage stage, double? fraction)
    {
        var (floor, ceiling, label) = Bands[stage];
        // A non-finite fraction is a broken measurement, not a position. Math.Clamp passes NaN
        // straight through, and a NaN bound to ProgressBar.Value corrupts the control, so treat it
        // exactly like a stage that reports nothing.
        if (fraction is not { } known || !double.IsFinite(known))
        {
            return new MeetingImportProgress(label, floor, IsIndeterminate: true);
        }

        var clamped = Math.Clamp(known, 0, 1);
        return new MeetingImportProgress(
            $"{label} — {clamped * 100:0}%",
            floor + (ceiling - floor) * clamped,
            IsIndeterminate: false);
    }

    /// <summary>The bar shown while a cancellation is being honoured, which has no measurable length.</summary>
    public static MeetingImportProgress Cancelling(double lastPercent) =>
        new("Cancelling…", lastPercent, IsIndeterminate: true);
}
