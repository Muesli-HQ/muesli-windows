using System.Threading.Channels;

namespace Muesli.Windows.Services;

/// <summary>
/// Transcribes the meeting while it is being recorded, with the selected meeting model. As on
/// macOS, these pieces are the final transcript: Stop only has to finish the last piece. Anything
/// that could not be transcribed in time is recorded as a gap, and only those stretches are
/// transcribed again from the retained audio.
/// </summary>
internal sealed class MeetingRollingTranscriber
{
    private const int SampleRate = 16000;
    private const int ChunkSamples = 16 * SampleRate;

    /// <summary>Pieces waiting for the model; beyond this a piece becomes a gap instead of growing memory.</summary>
    internal const int QueuedChunkLimit = 16;
    private sealed class Buffer
    {
        public List<float> Samples { get; } = [];
        public long Start;
        public long Next = -1;
        public long VadAccepted;
        public long VadOffset;
    }
    private sealed record Chunk(LiveTranscriptChannel Channel, long Start, float[] Samples);
    private readonly Func<byte[], Task<TranscriptionResult>> _decode;
    private readonly ILiveVad? _vad;
    private readonly object _gate = new();
    private readonly Dictionary<LiveTranscriptChannel, Buffer> _buffers = Enum.GetValues<LiveTranscriptChannel>()
        .ToDictionary(channel => channel, _ => new Buffer());
    private readonly Channel<Chunk> _chunks = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(QueuedChunkLimit)
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
    });
    private readonly List<LiveTranscriptSegment> _committed;
    private readonly List<LiveTranscriptGap> _gaps;
    private readonly Task _worker;
    private long _dropped;
    private bool _finished;
    private bool _cancelled;

    public MeetingRollingTranscriber(Func<byte[], Task<TranscriptionResult>> decode,
        IReadOnlyList<LiveTranscriptSegment>? prior = null, ILiveVad? vad = null,
        IReadOnlyList<LiveTranscriptGap>? priorGaps = null)
    {
        _decode = decode;
        _vad = vad;
        _committed = prior?.ToList() ?? [];
        _gaps = priorGaps?.ToList() ?? [];
        _worker = Task.Run(DecodeAsync);
    }

    public event EventHandler<LiveTranscriptSnapshot>? SnapshotChanged;
    public event EventHandler<Exception>? Failed;

    /// <summary>Stretches of audio that were captured but not transcribed here.</summary>
    public IReadOnlyList<LiveTranscriptGap> Gaps
    {
        get { lock (_gate) return _gaps.ToArray(); }
    }

    public long DroppedChunks => Interlocked.Read(ref _dropped);

    public void Feed(LivePcmSamplesEventArgs packet)
    {
        lock (_gate)
        {
            if (_finished) return;
            var buffer = _buffers[packet.Channel];
            if (buffer.Next != packet.StartSample)
            {
                // Audio buffered before a break in the stream was never handed to the model.
                if (buffer.Samples.Count > 0)
                    _gaps.Add(new LiveTranscriptGap(packet.Channel, buffer.Start, buffer.Start + buffer.Samples.Count, "stream-discontinuity"));
                buffer.Samples.Clear();
                buffer.Start = packet.StartSample;
                _vad?.Reset(packet.Channel);
                buffer.VadAccepted = 0;
                buffer.VadOffset = packet.StartSample;
            }
            buffer.Next = packet.StartSample + packet.Samples.Length;
            // Packets are normally 10–100 ms, but split larger packets too to keep memory bounded.
            for (var offset = 0; offset < packet.Samples.Length;)
            {
                var count = Math.Min(Math.Min(512, ChunkSamples - buffer.Samples.Count), packet.Samples.Length - offset);
                var samples = packet.Samples.AsSpan(offset, count).ToArray();
                buffer.Samples.AddRange(samples);
                offset += count;
                buffer.VadAccepted += count;
                if (_vad is not null) DrainSpeech(packet.Channel, buffer, _vad.Feed(packet.Channel, samples));
                if (buffer.Samples.Count < ChunkSamples) continue;
                if (_vad is not null)
                {
                    // Silero rotates at 15 seconds; retain its next utterance's leading audio while bounding silence.
                    var remove = buffer.Samples.Count - SampleRate;
                    buffer.Samples.RemoveRange(0, remove);
                    buffer.Start += remove;
                    continue;
                }
                var boundary = DictationRollingTranscriber.QuietBoundary(buffer.Samples);
                Enqueue(packet.Channel, buffer.Start, buffer.Samples.GetRange(0, boundary).ToArray());
                buffer.Samples.RemoveRange(0, boundary);
                buffer.Start += boundary;
            }
        }
    }

    public async Task FinishAsync(bool cancel = false)
    {
        List<Chunk> tails = [];
        lock (_gate)
        {
            if (!_finished)
            {
                _finished = true;
                _cancelled = cancel;
                foreach (var (channel, buffer) in _buffers)
                {
                    if (!cancel && _vad is not null)
                    {
                        foreach (var span in _vad.Finish(channel))
                            if (SpeechChunk(channel, buffer, span) is { } chunk) tails.Add(chunk);
                    }
                    else if (!cancel && buffer.Samples.Count > 0) tails.Add(new Chunk(channel, buffer.Start, buffer.Samples.ToArray()));
                    buffer.Samples.Clear();
                }
            }
        }
        // Capture has stopped; wait for space for the final tails without blocking audio callbacks.
        foreach (var tail in tails) await _chunks.Writer.WriteAsync(tail).ConfigureAwait(false);
        _chunks.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
        _vad?.Dispose();
    }

    private static Chunk? SpeechChunk(LiveTranscriptChannel channel, Buffer buffer, (long StartSample, long EndSample) span)
    {
        var start = Math.Max(buffer.Start, span.StartSample + buffer.VadOffset);
        var end = Math.Min(buffer.Start + buffer.Samples.Count, span.EndSample + buffer.VadOffset);
        return end <= start ? null : new Chunk(channel, start, buffer.Samples.GetRange((int)(start - buffer.Start), (int)(end - start)).ToArray());
    }

    private void DrainSpeech(LiveTranscriptChannel channel, Buffer buffer, IReadOnlyList<(long StartSample, long EndSample)> spans)
    {
        foreach (var span in spans)
        {
            if (SpeechChunk(channel, buffer, span) is { } chunk) Enqueue(channel, chunk.Start, chunk.Samples);
            var remove = (int)Math.Clamp(span.EndSample + buffer.VadOffset - buffer.Start, 0, buffer.Samples.Count);
            buffer.Samples.RemoveRange(0, remove);
            buffer.Start += remove;
        }
    }

    private void Enqueue(LiveTranscriptChannel channel, long start, float[] samples)
    {
        if (_chunks.Writer.TryWrite(new Chunk(channel, start, samples))) return;
        // The model is behind. Keep memory bounded and let Stop transcribe this stretch from the file.
        Interlocked.Increment(ref _dropped);
        _gaps.Add(new LiveTranscriptGap(channel, start, start + samples.Length, "transcription-backlog"));
    }

    private async Task DecodeAsync()
    {
        await foreach (var chunk in _chunks.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            lock (_gate) { if (_cancelled) continue; }
            try
            {
                var result = await _decode(DictationRollingTranscriber.ToWav(chunk.Samples)).ConfigureAwait(false);
                LiveTranscriptSnapshot snapshot;
                lock (_gate)
                {
                    if (_cancelled) continue;
                    if (!string.IsNullOrWhiteSpace(result.Text)) _committed.AddRange(Segments(chunk, result));
                    snapshot = new LiveTranscriptSnapshot(_committed.OrderBy(segment => segment.StartSample).ToArray(),
                        "", "", _chunks.Reader.Count, _dropped, 0, 0);
                }
                SnapshotChanged?.Invoke(this, snapshot);
            }
            catch (Exception exception)
            {
                lock (_gate)
                    _gaps.Add(new LiveTranscriptGap(chunk.Channel, chunk.Start, chunk.Start + chunk.Samples.Length, "decode-failed"));
                Failed?.Invoke(this, exception);
            }
        }
    }

    /// <summary>
    /// The model's own sentence timing inside a piece, so a 15-second piece with two speakers is
    /// not one block when speakers are matched to it later. Falls back to the whole piece.
    /// </summary>
    private static IEnumerable<LiveTranscriptSegment> Segments(Chunk chunk, TranscriptionResult result)
    {
        var end = chunk.Start + chunk.Samples.Length;
        var timed = result.Segments?.Where(segment => !string.IsNullOrWhiteSpace(segment.Text) && segment.EndMs > segment.StartMs).ToList();
        if (timed is not { Count: > 1 })
        {
            yield return new LiveTranscriptSegment($"batch_{Guid.NewGuid():N}", chunk.Channel, chunk.Start, end, result.Text.Trim());
            yield break;
        }
        foreach (var segment in timed)
        {
            var from = Math.Clamp(chunk.Start + segment.StartMs * (SampleRate / 1000L), chunk.Start, end);
            var to = Math.Clamp(chunk.Start + segment.EndMs * (SampleRate / 1000L), from, end);
            yield return new LiveTranscriptSegment($"batch_{Guid.NewGuid():N}", chunk.Channel, from, to, segment.Text.Trim());
        }
    }
}
