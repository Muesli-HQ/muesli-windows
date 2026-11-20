using Muesli.Windows.Services;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Core.Insights;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// Owns the real meeting capture/transcription lifecycle for the shipping WinUI process. It uses
/// the active profile and reuses the shared Platform coordinator and Core persistence records.
/// </summary>
public sealed class WinUiMeetingContext : IDisposable
{
    private readonly WinUiLibraryContext _library;
    private readonly AppLogService _log = new();
    private readonly NativeTranscriptionClient _transcription;
    private readonly MeetingRecordingCoordinator _coordinator;
    private readonly WinUiSettingsContext _settingsContext;
    private readonly IUiDispatcher? _dispatcher;
    private readonly NativeTextCleanupService _cleanup;
    private readonly TranscriptionPipelineService _pipeline;
    private MuesliSettings _settings;
    private string _title = "";
    private DateTime _startedAt;
    private int _operationBusy;
    private int _disposed;

    public WinUiMeetingContext(WinUiLibraryContext library, WinUiSettingsContext? settings = null, IUiDispatcher? dispatcher = null)
    {
        _library = library;
        _settingsContext = settings ?? new WinUiSettingsContext(library);
        _settings = _settingsContext.Load();
        _dispatcher = dispatcher;
        _cleanup = new NativeTextCleanupService(_log);
        _pipeline = new TranscriptionPipelineService(_cleanup, _log);
        _transcription = new NativeTranscriptionClient(_settings.FinalMeetingModelId);
        _coordinator = new MeetingRecordingCoordinator(_transcription, _log);
        _coordinator.StateChanged += (_, _) =>
        {
            _recoverable = null;
            CaptureStatus = MeetingCaptureStatusFormatter.Describe(_coordinator.CaptureSnapshot);
            RaiseChanged();
        };
        _coordinator.ProcessingStageChanged += (_, _) => RaiseChanged();
        _coordinator.LiveTranscriptChanged += (_, snapshot) =>
        {
            LatestLiveSnapshot = snapshot;
            LiveTranscript = BuildLiveText(snapshot);
            MicrophoneLevel = snapshot.MicrophoneLevel;
            SystemLevel = snapshot.SystemLevel;
            RaiseChanged();
        };
        _coordinator.LiveTranscriptionFailed += (_, exception) =>
        {
            _log.Error("WinUI live meeting transcription failed.", exception);
            RaiseChanged();
        };
    }

    public event EventHandler? Changed;
    public event EventHandler? SourceWarningDismissed;

    public MeetingSessionState State => _coordinator.State;

    public bool IsRecording => _coordinator.IsRecording;

    public bool IsBusy => _coordinator.IsBusy || Volatile.Read(ref _operationBusy) != 0;

    public bool IsPaused => _coordinator.State == MeetingSessionState.RecoverableInterruption;

    public string? ActiveMeetingId { get; private set; }

    /// <summary>macOS screenRecordingSessionID: identifies this capture session's screen clips, so a
    /// discard (including of a resumed meeting) removes only the clips it added.</summary>
    public Guid CaptureSessionId { get; private set; } = Guid.NewGuid();

    public DateTimeOffset StartedAt => _startedAt;

    /// <summary>Routes the meeting audio feed into the active screen clip; null detaches it.</summary>
    public Action<float[], long, bool>? ScreenAudioSink { get => _coordinator.ScreenAudioSink; set => _coordinator.ScreenAudioSink = value; }

    public long ElapsedMeetingSamples => _coordinator.ElapsedMeetingSamples;

    /// <summary>Finishes screen recording before the meeting stops (false) or is discarded (true).</summary>
    public Func<bool, Task>? ScreenRecordingFinishing { get; set; }

    /// <summary>True when screen clips (saved or journaled) belong to the meeting; a read error answers true.</summary>
    public Func<string, bool>? HasScreenMedia { get; set; }

