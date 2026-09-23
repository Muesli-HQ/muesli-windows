using System.IO;
using NAudio.Wave;

namespace Muesli.Windows.Services;

internal sealed class SegmentedWaveCaptureWriter : IDisposable
{
    public const long DefaultMaxSegmentBytes = 512L * 1024 * 1024;

    private readonly object _sync = new();
    private readonly string _directory;
    private readonly string _prefix;
    private readonly WaveFormat _format;
    private readonly long _maxSegmentBytes;
    private readonly List<string> _paths = [];
    private WaveFileWriter? _writer;
    private long _segmentDataBytes;

    public SegmentedWaveCaptureWriter(
        string directory,
        string prefix,
        WaveFormat format,
        long maxSegmentBytes = DefaultMaxSegmentBytes)
    {
        _directory = directory;
        _prefix = prefix;
        _format = format;
        _maxSegmentBytes = Math.Max(format.BlockAlign, maxSegmentBytes);
        Directory.CreateDirectory(directory);
        OpenNextSegment();
    }

    public IReadOnlyList<string> Paths
    {
        get
        {
            lock (_sync)
            {
                return _paths.ToArray();
            }
        }
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        lock (_sync)
        {
            while (count > 0)
            {
                if (_writer is null)
                {
                    return;
                }

                var available = _maxSegmentBytes - _segmentDataBytes;
                if (available < _format.BlockAlign)
                {
                    OpenNextSegment();
                    continue;
                }

                var writeCount = (int)Math.Min(count, available);
                writeCount -= writeCount % _format.BlockAlign;
                if (writeCount <= 0)
                {
                    OpenNextSegment();
                    continue;
                }

                _writer.Write(buffer, offset, writeCount);
                _segmentDataBytes += writeCount;
                offset += writeCount;
                count -= writeCount;
            }
        }
    }

    public IReadOnlyList<string> Complete()
    {
        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
            return _paths.ToArray();
        }
    }

    public void Dispose() => Complete();

    private void OpenNextSegment()
    {
        _writer?.Dispose();
        var suffix = _paths.Count == 0 ? "" : $"-part{_paths.Count + 1:D3}";
        var path = Path.Combine(_directory, $"{_prefix}{suffix}.wav");
        _writer = new WaveFileWriter(path, _format);
        _paths.Add(path);
        _segmentDataBytes = 0;
    }
}

internal static class WaveFileUtilities
{
    public static void NormalizeToWhisperWav(
        IReadOnlyList<string> sourcePaths,
        string destinationPath)
        => NormalizeToWhisperWav([new TimedWaveSource(sourcePaths, 0)], destinationPath);

    public static void NormalizeToWhisperWav(
        IReadOnlyList<TimedWaveSource> sourceLegs,
        string destinationPath)
    {
        if (sourceLegs.Count == 0 || sourceLegs.All(leg => leg.Paths.Count == 0))
        {
            throw new InvalidOperationException("Audio capture produced no WAV segments.");
        }

        var outputFormat = new WaveFormat(16000, 16, 1);
        using var output = new WaveFileWriter(destinationPath, outputFormat);
        var buffer = new byte[64 * 1024];
        var silence = new byte[64 * 1024];
        long writtenDataBytes = 0;

        foreach (var leg in sourceLegs.OrderBy(leg => leg.StartOffsetMs))
        {
            var desiredStartBytes = Math.Max(0L,
                (long)leg.StartOffsetMs * outputFormat.AverageBytesPerSecond / 1000);
            desiredStartBytes -= desiredStartBytes % outputFormat.BlockAlign;
            while (writtenDataBytes < desiredStartBytes)
            {
                var count = (int)Math.Min(silence.Length, desiredStartBytes - writtenDataBytes);
                count -= count % outputFormat.BlockAlign;
                if (count <= 0)
                {
                    break;
                }
                output.Write(silence, 0, count);
                writtenDataBytes += count;
            }

            foreach (var sourcePath in leg.Paths)
            {
                using var reader = new AudioFileReader(sourcePath);
                ISampleProvider mono = reader.WaveFormat.Channels switch
                {
                    1 => reader,
                    2 => reader.ToMono(),
                    _ => new DownmixToMonoSampleProvider(reader),
                };
                using var resampler = new MediaFoundationResampler(mono.ToWaveProvider16(), outputFormat)
                {
                    ResamplerQuality = 60
                };

                int read;
                while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    writtenDataBytes += read;
                }
            }
        }
    }

    public static TimeSpan GetDuration(string path)
    {
        using var reader = new WaveFileReader(path);
        return reader.TotalTime;
    }

    public static IReadOnlyList<WaveChunk> CreateChunks(
        string sourcePath,
        string outputDirectory,
        TimeSpan chunkDuration)
    {
        Directory.CreateDirectory(outputDirectory);
        using var reader = new WaveFileReader(sourcePath);
        var bytesPerChunk = (long)(reader.WaveFormat.AverageBytesPerSecond * chunkDuration.TotalSeconds);
        bytesPerChunk -= bytesPerChunk % reader.WaveFormat.BlockAlign;
        bytesPerChunk = Math.Max(reader.WaveFormat.BlockAlign, bytesPerChunk);

        var chunks = new List<WaveChunk>();
        var buffer = new byte[64 * 1024];
        long totalBytesRead = 0;
        var index = 0;
        while (reader.Position < reader.Length)
        {
            var chunkPath = Path.Combine(outputDirectory, $"chunk-{++index:D4}.wav");
            long chunkBytes = 0;
            using (var writer = new WaveFileWriter(chunkPath, reader.WaveFormat))
            {
                while (chunkBytes < bytesPerChunk)
                {
                    var remaining = bytesPerChunk - chunkBytes;
                    var requested = (int)Math.Min(buffer.Length, remaining);
                    requested -= requested % reader.WaveFormat.BlockAlign;
                    if (requested <= 0)
                    {
                        break;
                    }

                    var read = reader.Read(buffer, 0, requested);
                    if (read <= 0)
                    {
                        break;
                    }

                    writer.Write(buffer, 0, read);
                    chunkBytes += read;
                }
            }

            if (chunkBytes == 0)
            {
                File.Delete(chunkPath);
                break;
            }

            var offsetMs = (int)Math.Min(
                int.MaxValue,
                totalBytesRead * 1000L / reader.WaveFormat.AverageBytesPerSecond);
            chunks.Add(new WaveChunk(chunkPath, offsetMs));
            totalBytesRead += chunkBytes;
        }

        return chunks;
    }

    private sealed class DownmixToMonoSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _sourceBuffer = [];

        public DownmixToMonoSampleProvider(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var requested = count * _channels;
            if (_sourceBuffer.Length < requested)
            {
                _sourceBuffer = new float[requested];
            }

            var read = _source.Read(_sourceBuffer, 0, requested);
            var frames = read / _channels;
            for (var frame = 0; frame < frames; frame++)
            {
                float sum = 0;
                for (var channel = 0; channel < _channels; channel++)
                {
                    sum += _sourceBuffer[frame * _channels + channel];
                }

                buffer[offset + frame] = sum / _channels;
            }

            return frames;
        }
    }
}

internal sealed record TimedWaveSource(
    IReadOnlyList<string> Paths,
    int StartOffsetMs);

internal sealed record WaveChunk(string Path, int OffsetMs);
