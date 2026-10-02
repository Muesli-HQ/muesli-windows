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
            CaptureStatus = MeetingCaptureStatusFormatter.Describe(_coordinator.CaptureSnapshot);
            RaiseChanged();
        };
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

    private void SaveManualNotesCore()
    {
        if (ActiveMeetingId is null) return;
        var meeting = _library.FindMeeting(ActiveMeetingId) ?? new PersistedMeeting
        {
            SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion,
            Id = ActiveMeetingId, Title = _title, CreatedAt = _startedAt,
            ModelProfile = _settings.FinalMeetingModelId, SessionState = State
        };
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

    public IReadOnlyList<RecoverableMeetingSession> RecoverableSessions =>
        _coordinator.DiscoverRecoverableSessions();

    public LiveTranscriptSnapshot? LatestLiveSnapshot { get; private set; }

    public MeetingCaptureSnapshot CaptureSnapshot => _coordinator.CaptureSnapshot;

    public Task StartQuickNoteAsync(CancellationToken cancellationToken = default) =>
        StartMeetingAsync($"Quick Note {DateTime.Now:g}", null, cancellationToken);

    public Task StartMeetingAsync(string title, int? processId, CancellationToken cancellationToken = default) =>
        RunOperationAsync(async () =>
        {
            if (IsRecording || IsPaused) throw new InvalidOperationException("A meeting is already active.");
            await RefreshSettingsAsync(cancellationToken);
            _title = title;
            await StartCoreAsync(processId, cancellationToken);
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
        LiveTranscript = "";
        LatestLiveSnapshot = null;
        ManualNotes = "";
        AreNotesSaved = true;
        ActiveMeetingId = null;
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
        var result = await _coordinator.StopAsync(
            _title,
            _settings.SaveMeetingRecordings,
            cancellationToken);
        var persisted = Persist(result);
        var unchangedSpeech = IsResumingFinishedMeeting && string.IsNullOrWhiteSpace(result.Transcript);
        _coordinator.AcknowledgePersisted(result.MeetingId);
        ActiveMeetingId = null;
        IsSourceMissing = false;
        var meeting = unchangedSpeech ? persisted : await FinalizeMeetingAsync(persisted, cancellationToken);
        LiveTranscript = "";
        LatestLiveSnapshot = null;
        CaptureStatus = MeetingCaptureStatusFormatter.Describe(_coordinator.CaptureSnapshot);
        RaiseChanged();
        return meeting;
    }

    public async Task CancelAsync(bool keepNotes = false)
    {
        ThrowIfDisposed();
        if (IsBusy) throw new InvalidOperationException("Wait for the current meeting operation to finish.");
        if (keepNotes) SaveManualNotesCore();
        var resumed = IsResumingFinishedMeeting;
        await _coordinator.CancelAsync();
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

    public async Task ResumeAsync()
    {
        ThrowIfDisposed();
        await _coordinator.ResumeAsync();
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
        if (recovery.Journal.ResumedMeeting is null && _library.FindMeeting(id) is { } draft && string.IsNullOrWhiteSpace(draft.Transcript))
        {
            if (string.IsNullOrWhiteSpace(draft.ManualNotes)) _library.DeleteMeeting(id);
            else SaveMeeting(draft with { SessionState = MeetingSessionState.NoteOnly });
        }
        RaiseChanged();
        return Task.FromResult(true);
    });

    public Task<PersistedMeeting> ImportAsync(
        string sourcePath,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default) => RunOperationAsync(async () =>
        {
            if (IsRecording || IsPaused) throw new InvalidOperationException("Finish the active meeting before importing media.");
            await RefreshSettingsAsync(cancellationToken);
            return await ImportCoreAsync(sourcePath, progress, cancellationToken);
        });

    private async Task<PersistedMeeting> ImportCoreAsync(
        string sourcePath,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default)
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
            FinalTranscriptOwnerModelId = _transcription.ModelId
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

    private void SaveMeeting(PersistedMeeting meeting)
    {
        var meetings = _library.History.LoadMeetings().ToList();
        meetings.RemoveAll(existing => string.Equals(existing.Id, meeting.Id, StringComparison.Ordinal));
        meetings.Insert(0, meeting);
        _library.History.SaveMeetings(meetings);
    }

    private async Task<PersistedMeeting> FinalizeMeetingAsync(
        PersistedMeeting meeting,
        CancellationToken cancellationToken)
    {
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
            _settings.EnableLocalCleanup,
            _library.LoadDictionary(),
            cancellationToken,
            removeFillerWords: _settings.RemoveFillerWords);
        meeting = meeting with { Transcript = transcript, WordCount = CountWords(transcript) };
        SaveMeeting(meeting);
        var summarySettings = _settings;
        var template = _library.LoadMeetingTemplates().FirstOrDefault(item =>
            string.Equals(item.Name, summarySettings.MeetingSummaryTemplate, StringComparison.OrdinalIgnoreCase));
        if (template is not null) summarySettings = summarySettings with { MeetingSummaryPromptOverride = template.Prompt };

        var generatedTitle = await MeetingSummaryService.CreateTitleAsync(meeting, summarySettings, cancellationToken);
        var result = await MeetingSummaryService.CreateSummaryResultAsync(
            meeting.Transcript,
            generatedTitle,
            summarySettings,
            cancellationToken,
            manualNotes: meeting.ManualNotes,
            previousMeetingNotes: _library.Predecessor(meeting.Id) is { } predecessor ? MeetingContinuation.CarriedNotes(predecessor) : null);
        var finalized = MeetingNotesComposer.ApplyResummarization(
            meeting,
            result.Summary,
            _settings.MeetingSummaryTemplate,
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
            finalized = finalized with { AutomationResult = automation };
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