    /// <summary>Clips reference the meeting row, so screen recording saves the in-progress draft first.</summary>
    public void EnsureDraftSaved() => SaveManualNotesCore();
    public string ActiveTitle => _title;
    public string ManualNotes { get; private set; } = "";
    public bool AreNotesSaved { get; private set; } = true;
    public bool IsSourceMissing { get; private set; }
    public bool IsRollingPreview => string.IsNullOrWhiteSpace(_settings.LiveMeetingModelId);
    public bool IsResumingFinishedMeeting => _coordinator.ResumedMeeting is not null;
    public string LiveTranscriptPrefix => _coordinator.ResumedMeeting?.Transcript ?? "";
    public string PreviousMeetingNotes => _coordinator.ResumedMeeting is { } prior
        ? string.IsNullOrWhiteSpace(prior.Summary) ? prior.Transcript : prior.Summary : "";

    public void SetSourceMissing(bool missing)
    {
        if (IsSourceMissing == missing) return;
        IsSourceMissing = missing;
        RaiseChanged();
    }

    public void KeepRecording()
    {
        SourceWarningDismissed?.Invoke(this, EventArgs.Empty);
        SetSourceMissing(false);
    }

    public void SaveManualNotes(string notes)
    {
        ThrowIfDisposed();
        if (ActiveMeetingId is null || (!IsRecording && !IsPaused) || IsBusy)
            throw new InvalidOperationException("Notes can be edited while a meeting is recording or paused.");
        ManualNotes = notes;
        AreNotesSaved = false;
        SaveManualNotesCore();
    }

    /// <summary>Records typed notes in memory; Stop, discard and the debounced save persist them.</summary>
    public void UpdateManualNotesDraft(string notes)
    {
        ThrowIfDisposed();
        if (ActiveMeetingId is null) return;
        ManualNotes = notes;
        AreNotesSaved = false;
    }

    private PersistedMeeting DraftMeeting(string id) => _library.FindMeeting(id) ?? new PersistedMeeting
    {
        SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion,
        Id = id, Title = _title, CreatedAt = _startedAt,
        ModelProfile = _settings.FinalMeetingModelId, SessionState = State
    };

    // macOS lastRecordingTemplateSelection: the last saved choice, kept in case the library can't be read at Stop.
    private string? _recordingTemplate;

    public IReadOnlyList<string> TemplateNames => MeetingSummaryService.BuiltInTemplateNames
        .Concat(_library.LoadMeetingTemplates().Select(template => template.Name))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The template the live meeting will be summarized with.</summary>
    public string RecordingTemplate => _recordingTemplate ?? MeetingSummaryService.NormalizeTemplateName(_settings.MeetingSummaryTemplate);

