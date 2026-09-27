namespace Muesli.Windows.Services;

/// <summary>One captured part of a channel: where it belongs in meeting time, and how long it is.</summary>
public sealed record MeetingTrackPart(long StartOffsetMs, long DurationMs);

/// <summary>
/// Normalizes per-channel transcript and diarization timestamps onto a single meeting timeline.
///
/// Final ASR runs on a concatenated track, so its timestamps are positions inside that track. When a
/// channel starts late, is repaired, or resumes after a suspend, the concatenation removes the wall-clock
/// silence between parts, and track time drifts away from meeting time. Merging two channels that drifted
/// by different amounts places remote speech at the wrong point in the conversation, so every channel is
/// mapped back onto meeting time before the chronological merge.
/// </summary>
public static class MeetingTranscriptTimeline
{
    /// <summary>
    /// Builds contiguous parts for a channel that has no recorded anchors. This reproduces the
    /// pre-schema-3 assumption exactly, so migrated journals keep their existing behaviour.
    /// </summary>
    public static IReadOnlyList<MeetingTrackPart> Contiguous(IEnumerable<long> durationsMs)
    {
        var parts = new List<MeetingTrackPart>();
        long cursor = 0;
        foreach (var duration in durationsMs)
        {
            var safeDuration = Math.Max(0, duration);
            parts.Add(new(cursor, safeDuration));
            cursor += safeDuration;
        }
        return parts;
    }

    /// <summary>
    /// Pairs recorded offsets with measured durations. Missing or short offset lists fall back to
    /// contiguous placement for the remaining parts rather than dropping audio from the timeline.
    /// </summary>
    public static IReadOnlyList<MeetingTrackPart> Build(
        IReadOnlyList<long> durationsMs,
        IReadOnlyList<long>? offsetsMs)
    {
        if (offsetsMs is null || offsetsMs.Count == 0) return Contiguous(durationsMs);

        var parts = new List<MeetingTrackPart>(durationsMs.Count);
        long cursor = 0;
        for (var index = 0; index < durationsMs.Count; index++)
        {
            var duration = Math.Max(0, durationsMs[index]);
            // A recorded anchor never moves a part earlier than the end of the previous one; that
            // would overlap audio that was captured sequentially.
            var offset = index < offsetsMs.Count ? Math.Max(cursor, offsetsMs[index]) : cursor;
            parts.Add(new(offset, duration));
            cursor = offset + duration;
        }
        return parts;
    }

    /// <summary>Maps a position on the concatenated track timeline to milliseconds after meeting start.</summary>
    public static long ToMeetingMs(long trackMs, IReadOnlyList<MeetingTrackPart> parts)
    {
        if (parts.Count == 0) return Math.Max(0, trackMs);
        var remaining = Math.Max(0, trackMs);
        long consumed = 0;
        foreach (var part in parts)
        {
            if (remaining < consumed + part.DurationMs)
            {
                return part.StartOffsetMs + (remaining - consumed);
            }
            consumed += part.DurationMs;
        }
        // Past the end of the captured audio: extend from the final part rather than clamping,
        // so a segment that overruns the measured duration keeps its ordering.
        var last = parts[^1];
        return last.StartOffsetMs + last.DurationMs + (remaining - consumed);
    }

    public static List<TranscriptSegment> Normalize(
        IEnumerable<TranscriptSegment>? segments,
        IReadOnlyList<MeetingTrackPart> parts)
    {
        if (segments is null) return [];
        if (parts.Count <= 1 && (parts.Count == 0 || parts[0].StartOffsetMs == 0)) return segments.ToList();
        return segments
            .Select(segment =>
            {
                var start = ToMeetingMs(segment.StartMs, parts);
                var end = ToMeetingMs(segment.EndMs, parts);
                return segment with { StartMs = (int)Clamp(start), EndMs = (int)Clamp(Math.Max(start, end)) };
            })
            .OrderBy(segment => segment.StartMs)
            .ToList();
    }

    public static List<DiarizedSegment> Normalize(
        IEnumerable<DiarizedSegment>? segments,
        IReadOnlyList<MeetingTrackPart> parts)
    {
        if (segments is null) return [];
        if (parts.Count <= 1 && (parts.Count == 0 || parts[0].StartOffsetMs == 0)) return segments.ToList();
        return segments
            .Select(segment =>
            {
                var start = ToMeetingMs(segment.StartMs, parts);
                var end = ToMeetingMs(segment.EndMs, parts);
                return segment with { StartMs = (int)Clamp(start), EndMs = (int)Clamp(Math.Max(start, end)) };
            })
            .OrderBy(segment => segment.StartMs)
            .ToList();
    }

    private static long Clamp(long value) => Math.Clamp(value, 0, int.MaxValue);
}
