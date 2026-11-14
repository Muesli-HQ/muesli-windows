namespace Muesli.Windows.Services;

/// <summary>Bounds native Parakeet inference for long recordings and restores meeting-relative times.</summary>
internal static class ParakeetAudioChunking
{
    private const int MaximumChunkSeconds = 30;
    private const int QuietSearchSeconds = 4;
    private const double QuietRmsThreshold = 0.015;

    public static IReadOnlyList<(int Start, int End)> Ranges(float[] samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        var maximumSamples = checked(sampleRate * MaximumChunkSeconds);
        var ranges = new List<(int Start, int End)>();
        for (var start = 0; start < samples.Length;)
        {
            var end = (int)Math.Min(samples.Length, (long)start + maximumSamples);
            if (end < samples.Length)
                end = QuietBoundary(samples, start, end, sampleRate);
            ranges.Add((start, end));
            start = end;
        }
        return ranges;
    }

    public static TranscriptionResult Merge(
        IReadOnlyList<(int StartSample, TranscriptionResult Transcript)> chunks,
        int sampleRate,
        int totalDurationMs)
    {
        var text = new List<string>(chunks.Count);
        var segments = new List<TranscriptSegment>();
        for (var index = 0; index < chunks.Count; index++)
        {
            var (startSample, transcript) = chunks[index];
            if (!string.IsNullOrWhiteSpace(transcript.Text)) text.Add(transcript.Text.Trim());
            var offsetMs = (int)Math.Round(startSample * 1000.0 / sampleRate);
            foreach (var segment in transcript.Segments ?? [])
                segments.Add(segment with
                {
                    Id = $"chunk_{index:D4}_{segment.Id}",
                    StartMs = checked(offsetMs + segment.StartMs),
                    EndMs = checked(offsetMs + segment.EndMs)
                });
        }

        return new TranscriptionResult(string.Join(" ", text),
            $"ASR engine: native-parakeet-sherpa-onnx; bounded chunks: {chunks.Count}",
            totalDurationMs, segments);
    }

    private static int QuietBoundary(float[] samples, int start, int maximumEnd, int sampleRate)
    {
        var window = Math.Max(1, sampleRate / 10);
        var searchStart = Math.Max(start + sampleRate * (MaximumChunkSeconds - QuietSearchSeconds),
            maximumEnd - sampleRate * QuietSearchSeconds);
        var bestEnergy = double.MaxValue;
        var bestBoundary = maximumEnd;
        for (var candidate = searchStart; candidate + window <= maximumEnd; candidate += window)
        {
            double energy = 0;
            for (var index = candidate; index < candidate + window; index++)
                energy += samples[index] * samples[index];
            if (energy >= bestEnergy) continue;
            bestEnergy = energy;
            bestBoundary = candidate + window / 2;
        }

        return bestEnergy / window <= QuietRmsThreshold * QuietRmsThreshold
            ? bestBoundary
            : maximumEnd;
    }
}
