using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.WinUI.ViewModels;

public partial class MeetingDetailViewModel : ObservableObject, IDisposable
{
    private readonly WinUiLibraryContext _library;
    private readonly IClipboardService _clipboard;
    private readonly IFilePickerService _filePickers;
    private readonly IAppDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly WinUiMeetingDetailContext _runtime;
    private readonly WinUiMeetingContext _meetings;
    private PersistedMeeting? _meeting;
    private RetranscriptionCandidate? _candidate;
    private int _disposed;
    private CancellationTokenSource? _notesDebounce;
    private bool _notesPending;

    public MeetingDetailViewModel(
        WinUiLibraryContext library,
        WinUiSettingsContext settings,
        WinUiMeetingContext meetings,
        IClipboardService clipboard,
        IFilePickerService filePickers,
        IAppDialogService dialogs,
        IUiDispatcher dispatcher)
    {
        _library = library;
        _meetings = meetings;
        _clipboard = clipboard;
        _filePickers = filePickers;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _runtime = new WinUiMeetingDetailContext(library, settings);
        _runtime.PlaybackChanged += (_, args) => _dispatcher.TryEnqueue(() => UpdatePlayback(args));
        _meetings.Changed += OnMeetingRuntimeChanged;
        TemplateNames = MeetingSummaryService.BuiltInTemplateNames
            .Concat(library.LoadMeetingTemplates().Select(template => template.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [ObservableProperty] public partial string Title { get; set; } = "Meeting";
    [ObservableProperty] public partial string Metadata { get; private set; } = "";
    // macOS meetingIdentity: calendar date · clock duration · word count, each with its icon.
    [ObservableProperty] public partial string MetadataDate { get; private set; } = "";
    [ObservableProperty] public partial string MetadataDuration { get; private set; } = "";
    [ObservableProperty] public partial string MetadataWords { get; private set; } = "";
    [ObservableProperty] public partial string TitleOwnershipLabel { get; private set; } = "";
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string Transcript { get; set; } = "";
    [ObservableProperty] public partial string ManualNotes { get; set; } = "";
    [ObservableProperty] public partial string StatusMessage { get; private set; } = "";
    [ObservableProperty] public partial bool IsStatusOpen { get; set; }
    [ObservableProperty] public partial bool IsStatusError { get; private set; }
    [ObservableProperty] public partial bool HasMeeting { get; private set; }
    [ObservableProperty] public partial bool HasRecording { get; private set; }
    [ObservableProperty] public partial bool HasGeneratedSummary { get; private set; }
    [ObservableProperty] public partial bool HasTranscriptText { get; private set; }
    [ObservableProperty] public partial bool HasSavedTranscript { get; private set; }
    [ObservableProperty] public partial bool HasManualTitle { get; private set; }
    [ObservableProperty] public partial bool HasHealthWarnings { get; private set; }
    [ObservableProperty] public partial bool ShowNotesTab { get; set; } = true;
    [ObservableProperty] public partial bool IsWorking { get; private set; }
    [ObservableProperty] public partial double WorkProgress { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<string> HealthWarnings { get; private set; } = [];
    [ObservableProperty] public partial bool HasAutomationResult { get; private set; }
    [ObservableProperty] public partial string AutomationStatusLabel { get; private set; } = PostMeetingAutomationDiagnostics.StatusLabel(null);
    [ObservableProperty] public partial string AutomationDiagnostics { get; private set; } = PostMeetingAutomationDiagnostics.NoResultMessage;
    [ObservableProperty] public partial string AutomationRawOutput { get; private set; } = "";
    [ObservableProperty] public partial bool HasAutomationRawOutput { get; private set; }
    [ObservableProperty] public partial string SelectedTemplate { get; set; } = "Standard Meeting Notes";
    [ObservableProperty] public partial IReadOnlyList<MeetingPlaybackTrack> PlaybackTracks { get; private set; } = [];
    [ObservableProperty] public partial MeetingPlaybackTrack? SelectedPlaybackTrack { get; set; }
    [ObservableProperty] public partial string PlaybackStatus { get; private set; } = "No recording";
    [ObservableProperty] public partial IReadOnlyList<SpeakerAliasItem> SpeakerAliases { get; private set; } = [];
    [ObservableProperty] public partial string AliasSource { get; set; } = "";
    [ObservableProperty] public partial string AliasReplacement { get; set; } = "";
    [ObservableProperty] public partial string CandidateTranscript { get; private set; } = "";
    [ObservableProperty] public partial bool HasCandidate { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<MeetingFolderOption> FolderOptions { get; private set; } = [];
    [ObservableProperty] public partial MeetingFolderOption? SelectedFolder { get; set; }
    [ObservableProperty] public partial IReadOnlyList<double> WaveformPeaks { get; private set; } = [];
    [ObservableProperty] public partial bool IsPlaying { get; private set; }
    [ObservableProperty] public partial double PlaybackSeconds { get; set; }
    [ObservableProperty] public partial double PlaybackDurationSeconds { get; private set; }

    /// <summary>
    /// Raised after this page changes something the shell rail counts — a folder move or a
    /// delete. The page turns it into the existing <c>RefreshMeetingFolders</c> call so the rail
    /// recomputes through <c>MeetingFolderListItem.BuildTree</c> rather than keeping the numbers
    /// it had before. No count is calculated here.
    /// </summary>
    public event EventHandler? LibraryChanged;

    /// <summary>Raised when the meeting this page was showing no longer exists.</summary>
    public event EventHandler? MeetingRemoved;
    public event EventHandler? RecordingStarted;
    public string MeetingId => _meeting?.Id ?? "";
    [ObservableProperty] public partial IReadOnlyList<PersistedMeeting> RelatedMeetings { get; private set; } = [];
    public bool HasRelatedMeetings => RelatedMeetings.Count > 0;

    // macOS threadBreadcrumb: the predecessor link, "Thread · N meetings", then the follow-ups.
    [ObservableProperty] public partial MeetingThreadLink? ThreadPredecessor { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<MeetingThreadLink> ThreadSuccessors { get; private set; } = [];
    [ObservableProperty] public partial string ThreadCountLabel { get; private set; } = "";
    public bool HasThread => ThreadCountLabel.Length > 0;
    public bool HasThreadPredecessor => ThreadPredecessor is not null;
    public bool HasManySuccessors => ThreadSuccessors.Count > 1;
    public string FollowUpsHeading => $"Follow-ups ({ThreadSuccessors.Count})";

    /// <summary>macOS editing mode: Edit Notes / Edit Transcript in the "…" menu, Done to save.</summary>
    [ObservableProperty] public partial bool IsEditing { get; private set; }
    public string EditLabel => IsEditing ? "Done" : ShowNotesTab ? "Edit Notes" : "Edit Transcript";
    public bool IsEditingTranscript => IsEditing && !ShowNotesTab;
    public bool ShowTranscriptFeed => !IsEditingTranscript;

    /// <summary>The notes document: the generated notes, or the raw transcript under the title (macOS rawTranscriptNotes).</summary>
    public string NotesMarkdown => HasGeneratedSummary
        ? Summary
        : HasSavedTranscript ? $"# {Title}\n\n## Raw Transcript\n\n{Transcript}" : "";
    public bool ShowNotesDocument => NotesMarkdown.Length > 0 && !(IsEditing && HasGeneratedSummary);
    public bool ShowSummaryEditor => IsEditing && HasGeneratedSummary;
    public bool HasTrackChoice => PlaybackTracks.Count > 1;
    public bool ShowTranscriptCta => !HasGeneratedSummary && HasSavedTranscript && ShowGeneratedNotes;
    public bool ShowManualNotes => IsEditing || !string.IsNullOrWhiteSpace(ManualNotes);
    /// <summary>macOS shows "Apply Template" for a chosen-but-unapplied template; here choosing one applies it.</summary>
    public string SummaryActionLabel => "Re-summarize";
    public string TranscriptCta => $"Use {SummaryActionLabel} to turn this raw transcript into AI meeting notes and a cleaned-up title.";
    public string ExportCurrentLabel => ShowNotesTab ? "Export Notes" : "Export Transcript";

    /// <summary>The folder pill: the folder's path, or "Add to folder".</summary>
    [ObservableProperty] public partial string FolderLabel { get; private set; } = "Add to folder";
    public bool HasFolder => _meeting?.FolderId is not null;
    public string? FolderId => _meeting?.FolderId;
    public string? RecordingPath => _meeting is null ? null
        : new[] { _meeting.MicrophoneAudioPath, _meeting.SystemAudioPath, _meeting.SourcePath }.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    public bool HasRecordingFile => RecordingPath is not null;
    public bool CanContinueMeeting => CanWork && _meeting is not null && MeetingContinuation.CanContinue(_meeting)
        && !_meetings.IsRecording && !_meetings.IsPaused && !_meetings.IsBusy;
    public bool CanFollowUp => CanContinueMeeting && _library.SupportsMeetingThreads;

    [RelayCommand(CanExecute = nameof(CanContinueMeeting))]
    private async Task ResumeFinishedAsync() => await ContinueMeetingAsync(false);

    [RelayCommand(CanExecute = nameof(CanFollowUp))]
    private async Task StartFollowUpAsync() => await ContinueMeetingAsync(true);

    private async Task ContinueMeetingAsync(bool followUp)
    {
        if (_meeting is null) return;
        Save();
        _runtime.Stop();
        SetWorking(true, followUp ? "Starting follow-up…" : "Resuming meeting…");
        try
        {
            if (followUp) await _meetings.StartFollowUpAsync(_meeting.Id);
            else await _meetings.ResumeFinishedAsync(_meeting.Id);
            RecordingStarted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) { ShowStatus($"Could not start recording: {exception.Message}", true); }
        finally { SetWorking(false); }
    }

    private void OnMeetingRuntimeChanged(object? sender, EventArgs args) => _dispatcher.TryEnqueue(() =>
    {
        if (Volatile.Read(ref _disposed) == 0) { NotifyCommands(); NotifyPresentation(); }
    });

    public IReadOnlyList<string> TemplateNames { get; }
    public bool CanWork => HasMeeting && !IsWorking;

    /// <summary>
    /// Generating notes needs a *saved* transcript, not a draft in the editor: the generator reads
    /// the stored meeting. Without this gate the button stayed enabled on a transcript-less meeting
    /// and pressing it did nothing at all — no progress, no message, no error.
    /// </summary>
    public bool CanGenerateSummary => CanWork && HasSavedTranscript;

    public string GenerateSummaryHint => HasSavedTranscript
        ? "Write meeting notes from this transcript"
        : "Add and save a transcript first — notes are written from the transcript, never invented.";
    public bool IsMissing => !HasMeeting;
    public bool ShowTranscriptTab => HasMeeting && !ShowNotesTab;
    public bool ShowNotesContent => HasMeeting && ShowNotesTab;
    public bool ShowGeneratedNotes => HasMeeting && _meeting?.SessionState == MeetingSessionState.Completed;
    public bool HasWaveform => WaveformPeaks.Count > 0;
    public double PlaybackProgress => PlaybackDurationSeconds > 0 ? PlaybackSeconds / PlaybackDurationSeconds : 0;
    public string PlayPauseGlyph => IsPlaying ? "\uE769" : "\uE768";
    public string PlayPauseLabel => IsPlaying ? "Pause recording" : "Play recording";
    public string PlaybackTimeLabel =>
        $"{FormatTime(TimeSpan.FromSeconds(PlaybackSeconds))} / {FormatTime(TimeSpan.FromSeconds(PlaybackDurationSeconds))}";

    public void Load(string meetingId)
    {
        FlushNotes();
        _meeting = _library.FindMeeting(meetingId);
        HasMeeting = _meeting is not null;
        if (_meeting is null)
        {
            Title = "Meeting not found";
            Metadata = "This meeting may have been deleted.";
            TitleOwnershipLabel = "";
            HasManualTitle = false;
            HasGeneratedSummary = false;
            HasTranscriptText = false;
            HasSavedTranscript = false;
            HasHealthWarnings = false;
            HealthWarnings = [];
            ApplyAutomationDiagnostics(null);
            HasRecording = false;
            ClearCandidate();
            NotifyCommands();
            NotifyPresentation();
            return;
        }

        IsEditing = false;
        ApplyMeeting(_meeting);
        // macOS opens the notes when there are generated notes and the transcript otherwise.
        ShowNotesTab = HasGeneratedSummary || !HasSavedTranscript;
        var recovery = _runtime.RecoverCandidate(_meeting.Id);
        if (recovery?.Candidate is { } recovered)
        {
            SetCandidate(recovered);
            ShowStatus(recovery.Status);
        }
        NotifyCommands();
    }

    partial void OnHasMeetingChanged(bool value) => NotifyPresentation();

    partial void OnShowNotesTabChanged(bool value) => NotifyPresentation();
    partial void OnIsEditingChanged(bool value) => NotifyPresentation();
    partial void OnSummaryChanged(string value) => OnPropertyChanged(nameof(NotesMarkdown));

    partial void OnTitleChanged(string value)
    {
        OnPropertyChanged(nameof(NotesMarkdown));
        // macOS saves the title as it is edited; the editor commits on Enter or leaving the field.
        if (_meeting is null || _applying) return;
        var title = value.Trim();
        if (title.Length == 0 || title == _meeting.Title) return;
        var updated = MeetingNotesComposer.ApplyManualTitle(_meeting, title);
        if (_library.UpdateMeeting(updated)) { _meeting = updated; LibraryChanged?.Invoke(this, EventArgs.Empty); }
        else ShowStatus("The meeting no longer exists.", true);
    }

    private bool _applying;

    /// <summary>The folder popover: null clears the folder (macOS toggles a checked folder off).</summary>
    public void MoveToFolder(string? folderId)
    {
        if (_meeting is null || _meeting.FolderId == folderId) return;
        var updated = _meeting with { FolderId = folderId };
        if (!_library.UpdateMeeting(updated)) { ShowStatus("The meeting no longer exists.", true); return; }
        _meeting = updated;
        ApplyFolder(updated);
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CreateFolderAndMove(string name)
    {
        try { MoveToFolder(_library.SaveMeetingFolder(null, name).Id); }
        catch (Exception exception) { ShowStatus($"Could not create the folder: {exception.Message}", true); }
    }

    private void ApplyFolder(PersistedMeeting meeting)
    {
        FolderLabel = meeting.FolderId is { } id
            ? MeetingsPageViewModel.FolderPath(_library.History.LoadMeetingFolders(), id) ?? "Add to folder"
            : "Add to folder";
        OnPropertyChanged(nameof(HasFolder));
        OnPropertyChanged(nameof(FolderId));
    }

    /// <summary>Edit Notes / Edit Transcript, then Done saves what was edited.</summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task ToggleEditingAsync()
    {
        if (!IsEditing) { IsEditing = true; return; }
        IsEditing = false;
        if (_meeting is null) return;
        if (ShowNotesTab)
        {
            FlushNotes();
            if (HasGeneratedSummary && Summary != (_meeting.Summary ?? ""))
            {
                var updated = _meeting with { Summary = Summary };
                if (_library.UpdateMeeting(updated)) _meeting = updated;
                else ShowStatus("The meeting no longer exists.", true);
            }
            return;
        }
        if (Transcript == SpeakerAliasService.Apply(_meeting.Transcript, _meeting.SpeakerAliases)) return;
        SaveTranscript();
        if (!HasGeneratedSummary || !CanGenerateSummary) return;
        var choice = await _dialogs.ConfirmAsync(
            "Your transcript edits may change the generated notes. Re-summarize now to update them from the edited transcript.",
            "Re-summarize Notes?", "Re-summarize", "Not Now");
        if (choice == AppDialogChoice.Primary) await GenerateSummaryCommand.ExecuteAsync(null);
    }

    /// <summary>The Template submenu: choosing a template re-summarizes with it.</summary>
    public async Task ApplyTemplateAsync(string template)
    {
        SelectedTemplate = template;
        if (CanGenerateSummary) await GenerateSummaryCommand.ExecuteAsync(null);
    }

    /// <summary>macOS Copy: the current tab with the export metadata header.</summary>
    [RelayCommand]
    private async Task CopyCurrentAsync(CancellationToken cancellationToken)
    {
        if (_meeting is null) return;
        try
        {
            await _clipboard.SetTextAsync(MeetingExportFormatter.BuildMarkdown(ToMeetingItem(_meeting),
                ShowNotesTab ? MeetingExportMode.Notes : MeetingExportMode.Transcript, _meeting.SpeakerAliases), cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ShowStatus($"Could not copy: {WinUiClipboardService.DescribeFailure(exception)}", true); }
    }

    [RelayCommand] private Task ExportCurrentAsync(CancellationToken cancellationToken) =>
        ExportAsync(ShowNotesTab ? MeetingExportMode.Notes : MeetingExportMode.Transcript, cancellationToken);

    [RelayCommand] private Task ExportFullAsync(CancellationToken cancellationToken) =>
        ExportAsync(MeetingExportMode.FullMeeting, cancellationToken);

    /// <summary>macOS Show Recording: reveals the retained audio in Explorer.</summary>
    [RelayCommand]
    private void ShowRecording()
    {
        if (RecordingPath is not { } path) return;
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception exception) { ShowStatus($"Could not show the recording: {exception.Message}", true); }
    }

    partial void OnManualNotesChanged(string value)
    {
        if (_meeting is null || value == _meeting.ManualNotes || Volatile.Read(ref _disposed) != 0) return;
        // Persist after a typing pause, not per keystroke.
        _notesPending = true;
        _notesDebounce?.Cancel();
        var debounce = _notesDebounce = new CancellationTokenSource();
        _ = Task.Delay(700, debounce.Token).ContinueWith(
            _ => _dispatcher.TryEnqueue(() => { if (!debounce.IsCancellationRequested) FlushNotes(); }),
            TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    // Writes pending notes now. Pass the freshest copy of the meeting when one was just reloaded.
    private void FlushNotes(PersistedMeeting? latest = null)
    {
        _notesDebounce?.Cancel();
        if (!_notesPending || (latest ?? _meeting) is not { } target) return;
        _notesPending = false;
        try
        {
            var updated = MeetingNotesComposer.ApplyManualNotes(target, ManualNotes);
            if (_library.UpdateMeeting(updated)) _meeting = updated;
            else ShowStatus("The meeting no longer exists. Your writing has not been saved.", true);
        }
        catch (Exception exception) { ShowStatus($"Could not save notes: {exception.Message}", true); }
    }

    partial void OnTranscriptChanged(string value) =>
        HasTranscriptText = !string.IsNullOrWhiteSpace(value);

    partial void OnHasSavedTranscriptChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGenerateSummary));
        OnPropertyChanged(nameof(GenerateSummaryHint));
        GenerateSummaryCommand.NotifyCanExecuteChanged();
    }

    partial void OnPlaybackSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(PlaybackTimeLabel));
        OnPropertyChanged(nameof(PlaybackProgress));
    }

    partial void OnPlaybackDurationSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(PlaybackTimeLabel));
        OnPropertyChanged(nameof(PlaybackProgress));
    }

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayPauseGlyph));
        OnPropertyChanged(nameof(PlayPauseLabel));
    }

    [RelayCommand(CanExecute = nameof(CanWork))]
    private void Save()
    {
        if (_meeting is null) return;
        var title = string.IsNullOrWhiteSpace(Title) ? "Untitled meeting" : Title.Trim();
        var updated = MeetingNotesComposer.ApplyManualNotes(
            title == _meeting.Title ? _meeting : MeetingNotesComposer.ApplyManualTitle(_meeting, title), ManualNotes) with
        {
            FolderId = string.IsNullOrWhiteSpace(SelectedFolder?.Id) ? null : SelectedFolder.Id
        };
        if (_library.UpdateMeeting(updated))
        {
            _meeting = updated;
            ApplyMeeting(updated, keepTranscriptDraft: true);
            ShowStatus("Meeting changes saved.");
            // A folder move changes what the rail's folder rows count.
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        else ShowStatus("The meeting no longer exists.", true);
    }

    [RelayCommand(CanExecute = nameof(CanWork))]
    private void SaveTranscript()
    {
        if (_meeting is null) return;
        var result = _runtime.SaveTranscript(_meeting, Transcript);
        if (result.Meeting is not null)
        {
            _meeting = result.Meeting;
            ApplyMeeting(_meeting);
        }
        ShowStatus(result.Status, !result.Succeeded);
    }

    [RelayCommand(CanExecute = nameof(CanGenerateSummary))]
    private async Task GenerateSummaryAsync(CancellationToken cancellationToken)
    {
        if (_meeting is null || string.IsNullOrWhiteSpace(_meeting.Transcript)) return;
        SetWorking(true, "Generating meeting notes…");
        try
        {
            var result = await _runtime.GenerateSummaryAsync(_meeting, SelectedTemplate, cancellationToken);
            _meeting = _library.FindMeeting(_meeting.Id);
            if (_meeting is not null) ApplyMeeting(_meeting);
            var fellBack = result.UsedLocalFallback && result.SafeFailureReason is not null;
            ShowStatus(fellBack
                ? $"Notes generated locally because the selected provider was unavailable ({result.SafeFailureReason})."
                : $"Notes generated with {result.Provider}.", isError: fellBack);
        }
        catch (OperationCanceledException) { ShowStatus("Summary generation cancelled."); }
        catch (Exception exception) { ShowStatus($"Could not generate notes: {exception.Message}", true); }
        finally { SetWorking(false); }
    }

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task RetranscribeAsync(CancellationToken cancellationToken)
    {
        if (_meeting is null) return;
        SetWorking(true, "Re-transcribing retained audio…");
        try
        {
            var progress = new Progress<TranscriptionProgress>(value =>
                WorkProgress = Math.Clamp((value.Fraction ?? 0) * 100, 0, 100));
            var result = await _runtime.RetranscribeAsync(_meeting.Id, progress, cancellationToken);
            if (result.Candidate is { } candidate) SetCandidate(candidate);
            ShowStatus(result.Status, !result.Succeeded);
        }
        catch (OperationCanceledException) { ShowStatus("Re-transcription cancelled."); }
        catch (Exception exception) { ShowStatus($"Re-transcription failed: {exception.Message}", true); }
        finally { SetWorking(false); }
    }

    [RelayCommand]
    private void AcceptCandidate()
    {
        if (_meeting is null || _candidate is null) return;
        var result = _runtime.AcceptCandidate(_meeting.Id, _candidate.CandidateId);
        if (result.Meeting is not null)
        {
            _meeting = result.Meeting;
            ApplyMeeting(_meeting);
        }
        ClearCandidate();
        ShowStatus(result.Status, !result.Succeeded);
    }

    [RelayCommand]
    private void RejectCandidate()
    {
        if (_meeting is null || _candidate is null) return;
        var result = _runtime.RejectCandidate(_meeting.Id, _candidate.CandidateId);
        ClearCandidate();
        ShowStatus(result.Status, !result.Succeeded);
    }

    [RelayCommand]
    private async Task CopyTranscriptAsync(CancellationToken cancellationToken)
    {
        if (_meeting is null) return;
        try
        {
            // macOS copies with the export metadata header (title, date, duration, words).
            await _clipboard.SetTextAsync(MeetingExportFormatter.BuildMarkdown(
                ToMeetingItem(_meeting), MeetingExportMode.Transcript, _meeting.SpeakerAliases), cancellationToken);
            ShowStatus("Transcript copied to the clipboard.");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ShowStatus($"Could not copy the transcript: {WinUiClipboardService.DescribeFailure(exception)}", true); }
    }

    [RelayCommand]
    private Task ExportMarkdownAsync(CancellationToken cancellationToken) => ExportAsync(MeetingExportMode.FullMeeting, cancellationToken);

    private async Task ExportAsync(MeetingExportMode mode, CancellationToken cancellationToken)
    {
        if (_meeting is null) return;
        try
        {
            var model = ToMeetingItem(_meeting);
            var suggestedName = Path.ChangeExtension(
                MeetingExportFormatter.SuggestFilename(model, mode), ".md");
            // PDF is offered only while the QuestPDF Community-license eligibility gate is approved.
            var picked = await _filePickers.PickSaveFileAsync(
                new FilePickerRequest("Export meeting", MeetingDocumentWriter.ApprovedExportExtensions, suggestedName), cancellationToken);
            if (picked is null) return;
            var markdown = MeetingExportFormatter.BuildMarkdown(model, mode, _meeting.SpeakerAliases);
            await Task.Run(() => MeetingDocumentWriter.Write(markdown, picked.Path), cancellationToken);
            ShowStatus($"Meeting exported to {picked.DisplayName}.");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ShowStatus($"Could not export the meeting: {exception.Message}", true); }
    }

    [RelayCommand]
    private async Task DeleteMeetingAsync(CancellationToken cancellationToken)
    {
        if (_meeting is null) return;
        // Delete now sits behind the "…" menu, so the dialog is the last place the user can check
        // that the menu belonged to the meeting they meant. It names the record, the way the
        // dictation and meeting list confirmations do.
        var choice = await _dialogs.ConfirmAsync(
            "Are you sure you want to delete this meeting? Saved notes, transcript, and any retained recording will be removed.",
            "Delete Meeting", "Delete", "Cancel", cancellationToken);
        if (choice != AppDialogChoice.Primary) return;
        if (_library.DeleteMeeting(_meeting.Id))
        {
            _meeting = null;
            HasMeeting = false;
            Title = "Meeting not found";
            Metadata = "This meeting may have been deleted.";
            TitleOwnershipLabel = "";
            HasManualTitle = false;
            HasGeneratedSummary = false;
            HasTranscriptText = false;
            HasSavedTranscript = false;
            HasHealthWarnings = false;
            HealthWarnings = [];
            ApplyAutomationDiagnostics(null);
            HasRecording = false;
            ClearCandidate();
            NotifyCommands();
            ShowStatus("Meeting deleted.");
            LibraryChanged?.Invoke(this, EventArgs.Empty);
            MeetingRemoved?.Invoke(this, EventArgs.Empty);
        }
        else ShowStatus("The meeting was already removed.", true);
    }

    [RelayCommand]
    private async Task LoadPlaybackTrackAsync(CancellationToken cancellationToken)
    {
        if (SelectedPlaybackTrack is null) return;
        try
        {
            await _runtime.LoadTrackAsync(SelectedPlaybackTrack, cancellationToken);
            PlaybackStatus = $"Ready · {SelectedPlaybackTrack.Label}";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ShowStatus($"Could not load recording: {exception.Message}", true); }
    }

    [RelayCommand] private void Play() { try { _runtime.Play(); } catch (Exception exception) { ShowStatus(exception.Message, true); } }
    [RelayCommand] private void Pause() => _runtime.Pause();
    [RelayCommand] private void StopPlayback() => _runtime.Stop();

    /// <summary>macOS player: one play/pause toggle.</summary>
    [RelayCommand] private void TogglePlayback() { if (IsPlaying) Pause(); else Play(); }

    public void SeekTo(double seconds)
    {
        if (PlaybackDurationSeconds <= 0) return;
        _runtime.Seek(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, PlaybackDurationSeconds)));
    }

    [RelayCommand]
    private void AddAlias()
    {
        if (_meeting is null || string.IsNullOrWhiteSpace(AliasSource) || string.IsNullOrWhiteSpace(AliasReplacement)) return;
        try
        {
            _meeting = _runtime.SaveSpeakerAlias(_meeting, AliasSource, AliasReplacement);
            AliasSource = "";
            AliasReplacement = "";
            ApplyMeeting(_meeting);
            ShowStatus("Speaker name saved.");
        }
        catch (Exception exception) { ShowStatus($"Could not save speaker name: {exception.Message}", true); }
    }

    [RelayCommand]
    private void DeleteAlias(SpeakerAliasItem? alias)
    {
        if (_meeting is null || alias is null) return;
        _meeting = _runtime.DeleteSpeakerAlias(_meeting, alias.Source);
        ApplyMeeting(_meeting);
        ShowStatus("Speaker name removed.");
    }

    partial void OnSelectedPlaybackTrackChanged(MeetingPlaybackTrack? value)
    {
        if (value is not null) LoadPlaybackTrackCommand.Execute(null);
    }

    private void ApplyAutomationDiagnostics(PersistedMeeting? meeting)
    {
        var result = meeting?.AutomationResult;
        HasAutomationResult = PostMeetingAutomationDiagnostics.HasResult(result);
        AutomationStatusLabel = PostMeetingAutomationDiagnostics.StatusLabel(result);
        AutomationDiagnostics = PostMeetingAutomationDiagnostics.Format(result);
        AutomationRawOutput = PostMeetingAutomationDiagnostics.RawOutput(result);
        HasAutomationRawOutput = !string.IsNullOrWhiteSpace(AutomationRawOutput);
    }

    private void ApplyMeeting(PersistedMeeting meeting, bool keepTranscriptDraft = false)
    {
        // A reload must not overwrite notes typed during the debounce window.
        if (_notesPending && meeting.Id == _meeting?.Id)
        {
            FlushNotes(meeting);
            meeting = _meeting ?? meeting;
        }
        RelatedMeetings = _library.RelatedMeetings(meeting.Id);
        OnPropertyChanged(nameof(HasRelatedMeetings));
        ApplyThread(meeting);
        _applying = true;
        try { Title = meeting.Title; }
        finally { _applying = false; }
        Metadata = $"{meeting.CreatedAt.ToLocalTime():MMM d, yyyy · h:mm tt} · {FormatDuration(meeting.DurationMs)} · {meeting.ModelProfile}";
        MetadataDate = meeting.CreatedAt.ToLocalTime().ToString("d MMM yyyy 'at' h:mm:ss tt");
        MetadataDuration = FormatHeaderDuration(meeting.DurationMs);
        MetadataWords = $"{meeting.WordCount} words";
        HasManualTitle = meeting.TitleIsManual;
        TitleOwnershipLabel = meeting.TitleIsManual
            ? "Your title · kept when notes are regenerated"
            : "";
        HasGeneratedSummary = !string.IsNullOrWhiteSpace(meeting.Summary);
        Summary = meeting.Summary ?? "";
        if (!keepTranscriptDraft) Transcript = SpeakerAliasService.Apply(meeting.Transcript, meeting.SpeakerAliases);
        HasTranscriptText = !string.IsNullOrWhiteSpace(Transcript);
        HasSavedTranscript = !string.IsNullOrWhiteSpace(meeting.Transcript);
        ManualNotes = meeting.ManualNotes;
        HealthWarnings = meeting.HealthWarnings is { Count: > 0 }
            ? meeting.HealthWarnings.Where(warning => !string.IsNullOrWhiteSpace(warning)).ToList()
            : [];
        HasHealthWarnings = HealthWarnings.Count > 0;
        ApplyAutomationDiagnostics(meeting);
        SelectedTemplate = string.IsNullOrWhiteSpace(meeting.TemplateName) ? "Standard Meeting Notes" : meeting.TemplateName;
        SpeakerAliases = meeting.SpeakerAliases
            .OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(pair => new SpeakerAliasItem(pair.Key, pair.Value)).ToList();
        FolderOptions = BuildFolderOptions(_library.ReadSnapshot());
        SelectedFolder = FolderOptions.FirstOrDefault(folder => folder.Id == meeting.FolderId) ?? FolderOptions[0];
        ApplyFolder(meeting);
        PlaybackTracks = _runtime.GetTracks(meeting);
        HasRecording = PlaybackTracks.Count > 0;
        if (SelectedPlaybackTrack is null || !PlaybackTracks.Any(track => track.Path == SelectedPlaybackTrack.Path))
            SelectedPlaybackTrack = PlaybackTracks.FirstOrDefault();
        RefreshWaveform();
        NotifyPresentation();
    }

    private void ApplyThread(PersistedMeeting meeting)
    {
        if (!_library.SupportsMeetingThreads) return;
        var predecessor = _library.Predecessor(meeting.Id);
        var successors = _library.FollowUps(meeting.Id);
        ThreadPredecessor = predecessor is null ? null : MeetingThreadLink.From(predecessor, "Follow-up to: ");
        ThreadSuccessors = successors.Select(item => MeetingThreadLink.From(item, successors.Count == 1 ? "Followed by: " : "")).ToList();
        ThreadCountLabel = predecessor is null && successors.Count == 0 ? "" : $"Thread · {_library.ThreadSize(meeting.Id)} meetings";
        OnPropertyChanged(nameof(HasThread));
        OnPropertyChanged(nameof(HasThreadPredecessor));
        OnPropertyChanged(nameof(HasManySuccessors));
        OnPropertyChanged(nameof(FollowUpsHeading));
    }

    /// <summary>
    /// The folder picker is built from <see cref="MeetingFolderListItem.BuildTree"/>, the same
    /// builder the shell rail and the Meetings page use, so nesting and ordering here are the
    /// numbers and the order those surfaces already show rather than a third hand-rolled loop.
    /// Its leading "All Meetings" row is a *filter*, not a destination, so it is dropped and the
    /// picker opens with "No folder" instead — the only choice that clears a meeting's folder.
    /// No counts are shown: this picker chooses where a meeting goes, and a count next to a
    /// destination would invite the reader to interpret it under some rule of its own.
    /// </summary>
    private static IReadOnlyList<MeetingFolderOption> BuildFolderOptions(WinUiLibrarySnapshot snapshot)
    {
        var options = new List<MeetingFolderOption> { new("", "No folder", "", "No folder") };
        foreach (var folder in MeetingFolderListItem.BuildTree(snapshot))
        {
            if (folder.IsAllMeetings) continue;
            // DisplayName carries nesting as leading spaces, which the drop-down shows and UIA
            // trims away. Neither survives into the *closed* box, where only the leaf name is
            // drawn — so a nested folder also carries its parent as a dimmed suffix, and the
            // announced name spells the parent out. Two folders legitimately named "Notes" under
            // different parents are otherwise identical both on screen and to a screen reader.
            options.Add(new MeetingFolderOption(
                folder.Id,
                folder.DisplayName,
                folder.ParentName is null ? "" : $"in {folder.ParentName}",
                folder.ParentPickerAccessibleName));
        }

        return options;
    }

    private double _playbackAnchorSeconds;
    private long _playbackAnchorTimestamp;

    /// <summary>
    /// Called every rendered frame while playing. The reader's position advances in read-ahead chunks, so
    /// the playhead is extrapolated from the exact position of the last play/pause/seek on a high-resolution
    /// clock, the way video editors drive a playhead.
    /// </summary>
    // ponytail: wall clock vs. audio clock drift is ~0.01%; re-anchoring on every seek/pause keeps it invisible.
    public void RefreshPlaybackPosition()
    {
        if (!IsPlaying) return;
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_playbackAnchorTimestamp).TotalSeconds;
        PlaybackSeconds = Math.Min(_playbackAnchorSeconds + elapsed, PlaybackDurationSeconds);
        PlaybackStatus = $"Playing · {FormatTime(TimeSpan.FromSeconds(PlaybackSeconds))} / {FormatTime(TimeSpan.FromSeconds(PlaybackDurationSeconds))}";
    }

    private void UpdatePlayback(MeetingPlaybackStateChangedEventArgs args)
    {
        PlaybackStatus = args.State == MeetingPlaybackState.Failed
            ? $"Playback failed · {args.Failure}"
            : $"{args.State} · {FormatTime(args.Position)} / {FormatTime(args.Duration)}";
        PlaybackSeconds = args.Position.TotalSeconds;
        PlaybackDurationSeconds = Math.Max(args.Duration.TotalSeconds, 0);
        _playbackAnchorSeconds = PlaybackSeconds;
        _playbackAnchorTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        IsPlaying = args.State == MeetingPlaybackState.Playing;
        RefreshWaveform();
    }

    private void RefreshWaveform()
    {
        // The peak cache stores bar heights (4 + 20 x amplitude, amplitude floored at 0.04); recover amplitudes
        // with that floor as silence, then normalise to the loudest bucket with a 10/255 floor, as macOS
        // RecordingWaveformData does. Without removing the floor a silent recording drew full-height bars.
        var amplitudes = _runtime.WaveformPeaks.Select(height => Math.Clamp(((height - 4) / 20 - 0.04) / 0.96, 0, 1)).ToList();
        var loudest = Math.Max(amplitudes.DefaultIfEmpty(0).Max(), 0.01);
        WaveformPeaks = amplitudes.Select(amplitude => Math.Max(10 / 255d, amplitude / loudest)).ToList();
        OnPropertyChanged(nameof(HasWaveform));
    }

    private void SetCandidate(RetranscriptionCandidate candidate)
    {
        _candidate = candidate;
        CandidateTranscript = candidate.Transcript;
        HasCandidate = true;
        ShowNotesTab = false;
    }

    private void ClearCandidate()
    {
        _candidate = null;
        CandidateTranscript = "";
        HasCandidate = false;
    }

    /// <summary>
    /// While an operation runs, its message belongs to the progress banner only. Opening the
    /// status InfoBar as well printed the same sentence twice, one above the other, and the
    /// duplicate outlived the operation because nothing closed it.
    /// </summary>
    private void SetWorking(bool value, string? status = null)
    {
        IsWorking = value;
        if (value)
        {
            WorkProgress = 0;
            IsStatusOpen = false;
        }

        if (status is not null)
        {
            StatusMessage = status;
            IsStatusError = false;
        }

        NotifyCommands();
        NotifyPresentation();
    }

    private void NotifyCommands()
    {
        SaveCommand.NotifyCanExecuteChanged();
        SaveTranscriptCommand.NotifyCanExecuteChanged();
        GenerateSummaryCommand.NotifyCanExecuteChanged();
        RetranscribeCommand.NotifyCanExecuteChanged();
        ToggleEditingCommand.NotifyCanExecuteChanged();
        ResumeFinishedCommand.NotifyCanExecuteChanged();
        StartFollowUpCommand.NotifyCanExecuteChanged();
    }

    private void NotifyPresentation()
    {
        OnPropertyChanged(nameof(ShowGeneratedNotes));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(ShowTranscriptTab));
        OnPropertyChanged(nameof(ShowNotesContent));
        OnPropertyChanged(nameof(CanWork));
        OnPropertyChanged(nameof(CanGenerateSummary));
        OnPropertyChanged(nameof(CanContinueMeeting));
        OnPropertyChanged(nameof(CanFollowUp));
        OnPropertyChanged(nameof(EditLabel));
        OnPropertyChanged(nameof(IsEditingTranscript));
        OnPropertyChanged(nameof(ShowTranscriptFeed));
        OnPropertyChanged(nameof(NotesMarkdown));
        OnPropertyChanged(nameof(ShowNotesDocument));
        OnPropertyChanged(nameof(ShowSummaryEditor));
        OnPropertyChanged(nameof(HasTrackChoice));
        OnPropertyChanged(nameof(ShowTranscriptCta));
        OnPropertyChanged(nameof(ShowManualNotes));
        OnPropertyChanged(nameof(SummaryActionLabel));
        OnPropertyChanged(nameof(TranscriptCta));
        OnPropertyChanged(nameof(ExportCurrentLabel));
        OnPropertyChanged(nameof(HasRecordingFile));
    }

    /// <summary>macOS says nothing when an action works; only a failure opens the status bar.</summary>
    private void ShowStatus(string message, bool isError = false)
    {
        StatusMessage = message;
        IsStatusError = isError;
        IsStatusOpen = isError;
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _disposed) == 0) FlushNotes();
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _meetings.Changed -= OnMeetingRuntimeChanged;
        _runtime.Dispose();
    }

    private static Muesli.Windows.MeetingItem ToMeetingItem(PersistedMeeting item) => new(
        item.Id, item.Title, item.CreatedAt, item.Transcript, item.Summary, item.SourcePath,
        item.ModelProfile, item.DurationMs, item.FolderId, item.WordCount, item.TemplateName,
        item.SpeakerAliases, item.HealthWarnings, item.SessionState, item.MicrophoneAudioPath,
        item.SystemAudioPath, item.SystemCaptureMode, item.RecoveredFromInterruption,
        item.LivePreviewModelId, item.LiveTranscriptOwnership, item.FinalTranscriptOwnerModelId,
        item.GapRecoveryModelId, item.ManualNotes, item.TitleIsManual, item.AutomationResult);

    private static string FormatDuration(int durationMs) => durationMs <= 0
        ? "0s"
        : TimeSpan.FromMilliseconds(durationMs).TotalHours >= 1
            ? TimeSpan.FromMilliseconds(durationMs).ToString(@"h\:mm\:ss")
            : TimeSpan.FromMilliseconds(durationMs).ToString(@"m\:ss");

    /// <summary>macOS MeetingDetailView.formatDuration: "1h 5m", "1m 2s", "45s".</summary>
    private static string FormatHeaderDuration(int durationMs)
    {
        var seconds = (int)Math.Round(Math.Max(durationMs, 0) / 1000d);
        if (seconds >= 3600) return $"{seconds / 3600}h {seconds % 3600 / 60}m";
        if (seconds >= 60) return seconds % 60 == 0 ? $"{seconds / 60}m" : $"{seconds / 60}m {seconds % 60}s";
        return $"{seconds}s";
    }

    private static string FormatTime(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString(@"h\:mm\:ss")
        : value.ToString(@"m\:ss");
}

public sealed record SpeakerAliasItem(string Source, string Replacement);

/// <summary>One link in the macOS thread breadcrumb: "title · start time", with an optional lead-in.</summary>
public sealed record MeetingThreadLink(string Id, string Text)
{
    public static MeetingThreadLink From(PersistedMeeting meeting, string prefix) =>
        new(meeting.Id, $"{prefix}{meeting.Title} · {meeting.CreatedAt.ToLocalTime():MMM d, h:mm tt}");
}
/// <summary>
/// One destination in the meeting-detail folder picker. <paramref name="DisplayName"/> shows
/// nesting as leading spaces the way the folder lists do, <paramref name="ParentSuffix"/> keeps a
/// nested folder distinguishable in the closed box where the indentation is gone, and
/// <paramref name="AccessibleName"/> is what UIA announces.
/// </summary>
public sealed record MeetingFolderOption(string Id, string DisplayName, string ParentSuffix, string AccessibleName)
{
    public bool HasParent => ParentSuffix.Length > 0;
}
