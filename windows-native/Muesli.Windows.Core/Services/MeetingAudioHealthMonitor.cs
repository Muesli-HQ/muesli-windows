namespace Muesli.Windows.Services;

public enum MeetingAudioChannel
{
    Microphone,
    System
}

public enum MeetingChannelHealth
{
    Waiting,
    Healthy,
    Missing,
    Silent,
    Clipping
}

public sealed record MeetingAudioMetrics(
    MeetingAudioChannel Channel,
    DateTimeOffset Timestamp,
    int SampleCount,
    double Rms,
    float Peak,
    int ClippedSampleCount)
{
    public double ClippedRatio => SampleCount <= 0 ? 0 : (double)ClippedSampleCount / SampleCount;
}

public sealed record MeetingChannelHealthSnapshot(
    MeetingChannelHealth Health,
    DateTimeOffset? FirstCallbackAt,
    DateTimeOffset? LastCallbackAt,
    DateTimeOffset? LastSignalAt,
    long SampleCount,
    double Rms,
    float Peak,
    double ClippedRatio,
    string? Warning);

public sealed record MeetingAudioHealthSnapshot(
    MeetingChannelHealthSnapshot Microphone,
    MeetingChannelHealthSnapshot System)
{
    public bool IsDegraded =>
        Microphone.Health is MeetingChannelHealth.Missing or MeetingChannelHealth.Silent or MeetingChannelHealth.Clipping ||
        System.Health is MeetingChannelHealth.Missing or MeetingChannelHealth.Clipping;

    public IReadOnlyList<string> Warnings => new[] { Microphone.Warning, System.Warning }
        .Where(warning => !string.IsNullOrWhiteSpace(warning))
        .Cast<string>()
        .ToList();
}

public sealed class MeetingAudioHealthMonitor
{
    private const float SignalThreshold = 0.0001f;
    private const float PeerActiveThreshold = 0.01f;
    private const double ClippingRatioThreshold = 0.02;

    private readonly object _gate = new();
    private readonly DateTimeOffset _startedAt;
    private readonly TimeSpan _missingAfter;
    private readonly TimeSpan _silentAfter;
    private readonly TimeSpan _callbackFreshness;
    private readonly TimeSpan _clippingAfter;
    private readonly ChannelState _microphone = new();
    private readonly ChannelState _system = new();

    public MeetingAudioHealthMonitor(
        DateTimeOffset startedAt,
        TimeSpan? missingAfter = null,
        TimeSpan? silentAfter = null,
        TimeSpan? callbackFreshness = null,
        TimeSpan? clippingAfter = null)
    {
        _startedAt = startedAt;
        _missingAfter = missingAfter ?? TimeSpan.FromSeconds(3);
        _silentAfter = silentAfter ?? TimeSpan.FromSeconds(15);
        _callbackFreshness = callbackFreshness ?? TimeSpan.FromSeconds(2);
        _clippingAfter = clippingAfter ?? TimeSpan.FromSeconds(3);
    }

    public MeetingAudioHealthSnapshot Note(MeetingAudioMetrics metrics)
    {
        lock (_gate)
        {
            var channel = metrics.Channel == MeetingAudioChannel.Microphone ? _microphone : _system;
            channel.FirstCallbackAt ??= metrics.Timestamp;
            channel.LastCallbackAt = metrics.Timestamp;
            channel.SampleCount += Math.Max(0, metrics.SampleCount);
            channel.Rms = metrics.Rms;
            channel.Peak = metrics.Peak;
            channel.ClippedRatio = metrics.ClippedRatio;

            if (metrics.Peak > SignalThreshold)
            {
                channel.LastSignalAt = metrics.Timestamp;
            }

            if (metrics.ClippedRatio >= ClippingRatioThreshold)
            {
                channel.ClippingStartedAt ??= metrics.Timestamp;
            }
            else
            {
                channel.ClippingStartedAt = null;
            }

            return EvaluateLocked(metrics.Timestamp);
        }
    }

    public MeetingAudioHealthSnapshot Evaluate(DateTimeOffset now)
    {
        lock (_gate)
        {
            return EvaluateLocked(now);
        }
    }

    private MeetingAudioHealthSnapshot EvaluateLocked(DateTimeOffset now)
    {
        var mic = EvaluateChannel(
            MeetingAudioChannel.Microphone,
            _microphone,
            _system,
            now);
        var system = EvaluateChannel(
            MeetingAudioChannel.System,
            _system,
            _microphone,
            now);
        return new MeetingAudioHealthSnapshot(mic, system);
    }

