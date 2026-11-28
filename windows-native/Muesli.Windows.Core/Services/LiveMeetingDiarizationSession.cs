using System.Diagnostics;
using System.Threading.Channels;
using NAudio.Wave;
using SherpaOnnx;

namespace Muesli.Windows.Services;

/// <summary>
/// Diarizes the meeting system track in fixed blocks while it is being recorded, so Stop only has
/// the unprocessed tail left. Each block is diarized on its own, then its local speakers are linked
/// to meeting-wide speakers by comparing speaker embeddings (<see cref="LiveSpeakerLinker"/>).
/// The result is used only when the fed stream matches the final system track; otherwise the
/// pipeline falls back to diarizing the whole file.
/// </summary>
public sealed class LiveMeetingDiarizationSession : IDisposable
{
    public const int SampleRate = 16000;
    internal const int DefaultBlockSeconds = 30;

    /// <summary>A fed stream that differs from the final track by more than this is not trusted.</summary>
    internal const int MaxTrackLengthDriftSamples = SampleRate / 2;

    /// <summary>A tail shorter than this carries no usable speaker turn and is not diarized.</summary>
    internal const int MinTailSamples = SampleRate;

    /// <summary>Audio per local speaker fed to the embedding model; longer turns add little.</summary>
    private const int MaxEmbeddingSamples = 20 * SampleRate;

    private const int EmbeddingPieceSamples = 5 * SampleRate;

    private const int MinEmbeddingSamples = SampleRate / 4;

    private readonly NativeDiarizationClient _client;
    private readonly int _blockSamples;
    private readonly Channel<Block> _blocks = Channel.CreateUnbounded<Block>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly object _feedGate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly LiveSpeakerLinker _linker = new();
    private readonly List<DiarizedSegment> _segments = [];
    private readonly Task _worker;
    private float[] _pending;
    private int _pendingCount;
    private long _fedSamples;
    private long _queuedSamples;
    private bool _accepting = true;
    private SpeakerEmbeddingExtractor? _extractor;
    private Exception? _failure;
    private int _blocksProcessed;
    private long _blockProcessingMs;
    private int _disposed;

    private sealed record Block(float[] Samples, long OffsetSamples);

    public LiveMeetingDiarizationSession(NativeDiarizationClient client, int blockSeconds = DefaultBlockSeconds)
    {
        _client = client;
        _blockSamples = Math.Max(SampleRate, blockSeconds * SampleRate);
        _pending = new float[_blockSamples];
        _worker = Task.Run(ProcessBlocksAsync);
    }

    /// <summary>Completes once the background worker has stopped.</summary>
    public Task Completion => _worker;

    public long FedSamples
    {
        get
        {
            lock (_feedGate)
            {
                return _fedSamples;
            }
        }
    }

    /// <summary>Appends 16 kHz mono system-track samples in capture order.</summary>
    public void Feed(float[] samples)
    {
        if (samples.Length == 0)
        {
            return;
        }

        lock (_feedGate)
        {
            if (!_accepting)
            {
                return;
            }

            var offset = 0;
            while (offset < samples.Length)
            {
                var count = Math.Min(samples.Length - offset, _blockSamples - _pendingCount);
                Array.Copy(samples, offset, _pending, _pendingCount, count);
                _pendingCount += count;
                offset += count;
                if (_pendingCount == _blockSamples)
                {
                    QueuePendingUnderLock();
                }
            }

            _fedSamples += samples.Length;
        }
    }

    /// <summary>
    /// Stops feeding, diarizes the remaining tail and returns meeting-wide segments, or a
    /// fallback reason when the live result cannot stand in for the final system track.
    /// </summary>
    public async Task<LiveDiarizationCompletion> CompleteAsync(string systemTrackPath, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        lock (_feedGate)
        {
            if (_accepting)
            {
                _accepting = false;
                if (_pendingCount >= MinTailSamples)
                {
                    QueuePendingUnderLock();
                }
                _blocks.Writer.TryComplete();
            }
        }

        await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        var fed = FedSamples;
        if (_failure is not null)
        {
            return Fallback($"block-failed-{_failure.GetType().Name}", fed, null);
        }

        var trackSamples = MeasureTrackSamples(systemTrackPath, out var formatProblem);
        if (formatProblem is not null)
        {
            return Fallback(formatProblem, fed, null);
        }
        if (!StreamMatchesTrack(fed, trackSamples))
        {
            return Fallback("length-mismatch", fed, trackSamples);
        }

        List<DiarizedSegment> segments;
        lock (_segments)
        {
            segments = MergeAdjacent(_segments);
        }
        stopwatch.Stop();
        var result = new DiarizationResult(
            "",
            "en",
            (int)(trackSamples * 1000 / SampleRate),
            segments,
            [
                "ASR engine: native-sherpa-onnx-diarization",
                $"Diarization segments: {segments.Count}",
                $"Diarization provider: {_client.Provider}",
                "Diarization mode: live blocks",
                $"Diarization processing ms: {stopwatch.ElapsedMilliseconds}"
            ])
        {
            Provider = _client.Provider,
            ModelLoadMs = _client.InitialModelLoadMs,
            ModelReused = true,
            ProcessingMs = stopwatch.ElapsedMilliseconds
        };
        return new LiveDiarizationCompletion(result, null, fed, trackSamples, _blocksProcessed, Interlocked.Read(ref _blockProcessingMs));
    }

