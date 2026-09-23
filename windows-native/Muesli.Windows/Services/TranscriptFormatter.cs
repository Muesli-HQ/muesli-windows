namespace Muesli.Windows.Services;

public static class TranscriptFormatter
{
    private const int ConsolidationGapThresholdMs = 2000;

    public static string Merge(
        List<TranscriptSegment> micSegments,
        List<TranscriptSegment> systemSegments,
        List<DiarizedSegment> diarizationSegments,
        DateTime meetingStart)
        => MergeWithSegments(micSegments, systemSegments, diarizationSegments, meetingStart).Transcript;

    public static TranscriptFormatResult MergeWithSegments(
        List<TranscriptSegment> micSegments,
        List<TranscriptSegment> systemSegments,
        List<DiarizedSegment> diarizationSegments,
        DateTime meetingStart)
    {
        // Map raw diarization speaker IDs → "Speaker 1", "Speaker 2" in first-appearance order.
        var speakerLabelMap = new Dictionary<string, string>(StringComparer.Ordinal);
        int nextSpeakerNumber = 1;
        foreach (var seg in (diarizationSegments ?? []).OrderBy(s => s.StartMs))
        {
            if (!speakerLabelMap.ContainsKey(seg.SpeakerId))
            {
                speakerLabelMap[seg.SpeakerId] = $"Speaker {nextSpeakerNumber++}";
            }
        }

        // System audio remains useful without diarization. Keep it in the real timeline and
        // use one truthful fallback label rather than moving the whole track to the end.
        var taggedSystem = systemSegments
            .Select(seg => new MeetingTranscriptSegment(
                "system",
                diarizationSegments is { Count: > 0 }
                    ? FindSpeakerByOverlap(seg, diarizationSegments, speakerLabelMap) ?? "System audio"
                    : "System audio",
                seg.StartMs,
                seg.EndMs,
                seg.Text))
            .ToList();

        var taggedMic = micSegments
            .Select(seg => new MeetingTranscriptSegment(
                "microphone",
                "You",
                seg.StartMs,
                seg.EndMs,
                seg.Text))
            .ToList();

        var all = taggedMic.Concat(taggedSystem)
            .Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
            .OrderBy(segment => segment.StartMs)
            .ThenBy(segment => segment.Track, StringComparer.Ordinal)
            .ToList();

        if (all.Count == 0)
        {
            return new TranscriptFormatResult("", []);
        }

        var consolidated = Consolidate(all, ConsolidationGapThresholdMs);

        var transcript = string.Join(Environment.NewLine, consolidated.Select(segment =>
        {
            var timestamp = meetingStart.AddMilliseconds(segment.StartMs);
            return $"[{timestamp:HH:mm:ss}] {segment.Speaker}: {segment.Text.Trim()}";
        }));
        return new TranscriptFormatResult(transcript, all);
    }

    private static string? FindSpeakerByOverlap(
        TranscriptSegment transcriptSegment,
        List<DiarizedSegment> diarizationSegments,
        Dictionary<string, string> speakerLabelMap)
    {
        // Find diarization segment with maximum time overlap
        DiarizedSegment? bestMatch = null;
        double bestOverlap = 0;

        foreach (var diarSeg in diarizationSegments)
        {
            var overlap = CalculateOverlap(
                transcriptSegment.StartMs, transcriptSegment.EndMs,
                diarSeg.StartMs, diarSeg.EndMs);

            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                bestMatch = diarSeg;
            }
        }

        if (bestMatch is null || bestOverlap <= 0)
        {
            return null;
        }

        return speakerLabelMap.TryGetValue(bestMatch.SpeakerId, out var label)
            ? label
            : "Others";
    }

    private static double CalculateOverlap(int startA, int endA, int startB, int endB)
    {
        var overlapStart = Math.Max(startA, startB);
        var overlapEnd = Math.Min(endA, endB);
        return Math.Max(0, overlapEnd - overlapStart);
    }

    private static List<MeetingTranscriptSegment> Consolidate(
        List<MeetingTranscriptSegment> segments,
        int gapThresholdMs)
    {
        var result = new List<MeetingTranscriptSegment>();
        if (segments.Count == 0)
        {
            return result;
        }

        var currentSpeaker = segments[0].Speaker;
        var currentTrack = segments[0].Track;
        var currentStartMs = segments[0].StartMs;
        var currentEndMs = segments[0].EndMs;
        var currentText = segments[0].Text;

        for (int i = 1; i < segments.Count; i++)
        {
            var seg = segments[i];
            var gap = Math.Max(0, seg.StartMs - currentEndMs);

            if (seg.Speaker == currentSpeaker
                && seg.Track == currentTrack
                && gap <= gapThresholdMs)
            {
                currentText = AppendText(currentText, seg.Text, gap);
                currentEndMs = Math.Max(currentEndMs, seg.EndMs);
            }
            else
            {
                result.Add(new MeetingTranscriptSegment(
                    currentTrack,
                    currentSpeaker,
                    currentStartMs,
                    currentEndMs,
                    currentText));

                currentSpeaker = seg.Speaker;
                currentTrack = seg.Track;
                currentStartMs = seg.StartMs;
                currentEndMs = seg.EndMs;
                currentText = seg.Text;
            }
        }

        result.Add(new MeetingTranscriptSegment(
            currentTrack,
            currentSpeaker,
            currentStartMs,
            currentEndMs,
            currentText));

        return result;
    }

    private static string AppendText(string current, string next, int gapMs)
    {
        current = current.Trim();
        next = next.Trim();

        if (string.IsNullOrEmpty(current))
        {
            return next;
        }

        if (string.IsNullOrEmpty(next))
        {
            return current;
        }

        // If there's a significant gap (>1s), treat as separate sentences
        if (gapMs > 1000)
        {
            var separator = current.EndsWith('.') || current.EndsWith('!') || current.EndsWith('?')
                ? " "
                : ". ";
            return current + separator + next;
        }

        // Small gap — just space-separate
        return current + " " + next;
    }
}

public sealed record MeetingTranscriptSegment(
    string Track,
    string Speaker,
    int StartMs,
    int EndMs,
    string Text);

public sealed record TranscriptFormatResult(
    string Transcript,
    List<MeetingTranscriptSegment> Segments);
