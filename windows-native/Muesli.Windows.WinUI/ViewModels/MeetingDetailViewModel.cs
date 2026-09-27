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
    private PersistedMeeting? _meeting;
    private RetranscriptionCandidate? _candidate;
    private int _disposed;

    public MeetingDetailViewModel(
        WinUiLibraryContext library,
        WinUiSettingsContext settings,
        IClipboardService clipboard,
        IFilePickerService filePickers,
        IAppDialogService dialogs,
        IUiDispatcher dispatcher)
    {
        _library = library;
        _clipboard = clipboard;
        _filePickers = filePickers;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _runtime = new WinUiMeetingDetailContext(library, settings);
        _runtime.PlaybackChanged += (_, args) => _dispatcher.TryEnqueue(() => UpdatePlayback(args));
        TemplateNames = MeetingSummaryService.BuiltInTemplateNames
            .Concat(library.LoadMeetingTemplates().Select(template => template.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [ObservableProperty] public partial string Title { get; set; } = "Meeting";
    [ObservableProperty] public partial string Metadata { get; private set; } = "";
    [ObservableProperty] public partial string TitleOwnershipLabel { get; private set; } = "";
    [ObservableProperty] public partial string Summary { get; private set; } = "";
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
    [ObservableProperty] public partial IReadOnlyList<WaveformBar> WaveformBars { get; private set; } = [];
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
    public bool HasWaveform => WaveformBars.Count > 0;
    public double PlaybackSliderMaximum => Math.Max(1d, PlaybackDurationSeconds);
    public string PlaybackTimeLabel =>
        $"{FormatTime(TimeSpan.FromSeconds(PlaybackSeconds))} / {FormatTime(TimeSpan.FromSeconds(PlaybackDurationSeconds))}";

    public void Load(string meetingId)
    {
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

        ApplyMeeting(_meeting);
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

    partial void OnTranscriptChanged(string value) =>
        HasTranscriptText = !string.IsNullOrWhiteSpace(value);

    partial void OnHasSavedTranscriptChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGenerateSummary));
        OnPropertyChanged(nameof(GenerateSummaryHint));
        GenerateSummaryCommand.NotifyCanExecuteChanged();
    }

    partial void OnPlaybackSecondsChanged(double value) => OnPropertyChanged(nameof(PlaybackTimeLabel));

    partial void OnPlaybackDurationSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(PlaybackSliderMaximum));
        OnPropertyChanged(nameof(PlaybackTimeLabel));
    }

    [RelayCommand(CanExecute = nameof(CanWork))]
    private void Save()
    {
        if (_meeting is null) return;
        var title = string.IsNullOrWhiteSpace(Title) ? "Untitled meeting" : Title.Trim();
        var updated = MeetingNotesComposer.ApplyManualNotes(
            MeetingNotesComposer.ApplyManualTitle(_meeting, title), ManualNotes) with
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
            ShowStatus(result.UsedLocalFallback && result.SafeFailureReason is not null
                ? $"Notes generated locally because the selected provider was unavailable ({result.SafeFailureReason})."
                : $"Notes generated with {result.Provider}.");
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
            await _clipboard.SetTextAsync(SpeakerAliasService.Apply(_meeting.Transcript, _meeting.SpeakerAliases), cancellationToken);
            ShowStatus("Transcript copied to the clipboard.");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ShowStatus($"Could not copy the transcript: {WinUiClipboardService.DescribeFailure(exception)}", true); }
    }

    [RelayCommand]
    private async Task ExportMarkdownAsync(CancellationToken cancellationToken)
    {
        if (_meeting is null) return;
        try
        {
            var model = ToMeetingItem(_meeting);
            var suggestedName = Path.ChangeExtension(
                MeetingExportFormatter.SuggestFilename(model, MeetingExportMode.FullMeeting), ".md");
            // PDF is offered only while the QuestPDF Community-license eligibility gate is approved.
            var picked = await _filePickers.PickSaveFileAsync(
                new FilePickerRequest("Export meeting", MeetingDocumentWriter.ApprovedExportExtensions, suggestedName), cancellationToken);
            if (picked is null) return;
            var markdown = MeetingExportFormatter.BuildMarkdown(model, MeetingExportMode.FullMeeting, _meeting.SpeakerAliases);
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
        var name = string.IsNullOrWhiteSpace(_meeting.Title) ? "Untitled meeting" : _meeting.Title.Trim();
        var choice = await _dialogs.ConfirmAsync(
            $"Delete this meeting and its saved transcript?\n\n“{name}”\n{Metadata}\n\n" +
            "Retained recordings owned by Muesli remain available for recovery until cleanup.",
            "Delete meeting", cancellationToken);
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
        Title = meeting.Title;
        Metadata = $"{meeting.CreatedAt.ToLocalTime():MMM d, yyyy · h:mm tt} · {FormatDuration(meeting.DurationMs)} · {meeting.ModelProfile}";
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
        PlaybackTracks = _runtime.GetTracks(meeting);
        HasRecording = PlaybackTracks.Count > 0;
        if (SelectedPlaybackTrack is null || !PlaybackTracks.Any(track => track.Path == SelectedPlaybackTrack.Path))
            SelectedPlaybackTrack = PlaybackTracks.FirstOrDefault();
        RefreshWaveform();
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

    private void UpdatePlayback(MeetingPlaybackStateChangedEventArgs args)
    {
        PlaybackStatus = args.State == MeetingPlaybackState.Failed
            ? $"Playback failed · {args.Failure}"
            : $"{args.State} · {FormatTime(args.Position)} / {FormatTime(args.Duration)}";
        PlaybackSeconds = args.Position.TotalSeconds;
        PlaybackDurationSeconds = Math.Max(args.Duration.TotalSeconds, 0);
        RefreshWaveform();
    }

    private void RefreshWaveform()
    {
        WaveformBars = _runtime.WaveformPeaks
            .Select(peak => new WaveformBar(Math.Max(2, peak * 46)))
            .ToList();
        OnPropertyChanged(nameof(HasWaveform));
    }

    private void SetCandidate(RetranscriptionCandidate candidate)
    {
        _candidate = candidate;
        CandidateTranscript = candidate.Transcript;
        HasCandidate = true;
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
    }

    private void NotifyPresentation()
    {
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(ShowTranscriptTab));
        OnPropertyChanged(nameof(ShowNotesContent));
        OnPropertyChanged(nameof(CanWork));
        OnPropertyChanged(nameof(CanGenerateSummary));
    }

    private void ShowStatus(string message, bool isError = false)
    {
        StatusMessage = message;
        IsStatusError = isError;
        IsStatusOpen = true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
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

    private static string FormatTime(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString(@"h\:mm\:ss")
        : value.ToString(@"m\:ss");
}

public sealed record SpeakerAliasItem(string Source, string Replacement);
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
public sealed record WaveformBar(double Height);
