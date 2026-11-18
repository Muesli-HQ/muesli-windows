using System.Diagnostics;

namespace Muesli.Windows.Services;

/// <summary>
/// Latest microphone and system-audio level of a recording meeting, for the waveform. Written from
/// the capture threads on every audio buffer and read by the UI animation; nothing is marshalled.
/// It does not depend on live transcription, so the waveform follows the audio whether or not
/// captions are running.
///
/// A channel that stops delivering buffers reads as silent after <see cref="StaleAfter"/>:
/// Windows loopback delivers nothing while no sound is playing, so the last level must not linger.
/// </summary>
public sealed class MeetingLevelMeter
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMilliseconds(250);

    private readonly Func<long> _timestamp;
    private readonly long _staleTicks;
    private float _microphone;
    private float _system;
    private long _microphoneAt;
    private long _systemAt;

    public MeetingLevelMeter() : this(Stopwatch.GetTimestamp, Stopwatch.Frequency)
    {
    }

    /// <summary>Test seam: a caller-supplied clock and its ticks per second.</summary>
    public MeetingLevelMeter(Func<long> timestamp, long ticksPerSecond)
    {
        _timestamp = timestamp;
        _staleTicks = (long)(StaleAfter.TotalSeconds * ticksPerSecond);
        Reset();
    }

    /// <summary>Records the linear RMS level (0–1) of one captured buffer.</summary>
    public void Note(MeetingAudioChannel channel, double rms)
    {
        var level = double.IsFinite(rms) ? (float)Math.Clamp(rms, 0, 1) : 0f;
        var now = _timestamp();
        if (channel == MeetingAudioChannel.Microphone)
        {
            Volatile.Write(ref _microphone, level);
            Volatile.Write(ref _microphoneAt, now);
        }
        else
        {
            Volatile.Write(ref _system, level);
            Volatile.Write(ref _systemAt, now);
        }
    }

    public float Microphone => Fresh(Volatile.Read(ref _microphoneAt)) ? Volatile.Read(ref _microphone) : 0f;

    public float SystemAudio => Fresh(Volatile.Read(ref _systemAt)) ? Volatile.Read(ref _system) : 0f;

    /// <summary>The louder of the two channels: the waveform moves for you and for the other side.</summary>
    public float Current => Math.Max(Microphone, SystemAudio);

    public void Reset()
    {
        Volatile.Write(ref _microphone, 0f);
        Volatile.Write(ref _system, 0f);
        Volatile.Write(ref _microphoneAt, long.MinValue / 2);
        Volatile.Write(ref _systemAt, long.MinValue / 2);
    }

    private bool Fresh(long at) => _timestamp() - at <= _staleTicks;
}
