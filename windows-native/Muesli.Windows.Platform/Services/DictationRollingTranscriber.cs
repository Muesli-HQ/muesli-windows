using System.Buffers.Binary;
using System.Diagnostics;
using NAudio.Wave;

namespace Muesli.Windows.Services;

/// <summary>Decodes completed dictation audio while capture continues.</summary>
internal sealed class DictationRollingTranscriber
{
    private const int SampleRate = 16000;
    // Chunks are decoded independently and their texts joined with a space, so a cut in the
    // middle of a sentence leaves a stray full stop and capital. Cuts are therefore made only
    // where the speech detector heard a real pause, and never closer together than this.
    internal const int MinChunkSamples = SampleRate * 5;
    // Without a pause the chunk is cut here regardless. Audio left at release is decoded while
    // the user waits, so this also caps that wait: about 4 s at the RTF ~0.14 measured for
    // Parakeet v3 on a laptop CPU.
    internal const int MaxChunkSamples = SampleRate * 30;
    // Speech detection runs off the capture thread, on this much new audio at a time.
    private const int ScanSamples = SampleRate / 4;
    // The model can return nothing for a chunk that starts or ends in the middle of a phrase,
    // which a forced cut can cause. A chunk with at least this much detected speech and no text
    // means words were lost, so the whole recording is decoded again in one pass.
    internal const int SpeechInEmptyChunkSamples = SampleRate;
    // Used by the meeting preview, which still cuts on a clock.
    private const int EarliestBoundary = SampleRate * 3;
    private const int LatestBoundary = SampleRate * 11 / 2;
    private readonly Func<byte[], Task<TranscriptionResult>> _decode;
    private readonly Func<string, Task<TranscriptionResult>> _fallback;
    private readonly ILiveVad? _vad;
    private readonly List<float> _unscanned = [];
    private readonly List<int> _chunkAudioMs = [];
    private Task _scan = Task.CompletedTask;
    private long _scanned;
    private int _pauseCuts;
    private int _forcedCuts;
    private readonly object _gate = new();
    private readonly List<float> _pending = [];
    private readonly List<(long StartSample, int Length, TranscriptionResult Result)> _completed = [];
    // Stretches of speech the detector reported, as offsets from the first captured sample.
    private readonly List<(long Start, long End)> _speech = [];
    private bool _detectorFailed;
    private readonly List<long> _chunkDecodeMs = [];
    private Task _work = Task.CompletedTask;
    private long _nextSample = -1;
    private long _firstSample = -1;
    private long _pendingStart;
    private bool _gap;
    private bool _finished;
    private int _readyChunks;

    /// <param name="vad">
    /// Shared speech detector, or null when it is unavailable; chunks are then cut only at
    /// <see cref="MaxChunkSamples"/>. The caller owns it and must not use it for another
    /// session until this one has finished or drained.
    /// </param>
    public DictationRollingTranscriber(NativeTranscriptionClient client, ILiveVad? vad = null)
        : this(client.TranscribeAsync, path => client.TranscribeFileAsync("Dictation", path), vad) { }

    internal DictationRollingTranscriber(
        Func<byte[], Task<TranscriptionResult>> decode,
        Func<string, Task<TranscriptionResult>> fallback,
        ILiveVad? vad = null)
    {
        _decode = decode;
        _fallback = fallback;
        _vad = vad;
        _vad?.Reset(LiveTranscriptChannel.Microphone);
    }

    public bool CutsAtPauses => _vad is not null;

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
            while (_pending.Count >= MaxChunkSamples)
            {
                Cut(MaxChunkSamples);
                _forcedCuts++;
            }
            if (_vad is null) return;

