namespace Muesli.Windows.Services;

/// <summary>
/// Turns what the meeting model transcribed during the recording into the final transcript's
/// source, with the stretches that still need transcribing from the retained audio.
/// </summary>
public static class RollingTranscriptOwnership
{
    public static MeetingLiveTranscriptionResult Build(
        IReadOnlyList<LiveTranscriptSegment> committed,
        IReadOnlyList<LiveTranscriptGap> gaps,
        long droppedChunks,
        string modelId)
    {
        var allGaps = gaps.ToList();
        // macOS falls back to the whole computer-audio file when no piece of it survived: speech
        // detection may have missed everything, and that track is the other side of the call.
        if (!committed.Any(segment => segment.Channel == LiveTranscriptChannel.System))
        {
            allGaps.Add(new LiveTranscriptGap(LiveTranscriptChannel.System, 0, long.MaxValue, "no-system-transcript"));
        }
        return new MeetingLiveTranscriptionResult(
            committed.OrderBy(segment => segment.StartSample).ToList(),
            MeetingLiveTranscriptionSession.CoalesceGaps(allGaps),
            droppedChunks,
            modelId,
            LiveTranscriptOwnershipMode.UnifiedLiveAndFinal);
    }
}