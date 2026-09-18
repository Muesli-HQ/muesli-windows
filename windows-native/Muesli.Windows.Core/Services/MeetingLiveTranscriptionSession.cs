using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using SherpaOnnx;

namespace Muesli.Windows.Services;

public enum LiveTranscriptChannel
{
    Microphone,
    System
}

public sealed class LivePcmSamplesEventArgs(LiveTranscriptChannel channel, float[] samples, long startSample) : EventArgs
{
    public LiveTranscriptChannel Channel { get; } = channel;
    public float[] Samples { get; } = samples;
    public long StartSample { get; } = startSample;
}

public sealed record LiveTranscriptSegment(
    string Id,
    LiveTranscriptChannel Channel,
    long StartSample,
    long EndSample,
    string Text);

public sealed record LiveTranscriptGap(
    LiveTranscriptChannel Channel,
    long StartSample,
    long EndSample,
    string Reason);

public sealed record LiveTranscriptSnapshot(
    IReadOnlyList<LiveTranscriptSegment> Committed,
    string PartialMicrophone,
    string PartialSystem,
    int QueuedPackets,
    long DroppedPackets,
    float MicrophoneLevel,
    float SystemLevel)
{
    public string CommittedText => string.Join(Environment.NewLine, Committed
        .OrderBy(segment => segment.StartSample)
        .Select(segment => $"{(segment.Channel == LiveTranscriptChannel.Microphone ? "You" : "Others")}: {segment.Text}"));
}

public sealed record MeetingLiveTranscriptionResult(
    IReadOnlyList<LiveTranscriptSegment> Committed,
    IReadOnlyList<LiveTranscriptGap> Gaps,
    long DroppedPackets,
    string ModelId,
    LiveTranscriptOwnershipMode OwnershipMode);

public sealed record LiveTranscriptionConfiguration(
    string ModelId,
    LiveTranscriptOwnershipMode OwnershipMode,
    bool ShowWaveformOnHover);

internal interface ILiveRecognizer : IDisposable
{
    string Feed(LiveTranscriptChannel channel, float[] samples);
    string CommitBoundary(LiveTranscriptChannel channel);
    string Finish(LiveTranscriptChannel channel);
}

internal interface ILiveVad : IDisposable
{
    IReadOnlyList<(long StartSample, long EndSample)> Feed(LiveTranscriptChannel channel, float[] samples);
    IReadOnlyList<(long StartSample, long EndSample)> Finish(LiveTranscriptChannel channel);
}

internal sealed class NativeLiveRecognizer : ILiveRecognizer
{
    private sealed class StreamState(OnlineStream stream)
    {
        public OnlineStream Stream { get; set; } = stream;
        public string Text { get; set; } = "";
    }

    private readonly OnlineRecognizer _recognizer;
    private readonly Dictionary<LiveTranscriptChannel, StreamState> _streams;
    private bool _disposed;

    public NativeLiveRecognizer(StreamingModelDefinition model)
    {
        if (!new StreamingModelInstaller(model).IsVerified)
            throw new InvalidOperationException($"{model.DisplayName} is not downloaded and verified.");

        string File(string name) => Path.Combine(model.ModelPath, name);
        var config = new OnlineRecognizerConfig
        {
            DecodingMethod = "greedy_search",
            MaxActivePaths = 4,
            EnableEndpoint = 0,
            Rule1MinTrailingSilence = 0,
            Rule2MinTrailingSilence = 0,
            Rule3MinUtteranceLength = 0
        };
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount, 1, 8);
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.ModelConfig.Transducer.Encoder = File("encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = File("decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = File("joiner.int8.onnx");
        config.ModelConfig.Tokens = File("tokens.txt");
        _recognizer = new OnlineRecognizer(config);
        _streams = Enum.GetValues<LiveTranscriptChannel>().ToDictionary(channel => channel, _ => NewStream());
    }

    private StreamState NewStream()
    {
        var stream = _recognizer.CreateStream();
        if (stream.HasOption("language")) stream.SetOption("language", "auto");
        return new StreamState(stream);
    }

    public string Feed(LiveTranscriptChannel channel, float[] samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = _streams[channel];
        state.Stream.AcceptWaveform(16000, samples);
        while (_recognizer.IsReady(state.Stream)) _recognizer.Decode(state.Stream);
        state.Text = Clean(_recognizer.GetResult(state.Stream).Text);
        return state.Text;
    }

