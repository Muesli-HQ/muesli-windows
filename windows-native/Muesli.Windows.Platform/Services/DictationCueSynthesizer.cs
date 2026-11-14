using System.Buffers.Binary;
using System.IO;
using System.Media;

namespace Muesli.Windows.Services;

/// <summary>
/// The dictation start cue: a short wooden knock, generated rather than borrowed from a Windows
/// sound. It is three inharmonic partials with a few milliseconds of noise at the attack, gone
/// within about a tenth of a second, so it reads as a tap on an object and not as an alert.
/// </summary>
public static class DictationCueSynthesizer
{
    public const int SampleRate = 44100;
    private const double DurationSeconds = 0.11;
    private const double Peak = 0.34;
    private const double AttackNoise = 0.25;

    // Frequency (Hz), relative amplitude, decay time constant (s). The ratios are deliberately
    // not whole numbers; harmonic partials would sound like a note.
    private static readonly (double Frequency, double Amplitude, double Decay)[] Knock =
    [
        (430, 1.00, 0.024),
        (705, 0.60, 0.014),
        (1180, 0.28, 0.007)
    ];

    /// <summary>A 16-bit mono WAV for the cue, or null for cues that play a Windows sound.</summary>
    public static byte[]? Wav(SoundCue cue) => cue == SoundCue.DictationStart ? RenderKnock() : null;

    private static byte[] RenderKnock()
    {
        var count = (int)(SampleRate * DurationSeconds);
        var samples = new double[count];
        // A fixed seed keeps the attack identical on every press.
        var random = new Random(7);
        var loudest = 0.0;
        for (var index = 0; index < count; index++)
        {
            var time = index / (double)SampleRate;
            var value = 0.0;
            foreach (var (frequency, amplitude, decay) in Knock)
            {
                value += amplitude * Math.Exp(-time / decay) * Math.Sin(2 * Math.PI * frequency * time);
            }

            // The first few milliseconds of a struck object are noise, not tone.
            value += AttackNoise * Math.Exp(-time / 0.003) * (random.NextDouble() * 2 - 1);
            // One millisecond of fade-in avoids a digital click at sample zero.
            value *= Math.Min(1.0, time / 0.001);
            samples[index] = value;
            loudest = Math.Max(loudest, Math.Abs(value));
        }

        var data = new byte[count * sizeof(short)];
        var gain = loudest > 0 ? Peak / loudest : 0;
        for (var index = 0; index < count; index++)
        {
            // Fade the last fifth so the tail never ends on a step.
            var tail = Math.Min(1.0, (count - index) / (count * 0.2));
            BinaryPrimitives.WriteInt16LittleEndian(
                data.AsSpan(index * 2, 2),
                (short)Math.Round(samples[index] * gain * tail * short.MaxValue));
        }

        using var stream = new MemoryStream(44 + data.Length);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + data.Length);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(SampleRate);
            writer.Write(SampleRate * sizeof(short));
            writer.Write((short)sizeof(short));
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(data.Length);
            writer.Write(data);
        }

        return stream.ToArray();
    }
}

/// <summary>
/// Plays the generated start cue, and leaves every other cue (the release click and the
/// model-ready chime) to the Windows media files.
/// </summary>
public sealed class DictationCuePlayer(ISystemSoundPlayer fallback, Action<string, Exception?>? report = null)
    : ISystemSoundPlayer
{
    private readonly Dictionary<SoundCue, SoundPlayer?> _players = [];
    private readonly object _gate = new();

    public void Prewarm()
    {
        foreach (var cue in Enum.GetValues<SoundCue>())
        {
            try
            {
                GetOrCreate(cue);
            }
            catch (Exception exception)
            {
                report?.Invoke($"Lifecycle sound prewarm failed for {cue}.", exception);
            }
        }

        fallback.Prewarm();
    }

    public void Play(SoundCue cue)
    {
        try
        {
            if (GetOrCreate(cue) is { } player)
            {
                player.Play();
                return;
            }
        }
        catch (Exception exception)
        {
            report?.Invoke($"Lifecycle sound failed for {cue}.", exception);
            return;
        }

        fallback.Play(cue);
    }

    private SoundPlayer? GetOrCreate(SoundCue cue)
    {
        lock (_gate)
        {
            if (_players.TryGetValue(cue, out var existing)) return existing;
            SoundPlayer? player = null;
            if (DictationCueSynthesizer.Wav(cue) is { } wav)
            {
                // The player reads from this stream on every Play, so it lives as long as the player.
                player = new SoundPlayer(new MemoryStream(wav, writable: false));
                player.Load();
            }

            _players[cue] = player;
            return player;
        }
    }
}
