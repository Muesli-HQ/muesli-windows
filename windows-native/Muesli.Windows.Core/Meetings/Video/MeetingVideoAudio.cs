namespace Muesli.Windows.Services;

/// <summary>macOS MeetingVideoAudioSpans (2f10b23d): maps delayed AEC output back to raw microphone
/// capture positions on the 16 kHz host clock.</summary>
public sealed class MeetingVideoAudioSpans
{
    private readonly List<(long Start, int Count)> _spans = [];

    public void Append(long start, int count) => _spans.Add((start, count));

    public IReadOnlyList<(long Start, short[] Samples)> Consume(ReadOnlySpan<short> samples)
    {
        var result = new List<(long, short[])>();
        var offset = 0;
        while (offset < samples.Length && _spans.Count > 0)
        {
            var (start, available) = _spans[0];
            var count = Math.Min(available, samples.Length - offset);
            result.Add((start, samples.Slice(offset, count).ToArray()));
            offset += count;
            if (available == count) _spans.RemoveAt(0);
            else _spans[0] = (start + count, available - count);
        }
        return result;
    }
}

/// <summary>
/// macOS MeetingVideoAudioBuffer: a bounded timestamped mix. Late samples are dropped at their original
/// position, never shifted into a later slot. A single source keeps its full amplitude.
/// </summary>
public sealed class MeetingVideoAudioBuffer(long start)
{
    private const int Window = 160_000;
    private readonly Dictionary<long, short> _mic = [];
    private readonly Dictionary<long, short> _system = [];

    public long Cursor { get; private set; } = start;

    public void Append(ReadOnlySpan<short> samples, long start, bool microphone)
    {
        var target = microphone ? _mic : _system;
        for (var offset = 0; offset < samples.Length; offset++)
        {
            var position = start + offset;
            if (position < Cursor || position >= Cursor + Window) continue;
            target[position] = samples[offset];
        }
    }

    public short[] Take(int count)
    {
        var values = new short[count];
        for (var offset = 0; offset < count; offset++)
        {
            var position = Cursor + offset;
            var mic = _mic.Remove(position, out var a) ? a : 0;
            var system = _system.Remove(position, out var b) ? b : 0;
            values[offset] = (short)Math.Clamp(mic + system, short.MinValue, short.MaxValue);
        }
        Cursor += count;
        return values;
    }
}