    public string CommitBoundary(LiveTranscriptChannel channel)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = _streams[channel];
        var text = state.Text;
        state.Stream.Dispose();
        _streams[channel] = NewStream();
        return text;
    }

    public string Finish(LiveTranscriptChannel channel)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = _streams[channel];
        state.Stream.InputFinished();
        while (_recognizer.IsReady(state.Stream)) _recognizer.Decode(state.Stream);
        state.Text = Clean(_recognizer.GetResult(state.Stream).Text);
        return state.Text;
    }

    private static string Clean(string? text) => Regex.Replace(text ?? "", @"<[^>]+>", " ").Trim();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var state in _streams.Values) state.Stream.Dispose();
        _recognizer.Dispose();
    }
}

internal sealed class NativeSileroVad : ILiveVad
{
    private sealed class State(VoiceActivityDetector detector)
    {
        public VoiceActivityDetector Detector { get; } = detector;
        public long AcceptedSamples { get; set; }
    }
    private readonly Dictionary<LiveTranscriptChannel, State> _states;
    private bool _disposed;

    public NativeSileroVad(StreamingModelDefinition model)
    {
        _states = Enum.GetValues<LiveTranscriptChannel>().ToDictionary(channel => channel, _ =>
        {
            var config = new VadModelConfig { SampleRate = 16000, NumThreads = 1, Provider = "cpu", Debug = 0 };
            config.SileroVad.Model = model.VadPath;
            config.SileroVad.Threshold = 0.25f;
            config.SileroVad.MinSilenceDuration = 0.5f;
            config.SileroVad.MinSpeechDuration = 0.25f;
            config.SileroVad.WindowSize = 512;
            // Zero disables arbitrary duration rotation. Boundaries are emitted only
            // after Silero observes trailing silence or the session is finalized.
            config.SileroVad.MaxSpeechDuration = 0;
            return new State(new VoiceActivityDetector(config, 30));
        });
    }

    public IReadOnlyList<(long StartSample, long EndSample)> Feed(LiveTranscriptChannel channel, float[] samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = _states[channel];
        state.Detector.AcceptWaveform(samples);
        state.AcceptedSamples += samples.Length;
        return Drain(state);
    }

    public IReadOnlyList<(long StartSample, long EndSample)> Finish(LiveTranscriptChannel channel)
    {
        var state = _states[channel];
        state.Detector.Flush();
        return Drain(state);
    }

    private static IReadOnlyList<(long, long)> Drain(State state)
    {
        var boundaries = new List<(long, long)>();
        while (!state.Detector.IsEmpty())
        {
            var segment = state.Detector.Front();
            var start = Math.Max(0, (long)segment.Start);
            boundaries.Add((start, start + segment.Samples.LongLength));
            state.Detector.Pop();
        }
        return boundaries;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var state in _states.Values) state.Detector.Dispose();
    }
}

public sealed class MeetingLiveTranscriptionSession : IAsyncDisposable
{
    internal const string DroppedPacketReason = "bounded-queue-pressure";
    internal static readonly TimeSpan DefaultPublishInterval = TimeSpan.FromMilliseconds(200);
    /// <summary>Drop ranges closer than 100 ms are treated as one measured gap.</summary>
    private const long ContiguousDropSamples = 1600;
    /// <summary>Upper bound on retained drop ranges; further drops widen the newest range.</summary>
    internal const int MaxPendingDropRanges = 512;
    /// <summary>
    /// Identical text is only a duplicate when it re-commits within 1 s of the previous segment,
    /// which is a re-emission of the same audio rather than a speaker genuinely repeating themselves.
    /// </summary>
    internal const long DuplicateWindowSamples = 16000;

    private sealed record Packet(LiveTranscriptChannel Channel, float[] Samples, long StartSample);
    private readonly Channel<Packet> _queue;
    private readonly ILiveRecognizer _recognizer;
    private readonly ILiveVad _vad;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private readonly object _stateGate = new();
    private readonly object _dropGate = new();
    private readonly List<LiveTranscriptSegment> _committed = [];
    private readonly List<LiveTranscriptGap> _gaps = [];
    private readonly List<LiveTranscriptGap> _pendingDrops = [];
    private readonly Dictionary<LiveTranscriptChannel, int> _lastDropIndex = [];
    private readonly Dictionary<LiveTranscriptChannel, long> _lastSampleEnds = [];
    private readonly string _modelId;
    private readonly LiveTranscriptOwnershipMode _ownership;
    private readonly TimeSpan _publishInterval;
    private string _partialMicrophone = "";
    private string _partialSystem = "";
    private float _microphoneLevel;
    private float _systemLevel;
    private long _lastPublishTimestamp;
    private int _queued;
    private long _dropped;
    private int _finishing;
    private int _disposed;

    public MeetingLiveTranscriptionSession(
        StreamingModelDefinition model,
        LiveTranscriptOwnershipMode ownership,
        int capacity = 8)
        : this(new NativeLiveRecognizer(model), new NativeSileroVad(model), model.Id, ownership, capacity)
    {
    }