    /// <summary>
    /// macOS selectRecordingTemplate (14617391): saves a per-meeting choice while recording, without generating
    /// notes or changing the default. Locked once Stop begins.
    /// </summary>
    public void SelectRecordingTemplate(string name)
    {
        ThrowIfDisposed();
        if (ActiveMeetingId is not { } id || (!IsRecording && !IsPaused) || IsBusy)
            throw new InvalidOperationException("The template is locked once recording stops.");
        var normalized = MeetingSummaryService.NormalizeTemplateName(name);
        normalized = TemplateNames.FirstOrDefault(template => string.Equals(template, normalized, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("That template no longer exists. Choose another template and try again.");
        SaveMeeting(DraftMeeting(id) with { TemplateName = normalized });
        _recordingTemplate = normalized;
        RaiseChanged();
    }

    /// <summary>macOS lockMeetingTemplateForProcessing (23c3fe69): the saved choice, else the last one saved this session.</summary>
    private string LockRecordingTemplate(string id)
    {
        var template = _recordingTemplate;
        try
        {
            if (_library.FindMeeting(id)?.TemplateName is { Length: > 0 } saved) template = saved;
        }
        catch (Exception exception)
        {
            _log.Error("Meeting template could not be read at Stop; keeping the saved selection.", exception);
        }
        _recordingTemplate = null;
        return template ?? MeetingSummaryService.NormalizeTemplateName(_settings.MeetingSummaryTemplate);
    }

    private void SaveManualNotesCore()
    {
        if (ActiveMeetingId is null) return;
        var meeting = DraftMeeting(ActiveMeetingId);
        try { SaveMeeting(MeetingNotesComposer.ApplyManualNotes(meeting, ManualNotes)); }
        catch (Exception exception)
        {
            _log.Error("Meeting written notes could not be saved; the draft remains in memory.", exception);
            throw;
        }
        AreNotesSaved = true;
    }


    public string LiveTranscript { get; private set; } = "";

    public string CaptureStatus { get; private set; } = "Idle";

    public double MicrophoneLevel { get; private set; }

    public double SystemLevel { get; private set; }

    // Discovery reads (and repairs) journals on disk. Cache it so live snapshots, which raise
    // Changed several times a second, never rescan; lifecycle transitions invalidate it.
    private IReadOnlyList<RecoverableMeetingSession>? _recoverable;

    public IReadOnlyList<RecoverableMeetingSession> RecoverableSessions =>
        _recoverable ??= _coordinator.DiscoverRecoverableSessions();

    /// <summary>
    /// Stop was requested but saving failed; the audio is retained and Stop retries. Distinct from
    /// a user pause, which shares the coordinator's interrupted state.
    /// </summary>
    public bool FinalizationFailed { get; private set; }
    public string? ProcessingStage => _coordinator.ProcessingStage;

    public LiveTranscriptSnapshot? LatestLiveSnapshot { get; private set; }

    public MeetingCaptureSnapshot CaptureSnapshot => _coordinator.CaptureSnapshot;

    /// <summary>macOS 7c3abb66: a Quick Note started inside a folder is filed there.</summary>
    public async Task StartQuickNoteAsync(CancellationToken cancellationToken = default, string? folderId = null)
    {
        await StartMeetingAsync($"Quick Note {DateTime.Now:g}", null, cancellationToken);
        if (ExistingFolder(folderId) is { } folder && ActiveMeetingId is { } id)
            SaveMeeting(DraftMeeting(id) with { FolderId = folder });
    }

    /// <summary>The folder, unless it was deleted meanwhile (macOS inserts with <c>SELECT id FROM meeting_folders</c>).</summary>
    private string? ExistingFolder(string? folderId) =>
        !string.IsNullOrWhiteSpace(folderId) && _library.History.LoadMeetingFolders().Any(folder => folder.Id == folderId) ? folderId : null;

    public Task StartMeetingAsync(string title, int? processId, CancellationToken cancellationToken = default) =>
        RunOperationAsync(async () =>
        {
            if (IsRecording || IsPaused) throw new InvalidOperationException("A meeting is already active.");
            await RefreshSettingsAsync(cancellationToken);
            _title = title;
            try { await StartCoreAsync(processId, cancellationToken); }
            catch (Exception exception)
            {
                DiagnosticIncidentPrompts.Record(DiagnosticIncident.MeetingStartFailed, "start_meeting_recording", _settings.FinalMeetingModelId, exception);
                throw;
            }
            return true;
        });

    public Task ResumeFinishedAsync(string meetingId, CancellationToken cancellationToken = default) => RunOperationAsync(async () =>
    {
        if (IsRecording || IsPaused) throw new InvalidOperationException("Finish the active meeting first.");
        var meeting = _library.FindMeeting(meetingId) ?? throw new InvalidOperationException("The meeting no longer exists.");
        if (!MeetingContinuation.CanContinue(meeting)) throw new InvalidOperationException("Only completed meetings can be resumed.");
        await RefreshSettingsAsync(cancellationToken);
        _title = meeting.Title;
        await StartCoreAsync(null, cancellationToken, meeting);
        return true;
    });

    public Task StartFollowUpAsync(string meetingId, CancellationToken cancellationToken = default) => RunOperationAsync(async () =>
    {
        if (IsRecording || IsPaused) throw new InvalidOperationException("Finish the active meeting first.");
        var predecessor = _library.FindMeeting(meetingId) ?? throw new InvalidOperationException("The meeting no longer exists.");
        if (!MeetingContinuation.CanContinue(predecessor)) throw new InvalidOperationException("Only completed meetings can have follow-ups.");
        if (!_library.SupportsMeetingThreads) throw new InvalidOperationException("Meeting threads require the SQLite library.");
        await RefreshSettingsAsync(cancellationToken);
        _title = MeetingContinuation.FollowUpTitle(predecessor.Title);
        await StartCoreAsync(null, cancellationToken);
        try
        {
            SaveManualNotesCore();
            var draft = _library.FindMeeting(ActiveMeetingId!)! with { FolderId = predecessor.FolderId };
            SaveMeeting(draft);
            _library.LinkFollowUp(predecessor, draft);
        }
        catch
        {
            await _coordinator.CancelAsync();
            if (ActiveMeetingId is { } id) _library.DeleteMeeting(id);
            ActiveMeetingId = null;
            throw;
        }
        return true;
    });

    private async Task StartCoreAsync(int? processId, CancellationToken cancellationToken, PersistedMeeting? resumedMeeting = null)
    {
        ThrowIfDisposed();
        _suspendedForPower = false;
        CaptureSessionId = Guid.NewGuid();
        LiveTranscript = "";
        LatestLiveSnapshot = null;
        ManualNotes = "";
        AreNotesSaved = true;
        ActiveMeetingId = null;
        _recordingTemplate = null;
        IsSourceMissing = false;
        MicrophoneLevel = SystemLevel = 0;
        _startedAt = DateTime.Now;
        LiveTranscriptionConfiguration? liveConfiguration = null;
        if (!string.IsNullOrWhiteSpace(_settings.LiveMeetingModelId))
        {
            liveConfiguration = new LiveTranscriptionConfiguration(
                _settings.LiveMeetingModelId,
                LiveTranscriptOwnershipDescriptor.ModeFromSettingValue(_settings.LiveTranscriptOwnership),
                _settings.ShowLiveWaveformOnHover);
        }

        var start = await _coordinator.StartAsync(
            microphoneName: _settings.MicrophoneName,
            title: _title,
            retainRecording: _settings.SaveMeetingRecordings,
            targetProcessId: processId,
            cancellationToken: cancellationToken,
            liveConfiguration: liveConfiguration,
            resumedMeeting: resumedMeeting);
        ActiveMeetingId = resumedMeeting?.Id ?? start.MeetingId;
        if (resumedMeeting is not null) ManualNotes = resumedMeeting.ManualNotes;
        CaptureStatus = MeetingCaptureStatusFormatter.Describe(_coordinator.CaptureSnapshot);
        RaiseChanged();
    }

    public Task<PersistedMeeting> StopAndSaveAsync(CancellationToken cancellationToken = default) =>
        RunOperationAsync(() => StopAndSaveCoreAsync(cancellationToken));

    private async Task<PersistedMeeting> StopAndSaveCoreAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        // Retry a failed autosave before stopping capture. A storage error must not lose writing.
        if (!AreNotesSaved) SaveManualNotesCore();
        if (ScreenRecordingFinishing is { } finishScreen) await finishScreen(false);
        _coordinator.DiarizeMicrophoneAudio = _settings.DiarizeMicrophoneAudio;
        var template = ActiveMeetingId is { } activeId ? LockRecordingTemplate(activeId) : null;
        RecordedMeetingResult result;
        try
        {
            result = await _coordinator.StopAsync(
                _title,
                _settings.SaveMeetingRecordings,
                cancellationToken);
        }
        catch (MeetingSessionRecoverableException exception)
        {
            FinalizationFailed = true;
            RaiseChanged();
            DiagnosticIncidentPrompts.Record(DiagnosticIncident.MeetingProcessingFailed, "meeting_stop_processing", _settings.FinalMeetingModelId, exception);
            throw;
        }
        FinalizationFailed = false;
        var persisted = Persist(result);
        var unchangedSpeech = IsResumingFinishedMeeting && string.IsNullOrWhiteSpace(result.Transcript);
        _coordinator.AcknowledgePersisted(result.MeetingId);
        ActiveMeetingId = null;
        IsSourceMissing = false;
        var meeting = unchangedSpeech ? persisted : await FinalizeMeetingAsync(persisted, cancellationToken, template);
        meeting = await CompressSavedTracksAsync(meeting);
        LiveTranscript = "";
        LatestLiveSnapshot = null;
        CaptureStatus = MeetingCaptureStatusFormatter.Describe(_coordinator.CaptureSnapshot);
        RaiseChanged();
        return meeting;
    }

    /// <summary>
    /// Asks how to discard the active recording, then discards it. Every user-facing discard
    /// (Meetings page, floating pill) goes through here so audio and notes are never deleted
    /// without confirmation. Returns the outcome status, or null when the user keeps recording.
    /// </summary>
    public async Task<string?> ConfirmAndDiscardAsync(IAppDialogService dialogs, bool hasNotes)
    {
        var resumed = IsResumingFinishedMeeting;
        var choice = resumed
            ? await dialogs.ConfirmAsync("Discard only this resumed recording? The original meeting and your written notes will be kept.",
                "Discard resumed recording", "Discard new recording", "Cancel")
            : hasNotes
            ? await dialogs.ChooseAsync("Discard the audio recording? You can keep your written notes as a note-only meeting.",
                "Discard recording", "Keep notes", "Delete draft", "Cancel")
            : await dialogs.ConfirmAsync("Discard this audio recording?", "Discard recording", "Discard", "Cancel");
        if (choice is not (AppDialogChoice.Primary or AppDialogChoice.Secondary)) return null;
        var keepNotes = resumed || hasNotes && choice == AppDialogChoice.Primary;
        await CancelAsync(keepNotes);
        return resumed ? "Resumed recording discarded. Original meeting kept." : keepNotes ? "Recording discarded. Written notes saved." : "Recording discarded";
    }

    public async Task CancelAsync(bool keepNotes = false)
    {
        ThrowIfDisposed();
        if (IsBusy) throw new InvalidOperationException("Wait for the current meeting operation to finish.");
        if (keepNotes) SaveManualNotesCore();
        var resumed = IsResumingFinishedMeeting;
        if (ScreenRecordingFinishing is { } finishScreen) await finishScreen(true);
        await _coordinator.CancelAsync();
        FinalizationFailed = false;
        if (ActiveMeetingId is { } id)
        {
            if (resumed) { /* Discard only the appended capture; the existing meeting and writing survive. */ }
            else if (keepNotes && _library.FindMeeting(id) is { } draft)
                SaveMeeting(draft with { SessionState = MeetingSessionState.NoteOnly });
            else _library.DeleteMeeting(id);
        }
        ActiveMeetingId = null;
        ManualNotes = "";
        IsSourceMissing = false;
        LiveTranscript = "";
        LatestLiveSnapshot = null;
        CaptureStatus = MeetingCaptureStatusFormatter.Describe(_coordinator.CaptureSnapshot);
        RaiseChanged();
    }

    public async Task PauseAsync()
    {
        ThrowIfDisposed();
        await _coordinator.SuspendAsync(
            "user paused recording",
            "Recording paused. Capture will resume when you choose Resume.",
            "user-pause");
        CaptureStatus = MeetingCaptureStatusFormatter.Describe(_coordinator.CaptureSnapshot);
        RaiseChanged();
    }

    // Windows sleep: checkpoint the capture so the audio survives, and resume on wake only when
    // the sleep (not the user) paused it.
    private volatile bool _suspendedForPower;

    public async Task SuspendForPowerAsync()
    {
        if (Volatile.Read(ref _disposed) != 0 || !IsRecording) return;
        await _coordinator.SuspendAsync();
        _suspendedForPower = true;
        CaptureStatus = MeetingCaptureStatusFormatter.Describe(_coordinator.CaptureSnapshot);
        RaiseChanged();
    }

    public async Task ResumeAfterPowerAsync()
    {
        if (!_suspendedForPower || Volatile.Read(ref _disposed) != 0) return;
        _suspendedForPower = false;
        if (IsPaused) await ResumeAsync();
    }

    public async Task ResumeAsync()
    {
        ThrowIfDisposed();
        await _coordinator.ResumeAsync();
        _suspendedForPower = false;
        FinalizationFailed = false;
        CaptureStatus = MeetingCaptureStatusFormatter.Describe(_coordinator.CaptureSnapshot);
        RaiseChanged();
    }

    public Task<PersistedMeeting> FinalizeRecoverableAsync(
        RecoverableMeetingSession recovery,
        CancellationToken cancellationToken = default) => RunOperationAsync(async () =>
        {
            if (IsRecording || IsPaused) throw new InvalidOperationException("Finish the active meeting before recovering another.");
            if (recovery.Journal.ResumedMeeting is { } original && _library.FindMeeting(original.Id) is null)
                throw new InvalidOperationException("The original meeting was deleted. Discard this recovered capture to avoid restoring deleted content.");
            await RefreshSettingsAsync(cancellationToken);
            return await FinalizeRecoverableCoreAsync(recovery, cancellationToken);
        });

    private async Task<PersistedMeeting> FinalizeRecoverableCoreAsync(
        RecoverableMeetingSession recovery,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var result = await _coordinator.FinalizeRecoverableAsync(recovery, cancellationToken);
        var persisted = Persist(result);
        var unchangedSpeech = IsResumingFinishedMeeting && string.IsNullOrWhiteSpace(result.Transcript);
        _coordinator.AcknowledgePersisted(result.MeetingId);
        var meeting = unchangedSpeech ? persisted : await FinalizeMeetingAsync(persisted, cancellationToken);
        meeting = await CompressSavedTracksAsync(meeting);
        LiveTranscript = "";
        LatestLiveSnapshot = null;
        CaptureStatus = MeetingCaptureStatusFormatter.Describe(_coordinator.CaptureSnapshot);
        RaiseChanged();
        return meeting;
    }

    public Task DiscardRecoveryAsync(RecoverableMeetingSession recovery) => RunOperationAsync(() =>
    {
        if (IsRecording || IsPaused) throw new InvalidOperationException("Finish the active meeting before discarding recovered audio.");
        var id = recovery.Journal.SessionId;
        _coordinator.DiscardRecoverable(id);
        _recoverable = null;
        if (recovery.Journal.ResumedMeeting is null && _library.FindMeeting(id) is { } draft && string.IsNullOrWhiteSpace(draft.Transcript))
        {
            // macOS resolveLiveMeetingAfterStopFailure: a draft that owns screen video is kept.
            if (string.IsNullOrWhiteSpace(draft.ManualNotes) && HasScreenMedia?.Invoke(id) != true) _library.DeleteMeeting(id);
            else SaveMeeting(draft with { SessionState = MeetingSessionState.NoteOnly });
        }
        RaiseChanged();
        return Task.FromResult(true);
    });

    public Task<PersistedMeeting> ImportAsync(
        string sourcePath,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? folderId = null) => RunOperationAsync(async () =>
        {
            if (IsRecording || IsPaused) throw new InvalidOperationException("Finish the active meeting before importing media.");
            await RefreshSettingsAsync(cancellationToken);
            return await ImportCoreAsync(sourcePath, progress, cancellationToken, folderId);
        });

    private async Task<PersistedMeeting> ImportCoreAsync(
        string sourcePath,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? folderId = null)
    {
        ThrowIfDisposed();
        if (!MediaImportFormats.IsSupported(sourcePath))
        {
            throw new NotSupportedException(MediaImportFormats.ConversionGuidanceFor(sourcePath));
        }

        var title = Path.GetFileNameWithoutExtension(sourcePath);
        var result = await _transcription.TranscribeFileAsync(title, sourcePath, progress, cancellationToken);
        if (string.IsNullOrWhiteSpace(result.Text))
        {
            throw new InvalidOperationException("No speech was detected in the selected file.");
        }

        var transcript = result.Text.Trim();
        var meeting = new PersistedMeeting
        {
            SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion,
            Id = $"meet_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid().ToString("N")[..8]}",
            Title = title,
            CreatedAt = DateTime.Now,
            DurationMs = result.DurationMs,
            Transcript = transcript,
            Summary = "",
            SourcePath = Path.GetFullPath(sourcePath),
            ModelProfile = _transcription.ModelId,
            WordCount = CountWords(transcript),
            SessionState = MeetingSessionState.Completed,
            SystemCaptureMode = "imported-media",
            FinalTranscriptOwnerModelId = _transcription.ModelId,
            // Checked after transcription: the folder may have been deleted while the import ran.
            FolderId = ExistingFolder(folderId)
        };
        SaveMeeting(meeting);
        meeting = await FinalizeMeetingAsync(meeting, cancellationToken);
        RaiseChanged();
        return meeting;
    }

    public async Task PreserveForShutdownAsync()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            await _coordinator.PreserveForShutdownAsync();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _coordinator.Dispose();
        _transcription.Dispose();
        _cleanup.Dispose();
    }

