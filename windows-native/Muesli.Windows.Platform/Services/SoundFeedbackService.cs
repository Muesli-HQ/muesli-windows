using System.IO;
using System.Media;
using NAudio.CoreAudioApi;

namespace Muesli.Windows.Services;

public enum SoundCue
{
    DictationStart,
    DictationInsert,
    ModelReady
}

public enum DictationSessionKind
{
    Interactive,
    SetupTest,
    Auxiliary
}

public enum AudioOutputRouteKind
{
    SpeakerLike,
    HeadphoneLike,
    Unknown
}

public readonly record struct OutputDeviceDescription(int FormFactor, bool HasRender);

/// <summary>
/// Windows equivalent of macOS SoundController: Tink on dictation start, Purr on release,
/// Glass when a model is ready. Lifecycle cues play only on confirmed speaker-like output.
/// </summary>
public sealed class SoundFeedbackService
{
    private readonly ISystemSoundPlayer _player;
    private readonly Func<AudioOutputRouteKind> _routeKind;

    public SoundFeedbackService(
        ISystemSoundPlayer? player = null,
        Func<AudioOutputRouteKind>? routeKind = null)
    {
        _player = player ?? new WindowsSystemSoundPlayer();
        _routeKind = routeKind ?? DefaultRenderRouteInspector.Inspect;
    }

    public bool Enabled { get; set; } = true;

    public void Prewarm() => _player.Prewarm();

    public void PlayDictationStart(DictationSessionKind sessionKind = DictationSessionKind.Interactive) =>
        Play(SoundCue.DictationStart, sessionKind);

    public void PlayDictationInsert(DictationSessionKind sessionKind = DictationSessionKind.Interactive) =>
        Play(SoundCue.DictationInsert, sessionKind);

    public void PlayModelReady() => Play(SoundCue.ModelReady, DictationSessionKind.Interactive);

    private void Play(SoundCue cue, DictationSessionKind sessionKind)
    {
        if (!SoundFeedbackPolicy.ShouldPlay(cue, Enabled, _routeKind(), sessionKind))
        {
            return;
        }

        _player.Play(cue);
    }
}

public static class SoundFeedbackPolicy
{
    public static bool ShouldPlay(
        SoundCue cue,
        bool enabled,
        AudioOutputRouteKind routeKind,
        DictationSessionKind sessionKind)
    {
        if (!enabled)
        {
            return false;
        }

        if (cue != SoundCue.ModelReady && sessionKind != DictationSessionKind.Interactive)
        {
            return false;
        }

        if (cue != SoundCue.ModelReady && AudioOutputRouteClassifier.SuppressesLifecycleSounds(routeKind))
        {
            return false;
        }

        return true;
    }
}

public static class AudioOutputRouteClassifier
{
    public const int FormFactorUnknown = 0;
    public const int FormFactorRemoteNetworkDevice = 1;
    public const int FormFactorSpeakers = 2;
    public const int FormFactorLineLevel = 3;
    public const int FormFactorHeadphones = 4;
    public const int FormFactorMicrophone = 5;
    public const int FormFactorHeadset = 6;
    public const int FormFactorHandset = 7;
    public const int FormFactorUnknownDigitalPassthrough = 8;
    public const int FormFactorSpdif = 9;
    public const int FormFactorDigitalAudioDisplayDevice = 10;

    public static AudioOutputRouteKind Classify(OutputDeviceDescription device)
    {
        if (!device.HasRender)
        {
            return AudioOutputRouteKind.Unknown;
        }

        return device.FormFactor switch
        {
            FormFactorSpeakers or FormFactorLineLevel or FormFactorSpdif or FormFactorDigitalAudioDisplayDevice
                => AudioOutputRouteKind.SpeakerLike,
            FormFactorHeadphones or FormFactorHeadset or FormFactorHandset
                => AudioOutputRouteKind.HeadphoneLike,
            _ => AudioOutputRouteKind.Unknown
        };
    }

    public static bool SuppressesLifecycleSounds(AudioOutputRouteKind kind) =>
        kind != AudioOutputRouteKind.SpeakerLike;
}