    internal static bool StreamMatchesTrack(long fedSamples, long trackSamples) =>
        fedSamples > 0 && Math.Abs(fedSamples - trackSamples) <= MaxTrackLengthDriftSamples;

    /// <summary>Joins consecutive turns of one speaker that a block boundary or short pause split.</summary>
    internal static List<DiarizedSegment> MergeAdjacent(IEnumerable<DiarizedSegment> segments, int maxGapMs = 500)
    {
        var merged = new List<DiarizedSegment>();
        foreach (var segment in segments.OrderBy(segment => segment.StartMs).ThenBy(segment => segment.EndMs))
        {
            if (merged.Count > 0 &&
                merged[^1].SpeakerId == segment.SpeakerId &&
                segment.StartMs - merged[^1].EndMs <= maxGapMs)
            {
                merged[^1] = merged[^1] with { EndMs = Math.Max(merged[^1].EndMs, segment.EndMs) };
                continue;
            }
            merged.Add(segment);
        }
        return merged;
    }

    private LiveDiarizationCompletion Fallback(string reason, long fed, long? track) =>
        new(null, reason, fed, track ?? -1, _blocksProcessed, Interlocked.Read(ref _blockProcessingMs));

    private void QueuePendingUnderLock()
    {
        var samples = _pendingCount == _pending.Length ? _pending : _pending[.._pendingCount];
        _blocks.Writer.TryWrite(new Block(samples, _queuedSamples));
        _queuedSamples += _pendingCount;
        _pending = new float[_blockSamples];
        _pendingCount = 0;
    }

