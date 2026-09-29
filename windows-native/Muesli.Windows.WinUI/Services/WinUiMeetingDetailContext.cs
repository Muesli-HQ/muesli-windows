using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// Owns the non-visual meeting-document workflows that need a bounded lifetime: playback,
/// transcript editing, safe candidate-based retranscription, summaries, and speaker aliases.
/// </summary>
public sealed class WinUiMeetingDetailContext : IDisposable
{
    private readonly WinUiLibraryContext _library;
    private readonly WinUiSettingsContext _settings;
    private readonly NativeTranscriptionClient _transcription;
    private readonly TranscriptEditService _transcriptEdits;
    private readonly MeetingRecordingPlaybackService _playback = new();
    private readonly AppLogService _log = new();
    private int _disposed;

    public WinUiMeetingDetailContext(WinUiLibraryContext library, WinUiSettingsContext settings)
    {
        _library = library;
        _settings = settings;
        _transcription = new NativeTranscriptionClient(settings.Load().FinalMeetingModelId);
        var candidates = new RetranscriptionCandidateStore(
            Path.Combine(library.Profile.DataDirectory, "retranscription"));
        _transcriptEdits = new TranscriptEditService(
            new LibraryTranscriptMeetingStore(library.History),
            candidates,
            new NativeMeetingRetranscriptionAsr(_transcription),
            new AppLogTranscriptEditDiagnostics(_log),
            captureStorage: new CaptureStorageService(library.Profile.CaptureDirectory));
        _playback.StateChanged += (_, args) => PlaybackChanged?.Invoke(this, args);
    }

    public event EventHandler<MeetingPlaybackStateChangedEventArgs>? PlaybackChanged;

    public MeetingPlaybackState PlaybackState => _playback.State;
    public TimeSpan PlaybackPosition => _playback.Position;
    public TimeSpan PlaybackDuration => _playback.Duration;
    public IReadOnlyList<double> WaveformPeaks => _playback.WaveformPeaks;

    public IReadOnlyList<MeetingPlaybackTrack> GetTracks(PersistedMeeting meeting) =>
        MeetingRecordingPlaybackService.SelectTracks(
            meeting.MicrophoneAudioPath,
            meeting.SystemAudioPath,
            meeting.SourcePath);

    public Task LoadTrackAsync(MeetingPlaybackTrack track, CancellationToken cancellationToken = default) =>
        _playback.LoadAsync(track, cancellationToken);

    public void Play() => _playback.Play();
    public void Pause() => _playback.Pause();
    public void Stop() => _playback.Stop();
    public void Seek(TimeSpan position) => _playback.Seek(position);

    public TranscriptEditResult SaveTranscript(PersistedMeeting meeting, string transcript)
    {
        var begin = _transcriptEdits.BeginTranscriptEdit(meeting.Id);
        if (!begin.Succeeded || begin.Session is null) return begin;
        return _transcriptEdits.SaveTranscriptEdit(begin.Session, transcript ?? "");
    }

    public async Task<SummaryGenerationResult> GenerateSummaryAsync(
        PersistedMeeting meeting,
        string templateName,
        CancellationToken cancellationToken = default)
    {
        var normalized = MeetingSummaryService.NormalizeTemplateName(templateName);
        var settings = _settings.Load() with { MeetingSummaryTemplate = normalized };
        var custom = _library.History.LoadMeetingTemplates().FirstOrDefault(template =>
            string.Equals(template.Name, normalized, StringComparison.OrdinalIgnoreCase));
        if (custom is not null)
        {
            settings = settings with { MeetingSummaryPromptOverride = custom.Prompt };
        }

        var result = await MeetingSummaryService.CreateSummaryResultAsync(
            meeting.Transcript,
            meeting.Title,
            settings,
            cancellationToken);
        var generatedTitle = MeetingTitleService.Generate(meeting.Transcript, meeting.CreatedAt, meeting.Title);
        var updated = MeetingNotesComposer.ApplyResummarization(
            meeting,
            result.Summary,
            normalized,
            generatedTitle);
        if (!_library.UpdateMeeting(updated))
        {
            throw new InvalidOperationException("The meeting no longer exists.");
        }
        _transcriptEdits.ClearGeneratedNotesStale(meeting.Id);
        return result;
    }

    public async Task<RetranscriptionResult> RetranscribeAsync(
        string meetingId,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _transcription.SwitchModelAsync(_settings.Load().FinalMeetingModelId, cancellationToken);
        return await _transcriptEdits.RetranscribeAsync(meetingId, progress, cancellationToken);
    }

    public RetranscriptionResult? RecoverCandidate(string meetingId)
    {
        var candidate = _transcriptEdits.GetPendingCandidate(meetingId);
        return candidate is null ? null : _transcriptEdits.RecoverInFlight(meetingId);
    }

    public RetranscriptionResult AcceptCandidate(string meetingId, string candidateId) =>
        _transcriptEdits.AcceptCandidate(meetingId, candidateId);

    public RetranscriptionResult RejectCandidate(string meetingId, string candidateId) =>
        _transcriptEdits.RejectCandidate(meetingId, candidateId);

    public PersistedMeeting SaveSpeakerAlias(PersistedMeeting meeting, string source, string replacement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacement);
        var aliases = new Dictionary<string, string>(meeting.SpeakerAliases, StringComparer.Ordinal)
        {
            [source.Trim()] = replacement.Trim()
        };
        var updated = meeting with { SpeakerAliases = aliases };
        if (!_library.UpdateMeeting(updated))
        {
            throw new InvalidOperationException("The meeting no longer exists.");
        }
        return updated;
    }

    public PersistedMeeting DeleteSpeakerAlias(PersistedMeeting meeting, string source)
    {
        var aliases = new Dictionary<string, string>(meeting.SpeakerAliases, StringComparer.Ordinal);
        aliases.Remove(source);
        var updated = meeting with { SpeakerAliases = aliases };
        if (!_library.UpdateMeeting(updated))
        {
            throw new InvalidOperationException("The meeting no longer exists.");
        }
        return updated;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _playback.Dispose();
        _transcriptEdits.Dispose();
        _transcription.Dispose();
    }
}