    private PersistedMeeting Persist(RecordedMeetingResult result)
    {
        var transcript = result.Transcript?.Trim() ?? "";
        var meeting = new PersistedMeeting
        {
            SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion,
            Id = result.MeetingId,
            Title = result.Title,
            CreatedAt = result.StartedAt,
            DurationMs = result.DurationMs,
            Transcript = transcript,
            Summary = "",
            SourcePath = string.Join(
                "; ",
                new[] { result.MicAudioPath, result.SystemAudioPath }
                    .Where(path => !string.IsNullOrWhiteSpace(path))),
            ModelProfile = _transcription.ModelId,
            WordCount = CountWords(transcript),
            HealthWarnings = result.HealthWarnings ?? [],
            SessionState = result.SessionState,
            MicrophoneAudioPath = result.MicAudioPath,
            SystemAudioPath = result.SystemAudioPath,
            SystemCaptureMode = result.SystemCaptureMode,
            RecoveredFromInterruption = result.RecoveredFromInterruption,
            LivePreviewModelId = result.LivePreviewModelId,
            LiveTranscriptOwnership = result.LiveTranscriptOwnership,
            FinalTranscriptOwnerModelId = result.FinalTranscriptOwnerModelId,
            GapRecoveryModelId = result.GapRecoveryModelId
        };
        if (_coordinator.ResumedMeeting is { } prior) meeting = MeetingContinuation.AppendRecording(prior, meeting);
        meeting = MeetingNotesComposer.ApplyRecordedMeeting(meeting, _library.FindMeeting(meeting.Id));
        SaveMeeting(meeting);
        return meeting;
    }