    private async Task ProcessBlocksAsync()
    {
        try
        {
            await foreach (var block in _blocks.Reader.ReadAllAsync(_cancellation.Token).ConfigureAwait(false))
            {
                if (_failure is not null)
                {
                    continue;
                }
                try
                {
                    await ProcessBlockAsync(block).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _failure = exception;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ProcessBlockAsync(Block block)
    {
        var stopwatch = Stopwatch.StartNew();
        var local = await _client.DiarizeSamplesAsync(block.Samples).ConfigureAwait(false);
        _cancellation.Token.ThrowIfCancellationRequested();
        var blockSegments = new List<DiarizedSegment>();
        foreach (var speaker in local.GroupBy(segment => segment.Speaker))
        {
            var audio = CollectSpeakerAudio(block.Samples, speaker);
            var globalSpeaker = audio.Length >= MinEmbeddingSamples
                ? _linker.Assign(ComputeEmbedding(audio), audio.Length / (double)SampleRate)
                : _linker.AddUnmatched();
            foreach (var segment in speaker)
            {
                blockSegments.Add(new DiarizedSegment(
                    $"speaker_{globalSpeaker}",
                    (int)((block.OffsetSamples * 1000 / SampleRate) + segment.Start * 1000),
                    (int)((block.OffsetSamples * 1000 / SampleRate) + segment.End * 1000)));
            }
        }

        lock (_segments)
        {
            _segments.AddRange(blockSegments);
        }
        stopwatch.Stop();
        Interlocked.Increment(ref _blocksProcessed);
        Interlocked.Add(ref _blockProcessingMs, stopwatch.ElapsedMilliseconds);
    }

    private static float[] CollectSpeakerAudio(float[] block, IEnumerable<NativeDiarizationClient.LocalSpeakerSegment> turns)
    {
        var audio = new List<float>();
        foreach (var turn in turns.OrderByDescending(turn => turn.End - turn.Start))
        {
            var start = Math.Clamp((int)(turn.Start * SampleRate), 0, block.Length);
            var end = Math.Clamp((int)(turn.End * SampleRate), start, block.Length);
            audio.AddRange(new ArraySegment<float>(block, start, end - start));
            if (audio.Count >= MaxEmbeddingSamples)
            {
                break;
            }
        }
        return audio.Count > MaxEmbeddingSamples ? audio.GetRange(0, MaxEmbeddingSamples).ToArray() : audio.ToArray();
    }

    private float[] ComputeEmbedding(float[] audio)
    {
        _extractor ??= new SpeakerEmbeddingExtractor(new SpeakerEmbeddingExtractorConfig
        {
            Model = NativeDiarizationClient.EmbeddingModelPath,
            NumThreads = NativeDiarizationClient.CpuThreadCount,
            Provider = "cpu"
        });
        // Titanet's working memory grows with input length (about +340 MB for 20 s), so embed
        // short pieces and take their duration-weighted mean; the speaker links come out the same.
        var sum = new float[_extractor.Dim];
        for (var offset = 0; offset < audio.Length; offset += EmbeddingPieceSamples)
        {
            var count = Math.Min(EmbeddingPieceSamples, audio.Length - offset);
            if (count < MinEmbeddingSamples && offset > 0)
            {
                break;
            }
            using var stream = _extractor.CreateStream();
            stream.AcceptWaveform(SampleRate, audio.AsSpan(offset, count).ToArray());
            stream.InputFinished();
            var embedding = _extractor.Compute(stream);
            var norm = MathF.Sqrt(embedding.Sum(value => value * value));
            if (norm <= 0)
            {
                continue;
            }
            for (var index = 0; index < sum.Length && index < embedding.Length; index++)
            {
                sum[index] += embedding[index] / norm * count;
            }
        }
        return sum;
    }

    private static long MeasureTrackSamples(string path, out string? problem)
    {
        try
        {
            using var reader = new WaveFileReader(path);
            if (reader.WaveFormat.SampleRate != SampleRate || reader.WaveFormat.Channels != 1)
            {
                problem = "track-format";
                return -1;
            }
            problem = null;
            return reader.SampleCount;
        }
        catch (Exception exception)
        {
            problem = $"track-unreadable-{exception.GetType().Name}";
            return -1;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_feedGate)
        {
            _accepting = false;
            _pending = [];
            _pendingCount = 0;
        }
        _blocks.Writer.TryComplete();
        _cancellation.Cancel();
        // The native extractor may be mid-call on the worker; release it once the worker stops.
        _ = _worker.ContinueWith(
            _ =>
            {
                _extractor?.Dispose();
                _extractor = null;
                _cancellation.Dispose();
            },
            TaskScheduler.Default);
    }
}

public sealed record LiveDiarizationCompletion(
    DiarizationResult? Result,
    string? FallbackReason,
    long FedSamples,
    long TrackSamples,
    int BlocksProcessed,
    long BlockProcessingMs);

/// <summary>
/// Links block-local speakers to meeting-wide speakers: each local speaker joins the most similar
/// known speaker when the cosine similarity of their embeddings clears the threshold, otherwise it
/// becomes a new speaker. Known speakers keep a duration-weighted mean embedding.
/// </summary>
public sealed class LiveSpeakerLinker
{
    /// <summary>Matches the cosine-distance threshold the whole-file clustering uses.</summary>
    public const float DefaultThreshold = 0.5f;

    private const double MaxWeightSeconds = 20;

    private readonly float _threshold;
    private readonly List<float[]?> _centroids = [];

    public LiveSpeakerLinker(float threshold = DefaultThreshold)
    {
        _threshold = threshold;
    }

    public int SpeakerCount => _centroids.Count;

    public int Assign(float[] embedding, double seconds)
    {
        var normalized = Normalize(embedding);
        var best = -1;
        var bestSimilarity = float.MinValue;
        for (var index = 0; index < _centroids.Count; index++)
        {
            var centroid = _centroids[index];
            if (centroid is null)
            {
                continue;
            }
            var similarity = Dot(normalized, Normalize(centroid));
            if (similarity > bestSimilarity)
            {
                bestSimilarity = similarity;
                best = index;
            }
        }

        var weight = (float)Math.Clamp(seconds, 0.1, MaxWeightSeconds);
        if (best >= 0 && bestSimilarity >= _threshold)
        {
            var centroid = _centroids[best]!;
            for (var index = 0; index < centroid.Length; index++)
            {
                centroid[index] += normalized[index] * weight;
            }
            return best;
        }

        _centroids.Add(normalized.Select(value => value * weight).ToArray());
        return _centroids.Count - 1;
    }

    /// <summary>A speaker with too little audio to embed; kept separate like the whole-file path does.</summary>
    public int AddUnmatched()
    {
        _centroids.Add(null);
        return _centroids.Count - 1;
    }

    private static float[] Normalize(float[] vector)
    {
        var norm = MathF.Sqrt(vector.Sum(value => value * value));
        return norm <= 0 ? vector.ToArray() : vector.Select(value => value / norm).ToArray();
    }

    private static float Dot(float[] left, float[] right)
    {
        var sum = 0f;
        var length = Math.Min(left.Length, right.Length);
        for (var index = 0; index < length; index++)
        {
            sum += left[index] * right[index];
        }
        return sum;
    }
}
