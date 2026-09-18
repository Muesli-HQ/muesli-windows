using Muesli.Windows.Services;
using Muesli.Windows.Core.Contracts;

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

    public MeetingSessionState State => _coordinator.State;

    public bool IsRecording => _coordinator.IsRecording;

    public bool IsBusy => _coordinator.IsBusy || Volatile.Read(ref _operationBusy) != 0;

    public bool IsPaused => _coordinator.State == MeetingSessionState.RecoverableInterruption;

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

    private async Task StartCoreAsync(int? processId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        LiveTranscript = "";
        LiveTranscriptionConfiguration? liveConfiguration = null;
        if (!string.IsNullOrWhiteSpace(_settings.LiveMeetingModelId))
        {
            liveConfiguration = new LiveTranscriptionConfiguration(
                _settings.LiveMeetingModelId,
                LiveTranscriptOwnershipDescriptor.ModeFromSettingValue(_settings.LiveTranscriptOwnership),
                _settings.ShowLiveWaveformOnHover);
        }

        await _coordinator.StartAsync(
            microphoneName: _settings.MicrophoneName,
            title: _title,
            retainRecording: _settings.SaveMeetingRecordings,
            targetProcessId: processId,
            cancellationToken: cancellationToken,
            liveConfiguration: liveConfiguration);
        CaptureStatus = MeetingCaptureStatusFormatter.Describe(_coordinator.CaptureSnapshot);
        RaiseChanged();
    }

    public Task<PersistedMeeting> StopAndSaveAsync(CancellationToken cancellationToken = default) =>
        RunOperationAsync(() => StopAndSaveCoreAsync(cancellationToken));

    private async Task<PersistedMeeting> StopAndSaveCoreAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var result = await _coordinator.StopAsync(
            _title,
            _settings.SaveMeetingRecordings,
            cancellationToken);
        var persisted = Persist(result);
        _coordinator.AcknowledgePersisted(result.MeetingId);
        var meeting = await FinalizeMeetingAsync(persisted, cancellationToken);
        LiveTranscript = "";
        LatestLiveSnapshot = null;
        CaptureStatus = MeetingCaptureStatusFormatter.Describe(_coordinator.CaptureSnapshot);
        RaiseChanged();
        return meeting;
    }

    public async Task CancelAsync()
    {
        ThrowIfDisposed();
        await _coordinator.CancelAsync();
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
        _coordinator.AcknowledgePersisted(result.MeetingId);
        var meeting = await FinalizeMeetingAsync(persisted, cancellationToken);
        LiveTranscript = "";
        LatestLiveSnapshot = null;
        CaptureStatus = MeetingCaptureStatusFormatter.Describe(_coordinator.CaptureSnapshot);
        RaiseChanged();
        return meeting;
    }

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
            meeting.Transcript, _settings.EnableLocalCleanup, _library.LoadDictionary(), cancellationToken);
        meeting = meeting with { Transcript = transcript, WordCount = CountWords(transcript) };
        SaveMeeting(meeting);
        var summarySettings = _settings;
        var template = _library.LoadMeetingTemplates().FirstOrDefault(item =>
            string.Equals(item.Name, summarySettings.MeetingSummaryTemplate, StringComparison.OrdinalIgnoreCase));
        if (template is not null) summarySettings = summarySettings with { MeetingSummaryPromptOverride = template.Prompt };

        var generatedTitle = MeetingTitleService.Generate(
            meeting.Transcript,
            meeting.CreatedAt,
            meeting.Title);
        var result = await MeetingSummaryService.CreateSummaryResultAsync(
            meeting.Transcript,
            generatedTitle,
            summarySettings,
            cancellationToken);
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

    private static int CountWords(string text) => text.Split(
        (char[]?)null,
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

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