    private void SaveMeeting(PersistedMeeting meeting) => _library.SaveMeeting(meeting);

    /// <summary>Recording format M4A: compress the kept tracks once the transcript no longer needs them.</summary>
    private async Task<PersistedMeeting> CompressSavedTracksAsync(PersistedMeeting meeting)
    {
        if (!_settings.SaveMeetingRecordings || _settings.MeetingRecordingFormat != "m4a") return meeting;
        var microphone = await Task.Run(() => MeetingRecordingCompression.ToM4a(meeting.MicrophoneAudioPath, _log.Error));
        var system = await Task.Run(() => MeetingRecordingCompression.ToM4a(meeting.SystemAudioPath, _log.Error));
        if (microphone == meeting.MicrophoneAudioPath && system == meeting.SystemAudioPath) return meeting;
        meeting = meeting with { MicrophoneAudioPath = microphone, SystemAudioPath = system };
        SaveMeeting(meeting);
        return meeting;
    }

    private async Task<PersistedMeeting> FinalizeMeetingAsync(
        PersistedMeeting meeting,
        CancellationToken cancellationToken,
        string? lockedTemplate = null)
    {
        // The template locked at Stop, else one the meeting already carries (recovery), else the default.
        var templateName = MeetingSummaryService.NormalizeTemplateName(lockedTemplate
            ?? (string.IsNullOrWhiteSpace(meeting.TemplateName) ? _settings.MeetingSummaryTemplate : meeting.TemplateName));
        if (string.IsNullOrWhiteSpace(meeting.Transcript))
        {
            SaveMeeting(meeting);
            return meeting;
        }

        // Persist the raw recognition result before optional cleanup or network-backed notes.
        // Cancellation/failure of either must never discard a completed transcription.
        SaveMeeting(meeting);
        var transcript = await _pipeline.PrepareImportedTranscriptAsync(
            meeting.Transcript,
            _settings.EnableLocalCleanup && _settings.CleanupSource == "local",
            _library.LoadDictionary(),
            cancellationToken,
            removeFillerWords: _settings.RemoveFillerWords);
        meeting = meeting with { Transcript = transcript, WordCount = CountWords(transcript) };
        SaveMeeting(meeting);
        var summarySettings = _settings with { MeetingSummaryTemplate = templateName };
        var template = _library.LoadMeetingTemplates().FirstOrDefault(item =>
            string.Equals(item.Name, templateName, StringComparison.OrdinalIgnoreCase));
        if (template is not null) summarySettings = summarySettings with { MeetingSummaryPromptOverride = template.Prompt };

        var generatedTitle = await MeetingSummaryService.CreateTitleAsync(meeting, summarySettings, cancellationToken);
        var result = await MeetingSummaryService.CreateSummaryResultAsync(
            meeting.Transcript,
            generatedTitle,
            summarySettings,
            cancellationToken,
            manualNotes: meeting.ManualNotes,
            previousMeetingNotes: _library.Predecessor(meeting.Id) is { } predecessor ? MeetingContinuation.CarriedNotes(predecessor) : null,
            participantNames: MeetingParticipants.Load(_library.Profile.RootDirectory, meeting.Id).Select(person => person.Name));
        // Title/notes generation can take minutes; apply generated fields onto the latest saved copy
        // so a title, notes, or folder edited meanwhile in the detail page are not overwritten.
        var latest = _library.FindMeeting(meeting.Id) ?? meeting;
        var notes = result is { UsedLocalFallback: true, SafeFailureReason: { } reason }
            ? MeetingSummaryService.SummaryFailureNotes(meeting.Transcript, generatedTitle, result.Provider, reason)
            : result.Summary;
        var finalized = MeetingNotesComposer.ApplyResummarization(
            latest,
            notes,
            templateName,
            generatedTitle);
        SaveMeeting(finalized);
        if (finalized.SessionState == MeetingSessionState.Completed &&
            (_settings.PostMeetingHookEnabled || _settings.AutoExportMarkdownEnabled))
        {
            var item = new MeetingItem(finalized.Id, finalized.Title, finalized.CreatedAt, finalized.Transcript,
                finalized.Summary, finalized.SourcePath, finalized.ModelProfile, finalized.DurationMs, finalized.FolderId,
                finalized.WordCount, finalized.TemplateName, finalized.SpeakerAliases, finalized.HealthWarnings,
                finalized.SessionState, finalized.MicrophoneAudioPath, finalized.SystemAudioPath,
                finalized.SystemCaptureMode, finalized.RecoveredFromInterruption,
                ManualNotes: finalized.ManualNotes, TitleIsManual: finalized.TitleIsManual, AutomationResult: finalized.AutomationResult);
            var automation = await new PostMeetingAutomationService().RunAsync(item,
                PostMeetingAutomationOptions.FromSettings(_settings),
                finalized.RecoveredFromInterruption ? PostMeetingCompletionEvent.RecoveryCompleted : PostMeetingCompletionEvent.RecordingCompleted,
                cancellationToken);
            finalized = (_library.FindMeeting(finalized.Id) ?? finalized) with { AutomationResult = automation };
            SaveMeeting(finalized);
        }
        return finalized;
    }