    private MeetingChannelHealthSnapshot EvaluateChannel(
        MeetingAudioChannel channel,
        ChannelState state,
        ChannelState peer,
        DateTimeOffset now)
    {
        var peerActive = peer.LastSignalAt is { } peerSignal &&
                         now - peerSignal <= _callbackFreshness &&
                         peer.Peak >= PeerActiveThreshold;
        var callbackMissing = state.LastCallbackAt is null || now - state.LastCallbackAt > _callbackFreshness;
        var oldEnough = now - _startedAt >= _missingAfter;
        MeetingChannelHealth health;

        if (state.ClippingStartedAt is { } clippingStarted && now - clippingStarted >= _clippingAfter)
        {
            health = MeetingChannelHealth.Clipping;
        }
        else if (oldEnough && peerActive && callbackMissing)
        {
            health = MeetingChannelHealth.Missing;
        }
        else if (peerActive && state.FirstCallbackAt is { } firstCallback &&
                 now - (state.LastSignalAt ?? firstCallback) >= _silentAfter)
        {
            health = MeetingChannelHealth.Silent;
        }
        else if (state.LastSignalAt is not null)
        {
            health = MeetingChannelHealth.Healthy;
        }
        else
        {
            health = MeetingChannelHealth.Waiting;
        }

        var warning = WarningFor(channel, health);
        return new MeetingChannelHealthSnapshot(
            health,
            state.FirstCallbackAt,
            state.LastCallbackAt,
            state.LastSignalAt,
            state.SampleCount,
            state.Rms,
            state.Peak,
            state.ClippedRatio,
            warning);
    }

    private static string? WarningFor(MeetingAudioChannel channel, MeetingChannelHealth health) =>
        (channel, health) switch
        {
            (MeetingAudioChannel.Microphone, MeetingChannelHealth.Missing) =>
                "Microphone callbacks stopped while system audio remained active; your side may be incomplete.",
            (MeetingAudioChannel.Microphone, MeetingChannelHealth.Silent) =>
                "Microphone audio stayed silent while system audio was active; check the selected microphone.",
            (MeetingAudioChannel.Microphone, MeetingChannelHealth.Clipping) =>
                "Microphone audio is clipping; reduce the input level or move the microphone farther away.",
            (MeetingAudioChannel.System, MeetingChannelHealth.Missing) =>
                "System-audio callbacks stopped while the microphone remained active; remote speakers may be incomplete.",
            (MeetingAudioChannel.System, MeetingChannelHealth.Silent) =>
                "System audio stayed silent while the microphone was active; remote speakers may be missing.",
            (MeetingAudioChannel.System, MeetingChannelHealth.Clipping) =>
                "System audio is clipping; reduce the meeting application's output level.",
            _ => null
        };

    private sealed class ChannelState
    {
        public DateTimeOffset? FirstCallbackAt { get; set; }
        public DateTimeOffset? LastCallbackAt { get; set; }
        public DateTimeOffset? LastSignalAt { get; set; }
        public DateTimeOffset? ClippingStartedAt { get; set; }
        public long SampleCount { get; set; }
        public double Rms { get; set; }
        public float Peak { get; set; }
        public double ClippedRatio { get; set; }
    }
}

public static class AudioBufferMetrics
{
    public static MeetingAudioMetrics Measure(
        MeetingAudioChannel channel,
        byte[] buffer,
        int bytesRecorded,
        NAudio.Wave.WaveFormat format,
        DateTimeOffset? timestamp = null)
    {
        var sampleCount = 0;
        var clipped = 0;
        var peak = 0f;
        var sumSquares = 0d;

        void Add(float sample)
        {
            if (!float.IsFinite(sample))
            {
                return;
            }
            var absolute = Math.Abs(sample);
            sampleCount++;
            sumSquares += sample * sample;
            peak = Math.Max(peak, absolute);
            if (absolute >= 0.98f)
            {
                clipped++;
            }
        }

        if (format.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (var offset = 0; offset + 4 <= bytesRecorded; offset += 4)
            {
                Add(BitConverter.ToSingle(buffer, offset));
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (var offset = 0; offset + 2 <= bytesRecorded; offset += 2)
            {
                Add(BitConverter.ToInt16(buffer, offset) / 32768f);
            }
        }
        else if (format.BitsPerSample == 32)
        {
            for (var offset = 0; offset + 4 <= bytesRecorded; offset += 4)
            {
                Add(BitConverter.ToInt32(buffer, offset) / 2147483648f);
            }
        }

        return new MeetingAudioMetrics(
            channel,
            timestamp ?? DateTimeOffset.UtcNow,
            sampleCount,
            sampleCount == 0 ? 0 : Math.Sqrt(sumSquares / sampleCount),
            Math.Clamp(peak, 0, 1),
            clipped);
    }
}
