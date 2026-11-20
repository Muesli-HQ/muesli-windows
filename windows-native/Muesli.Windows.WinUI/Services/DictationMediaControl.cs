using Muesli.Windows.Services;
using NAudio.CoreAudioApi;
using Windows.Media.Control;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// macOS pause_media and mute_audio: while a dictation records, pause whatever is playing and mute
/// the speakers, then undo exactly what was changed. Media that was already paused stays paused and
/// speakers the user had muted stay muted.
/// </summary>
internal sealed class DictationMediaControl(AppLogService log)
{
    private readonly List<GlobalSystemMediaTransportControlsSession> _paused = [];
    private string? _mutedDeviceId;
    private Task _applying = Task.CompletedTask;

    /// <summary>Starts in the background so it never delays the microphone.</summary>
    public void Apply(bool pauseMedia, bool muteAudio)
    {
        if (!pauseMedia && !muteAudio) return;
        _applying = Task.Run(async () =>
        {
            if (muteAudio) Mute();
            if (pauseMedia) await PauseAsync();
        });
    }

    public async Task RestoreAsync()
    {
        try { await _applying; } catch (Exception exception) { log.Error("Dictation media control failed.", exception); }
        if (_mutedDeviceId is { } id)
        {
            _mutedDeviceId = null;
            try
            {
                using var devices = new MMDeviceEnumerator();
                devices.GetDevice(id).AudioEndpointVolume.Mute = false;
            }
            catch (Exception exception) { log.Error("Speakers could not be unmuted after dictation.", exception); }
        }

        foreach (var session in _paused)
        {
            try { await session.TryPlayAsync(); }
            catch (Exception exception) { log.Error("Media could not be resumed after dictation.", exception); }
        }
        _paused.Clear();
    }

    private void Mute()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            var speakers = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (speakers.AudioEndpointVolume.Mute) return;
            speakers.AudioEndpointVolume.Mute = true;
            _mutedDeviceId = speakers.ID;
        }
        catch (Exception exception) { log.Error("Speakers could not be muted for dictation.", exception); }
    }

    private async Task PauseAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            foreach (var session in manager.GetSessions())
            {
                if (session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
                    await session.TryPauseAsync())
                {
                    _paused.Add(session);
                }
            }
        }
        catch (Exception exception) { log.Error("Media could not be paused for dictation.", exception); }
    }
}
