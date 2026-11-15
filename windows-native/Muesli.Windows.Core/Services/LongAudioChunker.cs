namespace Muesli.Windows.Services;

/// <summary>
/// Cuts a long recording into pieces a speech model can decode one at a time. A whole-file decode
/// needs memory that grows much faster than the audio length, so a few minutes of meeting audio in
/// one pass can exhaust an 8 GB PC and stall for many minutes. Pieces are cut at the quietest point
/// near the end of each window, so a cut lands in a pause rather than inside a word.
/// </summary>
public static class LongAudioChunker
{
    /// <summary>Recordings up to this length are decoded in one pass, as before.</summary>
    public const double SinglePassSeconds = 45;

    /// <summary>Longest piece handed to the model.</summary>
    public const double MaxChunkSeconds = 30;

    /// <summary>How far back from the end of a window the quietest point is looked for.</summary>
    public const double SearchSeconds = 8;

    private const double FrameSeconds = 0.05;

    public readonly record struct Chunk(int Start, int Length);

    public static IReadOnlyList<Chunk> Split(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (sampleRate <= 0 || samples.Length <= (int)(SinglePassSeconds * sampleRate))
        {
            return [new Chunk(0, samples.Length)];
        }

        var maxLength = (int)(MaxChunkSeconds * sampleRate);
        var search = (int)(SearchSeconds * sampleRate);
        var frame = Math.Max(1, (int)(FrameSeconds * sampleRate));
        var chunks = new List<Chunk>();
        var start = 0;
        while (samples.Length - start > maxLength)
        {
            var windowEnd = start + maxLength;
            var cut = windowEnd;
            var quietest = double.MaxValue;
            for (var position = windowEnd - search; position + frame <= windowEnd; position += frame)
            {
                double energy = 0;
                var slice = samples.Slice(position, frame);
                foreach (var sample in slice) energy += sample * sample;
                if (energy < quietest)
                {
                    quietest = energy;
                    cut = position + frame / 2;
                }
            }
            chunks.Add(new Chunk(start, cut - start));
            start = cut;
        }
        if (samples.Length > start) chunks.Add(new Chunk(start, samples.Length - start));
        return chunks;
    }
}