    internal MeetingLiveTranscriptionSession(
        ILiveRecognizer recognizer,
        ILiveVad vad,
        string modelId,
        LiveTranscriptOwnershipMode ownership,
        int capacity = 8,
        TimeSpan? publishInterval = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _recognizer = recognizer;
        _vad = vad;
        _modelId = modelId;
        _ownership = ownership;
        _publishInterval = publishInterval ?? DefaultPublishInterval;
        _lastPublishTimestamp = Stopwatch.GetTimestamp();
        _queue = Channel.CreateBounded<Packet>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _worker = Task.Run(ProcessAsync);
    }

    public event EventHandler<LiveTranscriptSnapshot>? SnapshotChanged;
    public event EventHandler<Exception>? Failed;

    public bool TryEnqueue(LivePcmSamplesEventArgs samples)
    {
        if (Volatile.Read(ref _finishing) != 0 || Volatile.Read(ref _disposed) != 0) return false;
        var packet = new Packet(samples.Channel, samples.Samples, samples.StartSample);
        if (_queue.Writer.TryWrite(packet))
        {
            Interlocked.Increment(ref _queued);
            return true;
        }
        Interlocked.Increment(ref _dropped);
        RecordDroppedRange(samples.Channel, samples.StartSample, samples.StartSample + samples.Samples.LongLength);
        return false;
    }

    /// <summary>
    /// Runs on the WASAPI capture callback thread. It must stay allocation-light, must not
    /// take the snapshot lock, and must never publish: doing an O(committed) snapshot copy
    /// and a dispatcher hop here would slow the durable recorders exactly when the machine
    /// is already too loaded to keep up with live inference. The worker drains these ranges
    /// into the measured-gap ledger on its next iteration.
    /// </summary>
    private void RecordDroppedRange(LiveTranscriptChannel channel, long start, long end)
    {
        lock (_dropGate)
        {
            if (_lastDropIndex.TryGetValue(channel, out var index))
            {
                var prior = _pendingDrops[index];
                if (start <= prior.EndSample + ContiguousDropSamples || _pendingDrops.Count >= MaxPendingDropRanges)
                {
                    _pendingDrops[index] = prior with
                    {
                        StartSample = Math.Min(prior.StartSample, start),
                        EndSample = Math.Max(prior.EndSample, end)
                    };
                    return;
                }
            }
            _lastDropIndex[channel] = _pendingDrops.Count;
            _pendingDrops.Add(new(channel, start, end, DroppedPacketReason));
        }
    }

    /// <summary>Moves capture-thread drop ranges into the gap ledger. Caller holds <see cref="_stateGate"/>.</summary>
    private bool DrainDroppedRangesUnderGate()
    {
        lock (_dropGate)
        {
            if (_pendingDrops.Count == 0) return false;
            _gaps.AddRange(_pendingDrops);
            _pendingDrops.Clear();
            _lastDropIndex.Clear();
            return true;
        }
    }

    public LiveTranscriptSnapshot Snapshot()
    {
        lock (_stateGate)
        {
            return new(_committed.ToList(), _partialMicrophone, _partialSystem, Math.Max(0, Volatile.Read(ref _queued)), Interlocked.Read(ref _dropped), _microphoneLevel, _systemLevel);
        }
    }

