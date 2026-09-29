using System.Buffers.Binary;
using NAudio.Wave;

namespace Muesli.Windows.Services;

public sealed class StreamingPcmNormalizer
{
    private readonly object _gate = new();
    private WaveFormat? _format;
    private long _inputFrames;
    private long _outputSamples;
    private double _nextSourceFrame;
    private float _previous;
    private bool _hasPrevious;

    public (float[] Samples, long StartSample) Convert(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        lock (_gate)
        {
            if (_format is null || !SameFormat(_format, format)) Reset(format);
            var mono = DecodeMono(buffer, bytesRecorded, format);
            if (mono.Length == 0) return ([], _outputSamples);

            var output = new List<float>((int)Math.Ceiling(mono.Length * 16000.0 / format.SampleRate) + 2);
            var bufferStart = _inputFrames;
            var bufferEnd = bufferStart + mono.Length;
            var step = format.SampleRate / 16000.0;
            while (_nextSourceFrame < bufferEnd)
            {
                var floor = (long)Math.Floor(_nextSourceFrame);
                var fraction = (float)(_nextSourceFrame - floor);
                var left = SampleAt(floor, bufferStart, mono);
                var right = SampleAt(floor + 1, bufferStart, mono);
                output.Add(Math.Clamp(left + ((right - left) * fraction), -1f, 1f));
                _nextSourceFrame += step;
            }

            _inputFrames = bufferEnd;
            _previous = mono[^1];
            _hasPrevious = true;
            var start = _outputSamples;
            _outputSamples += output.Count;
            return (output.ToArray(), start);
        }
    }

    private float SampleAt(long absolute, long bufferStart, float[] mono)
    {
        var index = absolute - bufferStart;
        if (index < 0) return _hasPrevious ? _previous : mono[0];
        if (index >= mono.Length) return mono[^1];
        return mono[index];
    }

    private void Reset(WaveFormat format)
    {
        var preserveOutputOffset = _format is not null;
        _format = format;
        _inputFrames = 0;
        if (!preserveOutputOffset) _outputSamples = 0;
        _nextSourceFrame = 0;
        _previous = 0;
        _hasPrevious = false;
    }

    private static bool SameFormat(WaveFormat first, WaveFormat second) =>
        first.SampleRate == second.SampleRate && first.Channels == second.Channels &&
        first.BitsPerSample == second.BitsPerSample && first.Encoding == second.Encoding;

    private static float[] DecodeMono(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        var bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
        var bytesPerFrame = bytesPerSample * format.Channels;
        var frames = Math.Max(0, bytesRecorded / bytesPerFrame);
        var mono = new float[frames];
        var float32 = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                      (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32);
        for (var frame = 0; frame < frames; frame++)
        {
            double total = 0;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var offset = (frame * bytesPerFrame) + (channel * bytesPerSample);
                total += float32 ? BitConverter.ToSingle(buffer, offset) : format.BitsPerSample switch
                {
                    16 => BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset, 2)) / 32768f,
                    24 => ReadInt24(buffer, offset) / 8388608f,
                    32 => BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset, 4)) / 2147483648f,
                    _ => throw new NotSupportedException($"Live PCM conversion does not support {format.BitsPerSample}-bit {format.Encoding} audio.")
                };
            }
            mono[frame] = (float)Math.Clamp(total / format.Channels, -1, 1);
        }
        return mono;
    }

    private static int ReadInt24(byte[] buffer, int offset)
    {
        var value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
        return (value & 0x800000) == 0 ? value : value | unchecked((int)0xFF000000);
    }
}