    private static string BuildLiveText(LiveTranscriptSnapshot snapshot)
    {
        var committed = snapshot.CommittedText;
        var partial = string.Join(
            Environment.NewLine,
            new[]
            {
                string.IsNullOrWhiteSpace(snapshot.PartialMicrophone) ? null : $"You: {snapshot.PartialMicrophone}",
                string.IsNullOrWhiteSpace(snapshot.PartialSystem) ? null : $"Others: {snapshot.PartialSystem}"
            }.Where(line => line is not null));
        return string.Join(Environment.NewLine, new[] { committed, partial }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static int CountWords(string text) => LibraryMetrics.CountWords(text);

    private async Task RefreshSettingsAsync(CancellationToken cancellationToken)
    {
        _settings = _settingsContext.Load();
        await _transcription.SwitchModelAsync(_settings.FinalMeetingModelId, cancellationToken);
    }

    private async Task<T> RunOperationAsync<T>(Func<Task<T>> operation)
    {
        ThrowIfDisposed();
        if (Interlocked.CompareExchange(ref _operationBusy, 1, 0) != 0)
            throw new InvalidOperationException("A meeting operation is already in progress.");
        RaiseChanged();
        try { return await operation(); }
        finally
        {
            Interlocked.Exchange(ref _operationBusy, 0);
            RaiseChanged();
        }
    }

    private void RaiseChanged()
    {
        if (_dispatcher is null) Changed?.Invoke(this, EventArgs.Empty);
        else _dispatcher.TryEnqueue(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
