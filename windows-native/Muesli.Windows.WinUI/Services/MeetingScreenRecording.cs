using Muesli.Windows.Core.Profiles;
using Muesli.Windows.Services;
using Muesli.Windows.Services.Persistence;
using Windows.Graphics.Capture;
using Windows.Storage;

namespace Muesli.Windows.WinUI.Services;

/// <summary>macOS ScreenRecordingPauseState: an explicit screen pause survives a meeting resume.</summary>
public readonly record struct ScreenRecordingPauseState(bool UserPaused = false, bool MeetingPaused = false)
{
    public bool IsPaused => UserPaused || MeetingPaused;
}

/// <summary>
/// Port of macOS VisualMemoryController (2f10b23d) without the legacy visual-memory catalog, which never
/// existed on Windows. Starts only from an explicit user action, writes one clip per uninterrupted
/// interval, pauses with the meeting and for privacy on sleep or lock, and owns clip recovery.
/// Every member runs on the UI thread.
/// </summary>
public sealed class MeetingScreenRecording
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(8);

    private readonly MeetingVideoStore? _store;
    private readonly Func<WinUiMeetingContext> _meetings;
    private readonly AppLogService _log;
    private readonly HashSet<Guid> _recordedSessions = [];
    private readonly Dictionary<Guid, HashSet<string>> _sessionClipIds = [];
    private ScreenVideoRecorder? _recorder;
    private MeetingVideoClip? _recordingClip;
    private GraphicsCaptureItem? _item;
    private Guid? _recordingSessionId;
    private Task? _finalization;
    private Task? _start;
    private DateTimeOffset _meetingStart;
    private Guid _token = Guid.NewGuid();
    private global::Windows.Media.Playback.MediaPlayer? _announcement;

    public MeetingScreenRecording(MuesliDatabase? database, Func<WinUiMeetingContext> meetings, AppLogService log)
    {
        _meetings = meetings;
        _log = log;
        if (database is not null)
        {
            try { _store = new MeetingVideoStore(database, Path.Combine(MuesliProfilePaths.Current().CaptureDirectory, "videos")); }
            catch (Exception error) { Message = error.Message; }
        }
        _ = MaintainLoopAsync();
    }

    public event EventHandler? Changed;

    public string? RecordingMeetingId { get; private set; }
    public bool IsRecording => RecordingMeetingId is not null;
    public bool IsPreparing { get; private set; }
    public bool IsFinalizing { get; private set; }
    public ScreenRecordingPauseState PauseState { get; private set; }
    public string SourceName { get; private set; } = "";
    public string? Message { get; private set; }
    public string? MessageMeetingId { get; private set; }
    public bool IsAvailable => _store is not null;

    public IReadOnlyList<MeetingVideoClip> Clips(string meetingId)
    {
        try { return _store?.Clips(meetingId) ?? []; }
        catch (Exception error) { _log.Error("Meeting videos could not be read.", error); return []; }
    }

    public string? MediaPath(MeetingVideoClip clip)
    {
        try { return _store?.FullPath(clip.Path) is { } path && File.Exists(path) ? path : null; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>On a read error, keep the meeting rather than deleting possibly retained video.</summary>
    public bool HasMedia(string meetingId)
    {
        try { return _store?.HasMedia(meetingId) ?? false; }
        catch (Exception) { return true; }
    }

    public bool HasRecording(Guid sessionId) => _recordedSessions.Contains(sessionId);

    public void ClearMessage()
    {
        Message = null;
        Raise();
    }

    public void Delete(MeetingVideoClip clip)
    {
        try { _store?.Delete(new HashSet<string> { clip.Id }); }
        catch (Exception error) { Message = error.Message; }
        Raise();
    }

    /// <summary>Starts recording the confirmed source. The caller has already shown the picker.</summary>
    public async Task StartAsync(string meetingId, Guid sessionId, DateTimeOffset meetingStart, GraphicsCaptureItem item, string sourceName)
    {
        if (RecordingMeetingId is not null || IsPreparing || IsFinalizing) return;
        IsPreparing = true; Message = null; MessageMeetingId = meetingId; _token = Guid.NewGuid();
        var attempt = _token;
        Raise();
        try
        {
            _item = item;
            SourceName = sourceName;
            _meetingStart = meetingStart;
            RecordingMeetingId = meetingId;
            _recordingSessionId = sessionId;
            PauseState = new ScreenRecordingPauseState(MeetingPaused: _meetings().IsPaused);
            _start = BeginClipAsync(attempt);
            await _start;
            _recordedSessions.Add(sessionId);
        }
        catch (Exception error)
        {
            Message = error.Message;
            RecordingMeetingId = null; _item = null; _recordingSessionId = null;
        }
        finally
        {
            _start = null;
            if (_token == attempt) IsPreparing = false;
            Raise();
        }
    }

    private async Task BeginClipAsync(Guid attempt)
    {
        if (_store is not { } store) throw new InvalidOperationException("Meeting video storage is unavailable. Please restart Muesli and try again.");
        if (_token != attempt || _recorder is not null || _item is not { } item || RecordingMeetingId is not { } meetingId || PauseState.IsPaused) return;
        if (FreeBytes(store.Root) <= 2_000_000_000L) throw new InvalidOperationException("Screen recording needs at least 2 GB of free disk space.");
        var meetings = _meetings();
        var id = Guid.NewGuid().ToString();
        var clipStart = DateTimeOffset.Now;
        var clip = new MeetingVideoClip(id, meetingId, clipStart, SourceName, id + ".mp4",
            MeetingOffset: Math.Max(0, (clipStart - _meetingStart).TotalSeconds));
        var recording = new ScreenVideoRecorder(store.FullPath(clip.Path), () => meetings.ElapsedMeetingSamples);
        recording.Failed += reason => App.UiDispatcher.TryEnqueue(async () =>
        {
            if (_token != attempt) return;
            Message = reason;
            await StopAsync();
        });
        store.BeginRecording(clip);
        _recordingClip = clip;
        if (_recordingSessionId is { } session)
        {
            if (!_sessionClipIds.TryGetValue(session, out var ids)) _sessionClipIds[session] = ids = [];
            ids.Add(clip.Id);
        }
        _recorder = recording;
        meetings.ScreenAudioSink = (samples, startSample, microphone) => recording.AppendAudio(samples, startSample, microphone);
        try { await recording.StartAsync(item); }
        catch
        {
            meetings.ScreenAudioSink = null;
            _recorder = null; _recordingClip = null;
            store.EndRecording(clip);
            try { store.Delete(new HashSet<string> { clip.Id }); } catch (Exception) { /* The durable cleanup retries. */ }
            throw;
        }
        _recordingClip = clip with { PixelWidth = recording.PixelWidth, PixelHeight = recording.PixelHeight };
    }

    private async Task FinishClipAsync(bool discard = false)
    {
        if (_finalization is { } pending) { await pending; return; }
        if (_recorder is not { } recording || _recordingClip is not { } clip) return;
        _recorder = null; _recordingClip = null;
        _meetings().ScreenAudioSink = null;
        IsFinalizing = true;
        Raise();
        var store = _store;
        _finalization = Task.Run(async () =>
        {
            try
            {
                // The timeout releases only this waiter; the journal stays until the real completion.
                var duration = await recording.StopAsync(StopTimeout, _ => store?.EndRecording(clip));
                if (!discard && store is not null)
                {
                    var path = store.FullPath(clip.Path);
                    store.PersistCompleted(clip with { Duration = duration, Bytes = new FileInfo(path).Length });
                }
            }
            catch (Exception error)
            {
                _log.Error("Screen recording finalization failed.", error);
                App.UiDispatcher.TryEnqueue(() =>
                {
                    MessageMeetingId = clip.MeetingId;
                    Message = $"Screen recording was interrupted. Its file and recovery information were retained: {error.Message}";
                    Raise();
                });
            }
            if (discard)
            {
                store?.EndRecording(clip);
                try { store?.Delete(new HashSet<string> { clip.Id }); }
                catch (Exception error) { App.UiDispatcher.TryEnqueue(() => { Message = $"Video cleanup will be retried: {error.Message}"; Raise(); }); }
            }
        });
        try { await _finalization; }
        finally
        {
            _finalization = null;
            IsFinalizing = false;
            Raise();
        }
    }

    public async Task StopAsync(bool discard = false)
    {
        _token = Guid.NewGuid();
        _announcement?.Dispose();
        _announcement = null;
        if (_start is { } pending) { try { await pending; } catch (Exception) { } }
        IsPreparing = false;
        await FinishClipAsync(discard);
        RecordingMeetingId = null; _recordingSessionId = null; _item = null; PauseState = default;
        Raise();
    }

    /// <summary>Discarding a session (including a resumed one) removes only that session's new clips.</summary>
    public async Task DiscardSessionAsync(Guid sessionId)
    {
        if (_recordingSessionId == sessionId) await StopAsync(discard: true);
        var ids = _sessionClipIds.Remove(sessionId, out var clips) ? clips : [];
        try { if (ids.Count > 0) await Task.Run(() => _store?.Delete(ids)); }
        catch (Exception error) { Message = $"Could not remove discarded video: {error.Message}"; }
        _recordedSessions.Remove(sessionId);
        Raise();
    }

    public void ToggleScreenPause()
    {
        if (RecordingMeetingId is null || IsFinalizing || IsPreparing) return;
        PauseState = PauseState with { UserPaused = !PauseState.UserPaused };
        _ = ReconcilePauseAsync();
    }

    public void SetMeetingPaused(bool paused)
    {
        if (RecordingMeetingId is null || PauseState.MeetingPaused == paused) return;
        PauseState = PauseState with { MeetingPaused = paused };
        _ = ReconcilePauseAsync();
    }

    /// <summary>Sleep or lock pauses capture for privacy; resuming stays an explicit user action.</summary>
    public void SuspendForPrivacy()
    {
        if (RecordingMeetingId is not { } id) return;
        MessageMeetingId = id;
        Message = "Screen recording paused while the PC was locked or asleep.";
        PauseState = PauseState with { UserPaused = true };
        _ = ReconcilePauseAsync();
    }

    private async Task ReconcilePauseAsync()
    {
        var attempt = _token;
        Raise();
        if (_start is { } start) { try { await start; } catch (Exception) { } }
        if (_finalization is { } finalization) await finalization;
        if (_token != attempt) return;
        if (PauseState.IsPaused) await FinishClipAsync();
        else if (_recorder is null)
        {
            try { await BeginClipAsync(attempt); }
            catch (Exception error) { Message = error.Message; await StopAsync(); }
        }
        Raise();
    }

    /// <summary>macOS plays a bundled clip; Windows speaks the same sentence locally on the default output,
    /// never into the call. A failure warns and recording continues.</summary>
    public async Task PlayAnnouncementAsync()
    {
        try
        {
            using var synthesizer = new global::Windows.Media.SpeechSynthesis.SpeechSynthesizer();
            var speech = await synthesizer.SynthesizeTextToStreamAsync("This meeting is being recorded.");
            _announcement?.Dispose();
            _announcement = new global::Windows.Media.Playback.MediaPlayer
            {
                Source = global::Windows.Media.Core.MediaSource.CreateFromStream(speech, speech.ContentType)
            };
            _announcement.Play();
        }
        catch (Exception error)
        {
            _log.Error("Recording announcement could not play.", error);
            Message = "Announcement could not play. Screen recording continues; please notify participants manually.";
            Raise();
        }
    }

    private async Task MaintainLoopAsync()
    {
        while (true)
        {
            if (_store is { } store)
            {
                try { await Task.Run(() => store.Maintain(RecoverableDuration)); }
                catch (Exception error) { _log.Error("Meeting video recovery/cleanup will be retried.", error); }
            }
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>A recovered file is published only when it contains playable video.</summary>
    private static double? RecoverableDuration(string path)
    {
        try
        {
            var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
            var video = file.Properties.GetVideoPropertiesAsync().AsTask().GetAwaiter().GetResult();
            return video.Width > 0 && video.Duration > TimeSpan.Zero ? video.Duration.TotalSeconds : null;
        }
        catch (Exception) { return null; }
    }

    private static long FreeBytes(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace; }
        catch (Exception) { return 0; }
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