            _unscanned.AddRange(packet.Samples);
            if (_unscanned.Count < ScanSamples) return;
            var slice = _unscanned.ToArray();
            _unscanned.Clear();
            var previousScan = _scan;
            _scan = Task.Run(() => ScanAfterAsync(previousScan, slice));
        }
    }

    // Every sample belongs to exactly one chunk; chunks never overlap.
    private void Cut(int boundary)
    {
        var chunk = _pending.GetRange(0, boundary).ToArray();
        var start = _pendingStart;
        _pending.RemoveRange(0, boundary);
        _pendingStart += boundary;
        _chunkAudioMs.Add(boundary * 1000 / SampleRate);
        var previous = _work;
        _work = Task.Run(() => DecodeAfterAsync(previous, start, chunk));
    }

    private async Task ScanAfterAsync(Task previous, float[] slice)
    {
        await previous.ConfigureAwait(false);
        IReadOnlyList<(long StartSample, long EndSample)> speech;
        try
        {
            speech = _vad!.Feed(LiveTranscriptChannel.Microphone, slice);
        }
        catch
        {
            // A detector failure only loses pause cuts; the 30 s limit still applies.
            lock (_gate) _detectorFailed = true;
            return;
        }
        lock (_gate)
        {
            _scanned += slice.Length;
            _speech.AddRange(speech);
            // After release the rest is decoded as one tail; another cut would only add a seam.
            if (_finished) return;
            foreach (var (_, speechEnd) in speech)
            {
                // The detector reports a stretch of speech once it has heard the silence that
                // follows it. Cut in the middle of the silence heard so far.
                var cutAt = speechEnd + Math.Max(0, _scanned - speechEnd) / 2;
                var boundary = cutAt - (_pendingStart - _firstSample);
                if (boundary < MinChunkSamples || boundary > _pending.Count) continue;
                Cut((int)boundary);
                _pauseCuts++;
            }
        }
    }

    public async Task<TranscriptionResult> FinishAsync(string fullAudioPath)
    {
        float[] tail;
        long tailStart;
        Task work;
        Task scan;
        bool gap;
        int readyAtRelease;
        lock (_gate)
        {
            _finished = true;
            scan = _scan;
        }
        await scan.ConfigureAwait(false);
        FlushDetector();
        lock (_gate)
        {
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
                _completed.Add((tailStart, tail.Length, await _decode(ToWav(tail)).ConfigureAwait(false)));
            tailDecode.Stop();
            if (AnEmptyChunkHadSpeech()) return await FallbackAsync(fullAudioPath, "empty-chunk").ConfigureAwait(false);

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
                $"ASR during capture: {_completed.Count - 1} chunks; readyAtRelease={readyAtRelease}; queuedWaitMs={queuedWait.ElapsedMilliseconds}; tailDecodeMs={tailDecode.ElapsedMilliseconds}; chunkDecodeMs={string.Join(',', _chunkDecodeMs)}; chunkAudioMs={string.Join(',', _chunkAudioMs)}; pauseCuts={_pauseCuts}; forcedCuts={_forcedCuts}; cuts={(_vad is null ? "limit-only" : "pauses")}; {backend}; finalTailSamples={tail.Length}",
                (int)Math.Round((_nextSample - origin) * 1000.0 / SampleRate), segments);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The durable WAV is authoritative if a callback or speculative decode failed.
            return await FallbackAsync(fullAudioPath, "early-decode-failed").ConfigureAwait(false);
        }
    }

    // The detector only reports a stretch of speech after the silence that follows it, so the
    // last stretch, and the audio not yet scanned, are collected here.
    private void FlushDetector()
    {
        if (_vad is null) return;
        float[] rest;
        lock (_gate)
        {
            rest = _unscanned.ToArray();
            _unscanned.Clear();
        }
        try
        {
            var speech = new List<(long, long)>();
            if (rest.Length > 0) speech.AddRange(_vad.Feed(LiveTranscriptChannel.Microphone, rest));
            speech.AddRange(_vad.Finish(LiveTranscriptChannel.Microphone));
            lock (_gate) _speech.AddRange(speech);
        }
        catch
        {
            lock (_gate) _detectorFailed = true;
        }
    }

    private bool AnEmptyChunkHadSpeech()
    {
        var speechKnown = _vad is not null && !_detectorFailed;
        foreach (var (start, length, result) in _completed)
        {
            if (!string.IsNullOrWhiteSpace(result.Text)) continue;
            // Without the detector there is no evidence either way; a full-length chunk with
            // no text at all is treated as lost rather than assumed silent.
            if (!speechKnown)
            {
                if (length >= MinChunkSamples) return true;
                continue;
            }
            var chunkStart = start - _firstSample;
            var chunkEnd = chunkStart + length;
            long heard = 0;
            foreach (var (speechStart, speechEnd) in _speech)
                heard += Math.Max(0, Math.Min(speechEnd, chunkEnd) - Math.Max(speechStart, chunkStart));
            if (heard >= SpeechInEmptyChunkSamples) return true;
        }
        return false;
    }

    private async Task<TranscriptionResult> FallbackAsync(string path, string reason)
    {
        var result = await _fallback(path).ConfigureAwait(false);
        return result with { Diagnostic = $"Rolling fallback: {reason}{Environment.NewLine}{result.Diagnostic}" };
    }

    public async Task DrainAsync()
    {
        Task work;
        Task scan;
        lock (_gate)
        {
            _finished = true;
            scan = _scan;
        }
        await scan.ConfigureAwait(false);
        lock (_gate) work = _work;
        try { await work.ConfigureAwait(false); }
        catch { /* A discarded capture has no transcript to recover. */ }
    }

    private async Task DecodeAfterAsync(Task previous, long start, float[] samples)
    {
        await previous.ConfigureAwait(false);
        var decode = Stopwatch.StartNew();
        var result = await _decode(ToWav(samples)).ConfigureAwait(false);
        decode.Stop();
        _completed.Add((start, samples.Length, result));
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