    public async Task<MeetingLiveTranscriptionResult> FinishAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _finishing, 1) == 0) _queue.Writer.TryComplete();
        await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_stateGate)
        {
            return new(_committed.ToList(), CoalesceGaps(_gaps), _dropped, _modelId, _ownership);
        }
    }

    public void Cancel()
    {
        if (Interlocked.Exchange(ref _finishing, 1) == 0) _queue.Writer.TryComplete(new OperationCanceledException());
        _shutdown.Cancel();
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var packet in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _queued);
                var partial = _recognizer.Feed(packet.Channel, packet.Samples);
                var boundaries = _vad.Feed(packet.Channel, packet.Samples);
                bool changed;
                lock (_stateGate)
                {
                    changed = DrainDroppedRangesUnderGate();
                    _lastSampleEnds[packet.Channel] = Math.Max(
                        _lastSampleEnds.GetValueOrDefault(packet.Channel),
                        packet.StartSample + packet.Samples.LongLength);
                    var level = PeakLevel(packet.Samples);
                    if (packet.Channel == LiveTranscriptChannel.Microphone) { _partialMicrophone = partial; _microphoneLevel = level; }
                    else { _partialSystem = partial; _systemLevel = level; }
                    if (boundaries.Count > 0)
                    {
                        // The recognizer stream accumulates every word decoded since the last
                        // commit, so several VAD boundaries drained together are one committed
                        // span rather than one commit per boundary. Committing per boundary
                        // would return empty text for all but the first and record speech that
                        // was actually transcribed as a measured gap.
                        var text = Normalize(_recognizer.CommitBoundary(packet.Channel));
                        changed |= CommitUnderGate(packet.Channel, boundaries[0].StartSample, boundaries[^1].EndSample, text);
                        if (packet.Channel == LiveTranscriptChannel.Microphone) _partialMicrophone = ""; else _partialSystem = "";
                    }
                }
                Publish(force: changed);
            }

            foreach (var channel in Enum.GetValues<LiveTranscriptChannel>())
            {
                var boundaries = _vad.Finish(channel);
                var tail = Normalize(_recognizer.Finish(channel));
                lock (_stateGate)
                {
                    DrainDroppedRangesUnderGate();
                    if (boundaries.Count > 0)
                    {
                        CommitUnderGate(channel, boundaries[0].StartSample, boundaries[^1].EndSample, tail);
                    }
                    else if (!string.IsNullOrWhiteSpace(tail))
                    {
                        var end = _lastSampleEnds.GetValueOrDefault(channel, 1);
                        CommitUnderGate(channel, 0, Math.Max(1, end), tail);
                    }
                    if (channel == LiveTranscriptChannel.Microphone) _partialMicrophone = ""; else _partialSystem = "";
                }
            }
            Publish(force: true);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (_stateGate)
            {
                DrainDroppedRangesUnderGate();
                foreach (var channel in Enum.GetValues<LiveTranscriptChannel>())
                    _gaps.Add(new(channel, 0, long.MaxValue, "streaming-engine-failure"));
            }
            Failed?.Invoke(this, exception);
        }
        finally
        {
            // Packets dropped after the last worker iteration must still reach the ledger,
            // otherwise a cancelled or failed session would under-report recoverable audio.
            lock (_stateGate) DrainDroppedRangesUnderGate();
        }
    }

    private static float PeakLevel(float[] samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            var magnitude = Math.Abs(sample);
            if (magnitude > peak) peak = magnitude;
        }
        return peak;
    }

    /// <summary>Returns true when committed or gap state changed and the UI should be refreshed immediately.</summary>
    private bool CommitUnderGate(LiveTranscriptChannel channel, long start, long end, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _gaps.Add(new(channel, start, end, "speech-without-committed-text"));
            return true;
        }
        var previous = _committed.LastOrDefault(segment => segment.Channel == channel);
        // Only treat an identical repeat as a duplicate when it lands on essentially the same audio.
        // A speaker who genuinely says "no" twice, separated by a pause, must keep both lines.
        if (previous is not null &&
            SameText(previous.Text, text) &&
            start - previous.EndSample <= DuplicateWindowSamples)
        {
            return false;
        }
        _committed.Add(new($"live_{channel}_{_committed.Count + 1}", channel, start, Math.Max(start + 1, end), text));
        return true;
    }

    internal static bool SameText(string first, string second) =>
        Normalize(first).Equals(Normalize(second), StringComparison.OrdinalIgnoreCase);
    internal static string Normalize(string? text) => Regex.Replace(text?.Trim() ?? "", @"\s+", " ");

    internal static IReadOnlyList<LiveTranscriptGap> CoalesceGaps(IEnumerable<LiveTranscriptGap> gaps)
    {
        var result = new List<LiveTranscriptGap>();
        foreach (var gap in gaps.OrderBy(g => g.Channel).ThenBy(g => g.StartSample))
        {
            if (result.Count > 0 && result[^1] is var prior && prior.Channel == gap.Channel && gap.StartSample <= prior.EndSample + 1600 && prior.Reason == gap.Reason)
                result[^1] = prior with { EndSample = Math.Max(prior.EndSample, gap.EndSample) };
            else result.Add(gap);
        }
        return result;
    }

    /// <summary>
    /// Coalesces provisional-tail and level updates so a long meeting cannot drive an
    /// O(committed) snapshot copy plus a dispatcher layout pass at capture-callback rate.
    /// Committed text, measured gaps, and finalization always publish immediately.
    /// </summary>
    private void Publish(bool force)
    {
        var handler = SnapshotChanged;
        if (handler is null) return;
        var now = Stopwatch.GetTimestamp();
        if (!force &&
            Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastPublishTimestamp), now) < _publishInterval)
        {
            return;
        }
        Interlocked.Exchange(ref _lastPublishTimestamp, now);
        handler.Invoke(this, Snapshot());
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Cancel();
        try { await _worker.ConfigureAwait(false); } catch { }
        _recognizer.Dispose();
        _vad.Dispose();
        _shutdown.Dispose();
    }
}