public static class WindowsSystemSoundLocator
{
    public static readonly IReadOnlyList<string> DictationStartCandidates =
        ["Windows Ding.wav", "ding.wav", "Windows Navigation Start.wav"];

    public static readonly IReadOnlyList<string> DictationInsertCandidates =
        ["Windows Notify.wav", "notify.wav", "Windows Balloon.wav"];

    public static readonly IReadOnlyList<string> ModelReadyCandidates =
        ["chimes.wav", "Windows Notify Calendar.wav", "Windows Notify Email.wav"];

    public static string DefaultMediaDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");

    public static IReadOnlyList<string> CandidatesFor(SoundCue cue) => cue switch
    {
        SoundCue.DictationStart => DictationStartCandidates,
        SoundCue.DictationInsert => DictationInsertCandidates,
        SoundCue.ModelReady => ModelReadyCandidates,
        _ => []
    };

    public static string? Resolve(SoundCue cue, string mediaDirectory, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        foreach (var name in CandidatesFor(cue))
        {
            var path = Path.Combine(mediaDirectory, name);
            if (exists(path))
            {
                return path;
            }
        }

        return null;
    }
}

public interface ISystemSoundPlayer
{
    void Play(SoundCue cue);
    void Prewarm() { }
}

public sealed class WindowsSystemSoundPlayer : ISystemSoundPlayer
{
    private readonly Func<SoundCue, string?> _resolve;
    private readonly Action<string, Exception?>? _report;
    private readonly Dictionary<SoundCue, SoundPlayer> _players = [];
    private readonly object _gate = new();

    public WindowsSystemSoundPlayer(
        Func<SoundCue, string?>? resolve = null,
        Action<string, Exception?>? report = null)
    {
        _resolve = resolve ?? (cue => WindowsSystemSoundLocator.Resolve(cue, WindowsSystemSoundLocator.DefaultMediaDirectory));
        _report = report;
    }

    public void Prewarm()
    {
        foreach (SoundCue cue in Enum.GetValues<SoundCue>())
        {
            try
            {
                GetOrCreate(cue)?.Load();
            }
            catch (Exception exception)
            {
                _report?.Invoke($"Lifecycle sound prewarm failed for {cue}.", exception);
            }
        }
    }

    public void Play(SoundCue cue)
    {
        try
        {
            var player = GetOrCreate(cue);
            if (player is null)
            {
                _report?.Invoke($"Lifecycle sound skipped; no Windows Media file found for {cue}.", null);
                return;
            }

            player.Play();
        }
        catch (Exception exception)
        {
            _report?.Invoke($"Lifecycle sound failed for {cue}.", exception);
        }
    }

    private SoundPlayer? GetOrCreate(SoundCue cue)
    {
        lock (_gate)
        {
            if (_players.TryGetValue(cue, out var existing))
            {
                return existing;
            }

            var path = _resolve(cue);
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var player = new SoundPlayer(path);
            _players[cue] = player;
            return player;
        }
    }
}

public static class DefaultRenderRouteInspector
{
    private static readonly PropertyKey FormFactorKey = new(
        new Guid(0x1da5d803, 0xd492, 0x4edd, 0x8c, 0x23, 0xe0, 0xc0, 0xff, 0xee, 0x7f, 0x0e),
        0);

    public static AudioOutputRouteKind Inspect()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var formFactor = ReadFormFactor(device);
            return AudioOutputRouteClassifier.Classify(new OutputDeviceDescription(formFactor, HasRender: true));
        }
        catch
        {
            return AudioOutputRouteKind.Unknown;
        }
    }

    internal static int ReadFormFactor(MMDevice device)
    {
        try
        {
            if (!device.Properties.Contains(FormFactorKey))
            {
                return AudioOutputRouteClassifier.FormFactorUnknown;
            }

            return Convert.ToInt32(device.Properties[FormFactorKey].Value);
        }
        catch
        {
            return AudioOutputRouteClassifier.FormFactorUnknown;
        }
    }
}
