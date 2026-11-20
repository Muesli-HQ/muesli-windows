namespace Muesli.Windows.Services;

/// <summary>
/// macOS BufferedDictationInsertionStopReason (4481c15a): live text stopped while the microphone keeps
/// recording. A recording notice, not a warning: stop and cancel stay active and the final dictation
/// is still saved. The status line carries <see cref="Detail"/>; the pill shows <see cref="Title"/>.
/// </summary>
public enum BufferedDictationInsertionStop
{
    CursorOwnershipLost,
    TranscriptionFailed
}

public static class BufferedDictationInsertionStops
{
    public static string Title(this BufferedDictationInsertionStop reason) => reason switch
    {
        BufferedDictationInsertionStop.CursorOwnershipLost => "Cursor updates stopped · Still recording",
        _ => "Live transcription stopped · Still recording"
    };

    /// <summary>The macOS notch's short form.</summary>
    public static string CompactTitle(this BufferedDictationInsertionStop reason) => reason switch
    {
        BufferedDictationInsertionStop.CursorOwnershipLost => "Cursor stopped",
        _ => "Live text stopped"
    };

    public static string Detail(this BufferedDictationInsertionStop reason) => reason switch
    {
        BufferedDictationInsertionStop.CursorOwnershipLost =>
            "Cursor updates stopped. Still recording; the final dictation will be saved in history.",
        _ => "Live transcription stopped. Still recording; audio and the available transcript will be saved for recovery when you stop."
    };

    /// <summary>The reason a recording dictation's status announces, if any.</summary>
    public static BufferedDictationInsertionStop? FromStatus(string? status) =>
        Enum.GetValues<BufferedDictationInsertionStop>().Cast<BufferedDictationInsertionStop?>()
            .FirstOrDefault(reason => string.Equals(status, reason!.Value.Detail(), StringComparison.Ordinal));
}
