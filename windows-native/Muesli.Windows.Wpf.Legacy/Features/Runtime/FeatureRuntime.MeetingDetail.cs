using System.Windows;
using Muesli.Windows.Services;

namespace Muesli.Windows;

public sealed partial class FeatureRuntime
{
    private TranscriptEditSession? _transcriptEditSession;
    private string _transcriptEditDraft = "";
    private bool _isEditingTranscript;
    private RetranscriptionCandidate? _pendingRetranscriptionCandidate;
    private string _retranscriptionStatus = "";

    public string SelectedMeetingTitle
    {
        get => _selectedMeeting?.Title ?? "";
        set
        {
            if (_selectedMeeting is null || !MeetingTitleService.IsAcceptableManualTitle(value)) return;
            if (string.Equals(_selectedMeeting.Title, value.Trim(), StringComparison.Ordinal)) return;
            UpdateSelectedMeeting(meeting => meeting with { Title = value.Trim(), TitleIsManual = true });
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedMeetingTitleOwnership));
        }
    }

    public string SelectedMeetingTitleOwnership => _selectedMeeting?.TitleIsManual == true
        ? "Your title · kept when notes are regenerated"
        : "Generated title · replaced when notes are regenerated";

    public string SelectedMeetingManualNotes
    {
        get => _selectedMeeting?.ManualNotes ?? "";
        set
        {
            if (_selectedMeeting is null) return;
            var trimmed = value?.Trim() ?? "";
            if (string.Equals(_selectedMeeting.ManualNotes, trimmed, StringComparison.Ordinal)) return;
            UpdateSelectedMeeting(meeting => meeting with { ManualNotes = trimmed });
            OnPropertyChanged();
        }
    }

    public string SelectedMeetingMetadata => _selectedMeeting?.Metadata ?? "";

    public bool IsEditingTranscript
    {
        get => _isEditingTranscript;
        private set
        {
            if (!SetField(ref _isEditingTranscript, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsNotEditingTranscript));
            OnPropertyChanged(nameof(CanAcceptRetranscriptionCandidate));
            OnPropertyChanged(nameof(CanRejectRetranscriptionCandidate));
        }
    }

    public bool IsNotEditingTranscript => !IsEditingTranscript;

    public string TranscriptEditDraft
    {
        get => _transcriptEditDraft;
        set => SetField(ref _transcriptEditDraft, value ?? "");
    }

    public RetranscriptionCandidate? PendingRetranscriptionCandidate
    {
        get => _pendingRetranscriptionCandidate;
        private set
        {
            if (!SetField(ref _pendingRetranscriptionCandidate, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasRetranscriptionCandidate));
            OnPropertyChanged(nameof(CanAcceptRetranscriptionCandidate));
            OnPropertyChanged(nameof(CanRejectRetranscriptionCandidate));
        }
    }

    public bool HasRetranscriptionCandidate => PendingRetranscriptionCandidate is not null;
    public bool CanAcceptRetranscriptionCandidate => PendingRetranscriptionCandidate is { Status: RetranscriptionCandidateStatus.Ready } && !IsEditingTranscript;
    public bool CanRejectRetranscriptionCandidate => PendingRetranscriptionCandidate is not null && !IsEditingTranscript;

    public string RetranscriptionStatus
    {
        get => _retranscriptionStatus;
        private set => SetField(ref _retranscriptionStatus, value);
    }

    /// <summary>Applies an edit to the selected meeting and persists it, keeping list and detail in step.</summary>
    private void UpdateSelectedMeeting(Func<MeetingItem, MeetingItem> edit)
    {
        if (_selectedMeeting is null) return;
        var index = Meetings.IndexOf(_selectedMeeting);
        var updated = edit(_selectedMeeting);
        if (index >= 0) Meetings[index] = updated;
        _selectedMeeting = updated;
        SaveMeetings();
        RefreshSearchResults();
    }

    public string SelectedMeetingNotes => string.IsNullOrWhiteSpace(_selectedMeeting?.Summary)
        ? ""
        : ApplySpeakerAliasesToNotes(_selectedMeeting.Summary, _activeSpeakerAliases);

    public string SelectedMeetingTemplate
    {
        get => _selectedMeetingTemplate;
        set
        {
            var normalized = NormalizeSummaryTemplateName(value);
            if (SetField(ref _selectedMeetingTemplate, normalized))
            {
                OnPropertyChanged(nameof(SelectedMeetingNotesActionLabel));
            }
        }
    }

    public string SelectedMeetingNotesActionLabel => string.IsNullOrWhiteSpace(_selectedMeeting?.Summary) ? "Generate Notes" : "Regenerate Notes";

    /// <summary>True while a notes request is running, which drives the progress and Cancel affordances.</summary>
    public bool IsSummarizing
    {
        get => _isSummarizing;
        private set
        {
            if (!SetField(ref _isSummarizing, value)) return;
            OnPropertyChanged(nameof(IsNotSummarizing));
            OnPropertyChanged(nameof(CanRetrySummary));
        }
    }

    public bool IsNotSummarizing => !_isSummarizing;
    public bool CanRetrySummary => _summaryRetryAvailable && !_isSummarizing;

    private void EditSelectedMeetingTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (_isVisualPreview || _selectedMeeting is null)
        {
            return;
        }

        var result = _transcriptEditService.BeginTranscriptEdit(_selectedMeeting.Id);
        if (!result.Succeeded || result.Session is null)
        {
            DictationStatus = result.Status;
            _toastNotificationService.Show("Transcript editing unavailable", result.Status, ToastState.Error, 4200);
            return;
        }

        _transcriptEditSession = result.Session;
        TranscriptEditDraft = result.Meeting?.Transcript ?? _selectedMeeting.Transcript;
        IsEditingTranscript = true;
        RetranscriptionStatus = "Editing the current transcript. Save to keep changes, or cancel to restore it.";
        OnPropertyChanged(nameof(CanAcceptRetranscriptionCandidate));
        OnPropertyChanged(nameof(CanRejectRetranscriptionCandidate));
    }

    private void SaveSelectedMeetingTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (_transcriptEditSession is null)
        {
            return;
        }

        var result = _transcriptEditService.SaveTranscriptEdit(_transcriptEditSession, TranscriptEditDraft);
        if (result.Meeting is not null)
        {
            ApplyPersistedMeeting(result.Meeting);
            TranscriptEditDraft = result.Meeting.Transcript;
        }

        if (!result.Succeeded)
        {
            DictationStatus = result.Status;
            RetranscriptionStatus = result.Status;
            _toastNotificationService.Show("Transcript was not saved", result.Status, ToastState.Error, 4200);
            return;
        }

        _transcriptEditSession = null;
        IsEditingTranscript = false;
        DictationStatus = result.Status;
        RetranscriptionStatus = result.Status;
        PromptResummaryIfNeeded(result.RequestsResummary);
    }

    private void CancelSelectedMeetingTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (_transcriptEditSession is null)
        {
            return;
        }

        var result = _transcriptEditService.CancelTranscriptEdit(_transcriptEditSession);
        if (result.Meeting is not null)
        {
            ApplyPersistedMeeting(result.Meeting);
            TranscriptEditDraft = result.Meeting.Transcript;
        }

        _transcriptEditSession = null;
        IsEditingTranscript = false;
        DictationStatus = result.Status;
        RetranscriptionStatus = result.Status;
        if (!result.Succeeded)
        {
            _toastNotificationService.Show("Transcript edit cancelled", result.Status, ToastState.Idle, 3200);
        }
    }

    private async void RetranscribeSelectedMeeting_Click(object sender, RoutedEventArgs e)
    {
        if (_isVisualPreview || _selectedMeeting is null || IsEditingTranscript)
        {
            return;
        }

        RetranscriptionStatus = "Re-transcribing the retained meeting recording. The current transcript will remain unchanged until you accept the candidate.";
        var result = await _transcriptEditService.RetranscribeAsync(_selectedMeeting.Id);
        if (result.Candidate is not null && result.Outcome == RetranscriptionOutcome.CandidateReady)
        {
            PendingRetranscriptionCandidate = result.Candidate;
        }

        RetranscriptionStatus = result.Status;
        DictationStatus = result.Status;
        if (!result.Succeeded)
        {
            _toastNotificationService.Show("Re-transcription did not replace the transcript", result.Status, ToastState.Error, 4200);
        }
    }

    private void AcceptSelectedMeetingCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMeeting is null || PendingRetranscriptionCandidate is not { } candidate)
        {
            return;
        }

        var result = _transcriptEditService.AcceptCandidate(_selectedMeeting.Id, candidate.CandidateId);
        if (result.Meeting is not null)
        {
            ApplyPersistedMeeting(result.Meeting);
            TranscriptEditDraft = result.Meeting.Transcript;
        }

        if (result.Succeeded)
        {
            PendingRetranscriptionCandidate = null;
        }

        RetranscriptionStatus = result.Status;
        DictationStatus = result.Status;
        if (!result.Succeeded)
        {
            _toastNotificationService.Show("Candidate was not accepted", result.Status, ToastState.Error, 4200);
            return;
        }

        PromptResummaryIfNeeded(result.RequestsResummary);
    }

    private void RejectSelectedMeetingCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMeeting is null || PendingRetranscriptionCandidate is not { } candidate)
        {
            return;
        }

        var result = _transcriptEditService.RejectCandidate(_selectedMeeting.Id, candidate.CandidateId);
        if (result.Succeeded)
        {
            PendingRetranscriptionCandidate = null;
        }

        RetranscriptionStatus = result.Status;
        DictationStatus = result.Status;
        if (!result.Succeeded)
        {
            _toastNotificationService.Show("Candidate was not rejected", result.Status, ToastState.Error, 4200);
        }
    }

    private void PromptResummaryIfNeeded(bool requestsResummary)
    {
        if (!requestsResummary || _selectedMeeting is null)
        {
            return;
        }

        var choice = _appServices.Dialogs.Confirm(
            "The generated notes no longer match this transcript. Re-summarize now? Your written notes will stay unchanged.",
            "Re-summarize meeting notes");
        if (choice == MessageBoxResult.Yes)
        {
            _ = GenerateSelectedMeetingNotesAsync();
        }
    }

    private void ApplyPersistedMeeting(PersistedMeeting persisted)
    {
        var item = ToMeetingItem(persisted);
        var index = _selectedMeeting is null ? -1 : Meetings.IndexOf(_selectedMeeting);
        if (index >= 0)
        {
            Meetings[index] = item;
        }

        _selectedMeeting = item;
        _selectedMeetingTemplate = NormalizeSummaryTemplateName(
            string.IsNullOrWhiteSpace(item.TemplateName) ? SelectedSummaryTemplate : item.TemplateName);
        _activeSpeakerAliases = new Dictionary<string, string>(item.SpeakerAliases ?? new Dictionary<string, string>());
        BuildSpeakerAliasPanel();
        BuildMeetingWarningsPanel(item);
        BuildMeetingNotesContent();
        OnPropertyChanged(nameof(SelectedMeetingTitle));
        OnPropertyChanged(nameof(SelectedMeetingTitleOwnership));
        OnPropertyChanged(nameof(SelectedMeetingManualNotes));
        OnPropertyChanged(nameof(SelectedMeetingMetadata));
        OnPropertyChanged(nameof(SelectedMeetingNotes));
        OnPropertyChanged(nameof(SelectedMeetingTemplate));
        OnPropertyChanged(nameof(SelectedMeetingNotesActionLabel));
        OnPropertyChanged(nameof(SelectedMeetingTranscript));
        RefreshSearchResults();
    }

    private static MeetingItem ToMeetingItem(PersistedMeeting meeting) => new(
        meeting.Id,
        meeting.Title,
        meeting.CreatedAt,
        meeting.Transcript,
        meeting.Summary,
        meeting.SourcePath,
        meeting.ModelProfile,
        meeting.DurationMs,
        meeting.FolderId,
        meeting.WordCount,
        meeting.TemplateName,
        meeting.SpeakerAliases,
        MeetingRecordingCoordinator.CleanupHealthWarnings(meeting.HealthWarnings, meeting.Transcript),
        meeting.SessionState,
        meeting.MicrophoneAudioPath,
        meeting.SystemAudioPath,
        meeting.SystemCaptureMode,
        meeting.RecoveredFromInterruption,
        meeting.LivePreviewModelId,
        meeting.LiveTranscriptOwnership,
        meeting.FinalTranscriptOwnerModelId,
        meeting.GapRecoveryModelId,
        meeting.ManualNotes,
        meeting.TitleIsManual,
        meeting.AutomationResult);

    private void CancelSummary_Click(object sender, RoutedEventArgs e)
    {
        _summaryCancellation?.Cancel();
        DictationStatus = "Cancelling notes generation…";
    }

    /// <summary>True while a media import is running, which drives the progress and Cancel affordances.</summary>
    public bool IsImportingMeeting
    {
        get => _isImportingMeeting;
        private set
        {
            if (!SetField(ref _isImportingMeeting, value)) return;
            OnPropertyChanged(nameof(IsNotImportingMeeting));
        }
    }

    public bool IsNotImportingMeeting => !_isImportingMeeting;

    /// <summary>Overall import completion, 0-100, across decode, transcription, cleanup and notes.</summary>
    public double ImportProgressPercent
    {
        get => _importProgressPercent;
        private set => SetField(ref _importProgressPercent, value);
    }

    public string ImportProgressLabel
    {
        get => _importProgressLabel;
        private set => SetField(ref _importProgressLabel, value);
    }

    /// <summary>
    /// True for stages that report no fraction. Parakeet decodes a whole file in one native call,
    /// so the bar must say "working" rather than invent a percentage that never moves.
    /// </summary>
    public bool ImportProgressIsIndeterminate
    {
        get => _importProgressIsIndeterminate;
        private set => SetField(ref _importProgressIsIndeterminate, value);
    }

    private void CancelImport_Click(object sender, RoutedEventArgs e)
    {
        _importCancellation?.Cancel();
        DictationStatus = "Cancelling import…";
        ApplyImportProgress(MeetingImportProgressMapper.Cancelling(ImportProgressPercent));
    }

    private void ReportImportProgress(MeetingImportStage stage, double? fraction) =>
        ApplyImportProgress(MeetingImportProgressMapper.Map(stage, fraction));

    private void ApplyImportProgress(MeetingImportProgress progress)
    {
        ImportProgressLabel = progress.Label;
        ImportProgressPercent = progress.Percent;
        ImportProgressIsIndeterminate = progress.IsIndeterminate;
    }

    private void RetrySummary_Click(object sender, RoutedEventArgs e) => _ = RetrySummaryAsync();

    private async Task RetrySummaryAsync()
    {
        _summaryRetryAvailable = false;
        OnPropertyChanged(nameof(CanRetrySummary));
        await GenerateSelectedMeetingNotesAsync();
    }
}
