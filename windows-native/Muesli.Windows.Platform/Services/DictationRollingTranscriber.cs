using System.Buffers.Binary;
using System.Diagnostics;
using NAudio.Wave;

namespace Muesli.Windows.Services;

/// <summary>Decodes completed dictation audio while capture continues.</summary>
internal sealed class DictationRollingTranscriber
{
    private const int SampleRate = 16000;
    private const int ChunkSamples = SampleRate * 16;
    private const int EarliestBoundary = SampleRate * 8;
    private const int LatestBoundary = SampleRate * 15;
    private readonly Func<byte[], Task<TranscriptionResult>> _decode;
    private readonly Func<string, Task<TranscriptionResult>> _fallback;
    private readonly object _gate = new();
    private readonly List<float> _pending = [];
    private readonly List<(long StartSample, TranscriptionResult Result)> _completed = [];
    private readonly List<long> _chunkDecodeMs = [];
    private Task _work = Task.CompletedTask;
    private long _nextSample = -1;
    private long _firstSample = -1;
    private long _pendingStart;
    private bool _gap;
    private bool _finished;
    private int _readyChunks;

    public DictationRollingTranscriber(NativeTranscriptionClient client)
        : this(client.TranscribeAsync, path => client.TranscribeFileAsync("Dictation", path)) { }

    internal DictationRollingTranscriber(
        Func<byte[], Task<TranscriptionResult>> decode,
        Func<string, Task<TranscriptionResult>> fallback)
    {
        _decode = decode;
        _fallback = fallback;
    }

    public void Feed(object? sender, LivePcmSamplesEventArgs packet)
    {
        lock (_gate)
        {
            if (_finished || _gap || packet.Channel != LiveTranscriptChannel.Microphone) return;
            if (_nextSample < 0)
            {
                _nextSample = packet.StartSample;
                _firstSample = packet.StartSample;
                _pendingStart = packet.StartSample;
            }
            if (packet.StartSample != _nextSample)
            {
                _gap = true;
                return;
            }
            _nextSample += packet.Samples.LongLength;
            _pending.AddRange(packet.Samples);
            if (_pending.Count < ChunkSamples) return;

            // Split at the quietest 200 ms window near the end of a chunk. For uninterrupted
            // speech the lowest-energy window is still used; every sample belongs to one chunk.
            var boundary = QuietBoundary(_pending);
            var chunk = _pending.GetRange(0, boundary).ToArray();
            var start = _pendingStart;
            _pending.RemoveRange(0, boundary);
            _pendingStart += boundary;
            var previous = _work;
            _work = Task.Run(() => DecodeAfterAsync(previous, start, chunk));
        }
    }

    public async Task<TranscriptionResult> FinishAsync(string fullAudioPath)
    {
        float[] tail;
        long tailStart;
        Task work;
        bool gap;
        int readyAtRelease;
        lock (_gate)
        {
            _finished = true;
            tail = _pending.ToArray();
            tailStart = _pendingStart;
            work = _work;
            gap = _gap;
            readyAtRelease = Volatile.Read(ref _readyChunks);
        }

        try
        {
            var queuedWait = Stopwatch.StartNew();
            await work.ConfigureAwait(false);
            queuedWait.Stop();
            if (gap) return await FallbackAsync(fullAudioPath, "packet-gap").ConfigureAwait(false);
            if (_completed.Count == 0) return await FallbackAsync(fullAudioPath, "short-recording").ConfigureAwait(false);
            if (!MatchesRecording(fullAudioPath)) return await FallbackAsync(fullAudioPath, "sample-count-mismatch").ConfigureAwait(false);

            var tailDecode = Stopwatch.StartNew();
            if (tail.Length > 0)
                _completed.Add((tailStart, await _decode(ToWav(tail)).ConfigureAwait(false)));
            tailDecode.Stop();

            var origin = _completed[0].StartSample;
            var backend = _completed[0].Result.Diagnostic?.Split('\n')
                .FirstOrDefault(line => line.StartsWith("Backend: ", StringComparison.Ordinal))?.Trim() ?? "Backend: unknown";
            var text = string.Join(" ", _completed.Select(part => part.Result.Text?.Trim()).Where(part => !string.IsNullOrWhiteSpace(part)));
            var segments = _completed.SelectMany((part, index) =>
            {
                var offsetMs = (int)Math.Round((part.StartSample - origin) * 1000.0 / SampleRate);
                return (part.Result.Segments ?? []).Select(segment => segment with
                {
                    Id = $"dictation_{index}_{segment.Id}",
                    StartMs = segment.StartMs + offsetMs,
                    EndMs = segment.EndMs + offsetMs
                });
            }).ToList();
            return new TranscriptionResult(text,
                $"ASR during capture: {_completed.Count - 1} chunks; readyAtRelease={readyAtRelease}; queuedWaitMs={queuedWait.ElapsedMilliseconds}; tailDecodeMs={tailDecode.ElapsedMilliseconds}; chunkDecodeMs={string.Join(',', _chunkDecodeMs)}; {backend}; finalTailSamples={tail.Length}",
                (int)Math.Round((_nextSample - origin) * 1000.0 / SampleRate), segments);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The durable WAV is authoritative if a callback or speculative decode failed.
            return await FallbackAsync(fullAudioPath, "early-decode-failed").ConfigureAwait(false);
        }
    }

    private async Task<TranscriptionResult> FallbackAsync(string path, string reason)
    {
        var result = await _fallback(path).ConfigureAwait(false);
        return result with { Diagnostic = $"Rolling fallback: {reason}{Environment.NewLine}{result.Diagnostic}" };
    }

    public async Task DrainAsync()
    {
        Task work;
        lock (_gate)
        {
            _finished = true;
            work = _work;
        }
        try { await work.ConfigureAwait(false); }
        catch { /* A discarded capture has no transcript to recover. */ }
    }

    private async Task DecodeAfterAsync(Task previous, long start, float[] samples)
    {
        await previous.ConfigureAwait(false);
        var decode = Stopwatch.StartNew();
        var result = await _decode(ToWav(samples)).ConfigureAwait(false);
        decode.Stop();
        _completed.Add((start, result));
        _chunkDecodeMs.Add(decode.ElapsedMilliseconds);
        Interlocked.Increment(ref _readyChunks);
    }

    private bool MatchesRecording(string path)
    {
        using var reader = new WaveFileReader(path);
        var observedMs = (_nextSample - _firstSample) * 1000.0 / SampleRate;
        return Math.Abs(reader.TotalTime.TotalMilliseconds - observedMs) <= 50;
    }

    internal static int QuietBoundary(List<float> samples)
    {
        const int window = SampleRate / 5;
        var best = EarliestBoundary;
        var lowestEnergy = double.MaxValue;
        for (var end = EarliestBoundary; end <= LatestBoundary; end += window)
        {
            double energy = 0;
            for (var i = end - window; i < end; i++) energy += samples[i] * samples[i];
            if (energy < lowestEnergy)
            {
                lowestEnergy = energy;
                best = end;
            }
        }
        return best;
    }

    internal static byte[] ToWav(float[] samples)
    {
        using var stream = new MemoryStream();
        using (var writer = new WaveFileWriter(stream, new WaveFormat(SampleRate, 16, 1)))
        {
            var pcm = new byte[samples.Length * sizeof(short)];
            for (var i = 0; i < samples.Length; i++)
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2),
                    (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue));
            writer.Write(pcm, 0, pcm.Length);
        }
        return stream.ToArray();
    }
}
