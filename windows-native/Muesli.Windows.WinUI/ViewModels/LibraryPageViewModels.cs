using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muesli.Windows.Services;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.WinUI.ViewModels;

public partial class TimelinePageViewModel : ObservableObject
{
    private readonly WinUiLibraryContext _library;

    public TimelinePageViewModel(WinUiLibraryContext library)
    {
        _library = library;
        Snapshot = library.ReadSnapshot();
        ApplyFilterAndSort();
    }

    [ObservableProperty]
    public partial WinUiLibrarySnapshot Snapshot { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<WinUiTimelineEntry> Entries { get; private set; } = [];

    /// <summary>
    /// Timeline sections mirror the desktop library: a small date heading followed by one
    /// divided surface containing the activity for that day.  Keeping the grouping in the
    /// view model means the page can stay responsive while filtering and sorting.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<TimelineGroup> Groups { get; private set; } = [];

    [ObservableProperty]
    public partial int FilterIndex { get; private set; }

    [ObservableProperty]
    public partial int SortIndex { get; set; }

    public bool HasEntries => Entries.Count > 0;

    /// <summary>
    /// True when the kind filter is narrowing the list, so the empty state can say "this filter
    /// matches nothing" rather than "you have no history".
    /// </summary>
    public bool IsFiltered => FilterIndex != 0;

    public string ResultCountLabel => $"{Entries.Count:N0} {(Entries.Count == 1 ? "item" : "items")}";
    /// <summary>
    /// P2-01 — "words dictated" means dictation words. This card used to print
    /// <c>Snapshot.TotalWords</c> (dictations + meeting transcripts) under that label, which is
    /// why Timeline read 129 next to the Dictations page's 63 for one profile.
    /// </summary>
    public string DictationWordsLabel => Snapshot.DictationWords.ToString("N0");

    public string AveragePaceLabel => Snapshot.AverageWordsPerMinute == 0
        ? "—"
        : $"{Snapshot.AverageWordsPerMinute:N0}";

    [RelayCommand]
    private void Refresh()
    {
        Snapshot = _library.ReadSnapshot();
        ApplyFilterAndSort();
        NotifyDerived();
    }

    public void SetFilter(int filterIndex)
    {
        if (filterIndex is < 0 or > 2) return;
        FilterIndex = filterIndex;
        ApplyFilterAndSort();
    }

    partial void OnSnapshotChanged(WinUiLibrarySnapshot value) => NotifyDerived();

    partial void OnSortIndexChanged(int value) => ApplyFilterAndSort();

    partial void OnEntriesChanged(IReadOnlyList<WinUiTimelineEntry> value) => NotifyDerived();

    /// <summary>
    /// Kind filter plus sort order. P2-05 removed this page's own search field — the shell's
    /// single search entry point routes cross-library queries to <c>SearchPage</c> — so the text
    /// predicate that used to live here is gone with it. Ordering and the kind filter are
    /// unchanged.
    /// </summary>
    private void ApplyFilterAndSort()
    {
        IEnumerable<WinUiTimelineEntry> query = Snapshot.Timeline;
        query = FilterIndex switch
        {
            1 => query.Where(item => item.Kind == WinUiTimelineKind.Dictation),
            2 => query.Where(item => item.Kind == WinUiTimelineKind.Meeting),
            _ => query
        };
        Entries = SortIndex == 1
            ? query.OrderBy(item => item.Timestamp).ToList()
            : query.OrderByDescending(item => item.Timestamp).ToList();

        Groups = Entries
            .GroupBy(item => item.Timestamp.ToLocalTime().Date)
            .Select(group => new TimelineGroup(
                FormatDateHeading(group.Key),
                group.ToList()))
            .ToList();
    }

    private static string FormatDateHeading(DateTime date)
    {
        var localDate = date.Date;
        var today = DateTime.Today;
        return localDate == today
            ? "TODAY"
            : localDate == today.AddDays(-1)
                ? "YESTERDAY"
                : localDate.ToString("MMMM d, yyyy").ToUpperInvariant();
    }

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(Entries));
        OnPropertyChanged(nameof(Groups));
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(ResultCountLabel));
        OnPropertyChanged(nameof(DictationWordsLabel));
        OnPropertyChanged(nameof(AveragePaceLabel));
    }
}

public sealed record TimelineGroup(string Name, IReadOnlyList<WinUiTimelineEntry> Items);

public partial class DictationsPageViewModel : ObservableObject, IDisposable
{
    private readonly WinUiLibraryContext _library;
    private readonly IClipboardService _clipboard;
    private readonly IAppDialogService _dialogs;
    private readonly WinUiDictationContext _dictation;
    private readonly IUiDispatcher _dispatcher;
    private IReadOnlyList<DictationListItem> _allItems = [];

    public DictationsPageViewModel(
        WinUiLibraryContext library,
        IClipboardService clipboard,
        IAppDialogService dialogs,
        WinUiDictationContext dictation,
        IUiDispatcher dispatcher)
    {
        _library = library;
        _clipboard = clipboard;
        _dialogs = dialogs;
        _dictation = dictation;
        _dispatcher = dispatcher;
        _dictation.Changed += OnDictationChanged;
        Reload();
        UpdateDictationState();
    }

    private void OnDictationChanged(object? sender, EventArgs args) => _dispatcher.TryEnqueue(UpdateDictationState);
    public void Dispose() => _dictation.Changed -= OnDictationChanged;

    [ObservableProperty]
    public partial IReadOnlyList<DictationListItem> Items { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<DictationGroup> Groups { get; private set; } = [];

    [ObservableProperty]
    public partial int SortIndex { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsStatusOpen { get; set; }

    [ObservableProperty]
    public partial bool IsStatusError { get; private set; }

    [ObservableProperty]
    public partial string DictationWordsLabel { get; private set; } = "0";

    [ObservableProperty]
    public partial string CurrentStreakLabel { get; private set; } = "0";

    [ObservableProperty]
    public partial string AveragePaceLabel { get; private set; } = "—";

    [ObservableProperty]
    public partial bool IsRecording { get; private set; }

    [ObservableProperty]
    public partial bool IsDictationBusy { get; private set; }

    [ObservableProperty]
    public partial string DictationStatus { get; private set; } = "Ready";

    public string RecordButtonLabel => IsRecording ? "Stop Dictation" : "Start Dictation";
    public bool CanToggleDictation => IsRecording || !IsDictationBusy;
    public bool HasActiveRuntimeStatus => IsRecording || IsDictationBusy;

    public bool HasItems => Items.Count > 0;

    public string ResultCountLabel =>
        $"{Items.Count:N0} {(Items.Count == 1 ? "dictation" : "dictations")}";

    /// <summary>
    /// True when <paramref name="id"/> names a dictation currently in this profile's history.
    /// The page uses it to tell a real record id (handed over by a Timeline row activation) from
    /// the shell's navigation tag, so a stale or unknown id never selects the wrong row.
    /// </summary>
    public bool Contains(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        _allItems.Any(item => string.Equals(item.Id, id, StringComparison.Ordinal));

    [RelayCommand]
    private void Refresh()
    {
        Reload();
        ShowStatus("Dictation history refreshed.");
    }

    [RelayCommand(CanExecute = nameof(CanToggleDictation))]
    private async Task ToggleDictationAsync()
    {
        try
        {
            if (_dictation.IsRecording) await _dictation.StopFromDashboardAsync();
            else await _dictation.StartFromDashboardAsync();
        }
        catch (Exception exception)
        {
            ShowStatus($"Dictation failed: {exception.Message}", isError: true);
        }
        finally
        {
            Reload();
            UpdateDictationState();
        }
    }

    [RelayCommand]
    private async Task CancelDictationAsync()
    {
        await _dictation.CancelAsync();
        UpdateDictationState();
    }

    [RelayCommand]
    private async Task CopyAsync(DictationListItem? item, CancellationToken cancellationToken)
    {
        if (item is null) return;
        try
        {
            await _clipboard.SetTextAsync(item.Text, cancellationToken);
            ShowStatus("Dictation copied to the clipboard.");
        }
        catch (Exception exception)
        {
            ShowStatus($"Could not copy the dictation: {WinUiClipboardService.DescribeFailure(exception)}", isError: true);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(DictationListItem? item, CancellationToken cancellationToken)
    {
        if (item is null) return;
        // Row actions are revealed on hover, so the confirmation has to repeat which record is
        // about to go: the dialog is the last point at which the user can tell that the pointer
        // was over the row they meant.
        var choice = await _dialogs.ConfirmAsync(
            $"Delete this dictation permanently?\n\n{item.TimeLabel} · “{Snippet(item.Text)}”\n\n" +
            "This removes its saved transcript from this profile.",
            "Delete dictation",
            cancellationToken);
        if (choice != AppDialogChoice.Primary) return;

        try
        {
            if (_library.DeleteDictation(item.Id))
            {
                Reload();
                ShowStatus("Dictation deleted.");
            }
            else
            {
                Reload();
                ShowStatus("That dictation was already removed.", isError: true);
            }
        }
        catch (Exception exception)
        {
            ShowStatus($"Could not delete the dictation: {exception.Message}", isError: true);
        }
    }

    /// <summary>One line of the transcript, short enough to stay readable inside a dialog.</summary>
    private static string Snippet(string text)
    {
        var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= 90 ? single : single[..90].TrimEnd() + "…";
    }

    partial void OnItemsChanged(IReadOnlyList<DictationListItem> value)
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ResultCountLabel));
    }

    partial void OnSortIndexChanged(int value) => ApplyFilterAndSort();

    private void Reload()
    {
        var snapshot = _library.ReadSnapshot();
        _allItems = snapshot.Dictations.Select(DictationListItem.From).ToList();
        DictationWordsLabel = snapshot.DictationWords.ToString("N0");
        CurrentStreakLabel = snapshot.CurrentStreak.ToString("N0");
        // P2-01: the pace is read from the shared snapshot calculation rather than recomputed
        // here, which is how this page came to disagree with Timeline and Insights.
        AveragePaceLabel = snapshot.AverageWordsPerMinute == 0
            ? "—"
            : snapshot.AverageWordsPerMinute.ToString("N0");
        ApplyFilterAndSort();
    }

    /// <summary>
    /// Sort order only. P2-05 removed this page's own search field in favour of the shell's
    /// single search entry point, so the text predicate that used to live here went with it.
    /// Ordering is unchanged.
    /// </summary>
    private void ApplyFilterAndSort()
    {
        IEnumerable<DictationListItem> query = _allItems;

        Items = SortIndex == 1
            ? query.OrderBy(item => item.Timestamp).ToList()
            : query.OrderByDescending(item => item.Timestamp).ToList();

        Groups = Items
            .GroupBy(item => item.Timestamp.ToLocalTime().Date)
            .Select(group => new DictationGroup(
                FormatDateHeading(group.Key),
                group.ToList()))
            .ToList();
    }

    private static string FormatDateHeading(DateTime date)
    {
        var localDate = date.Date;
        var today = DateTime.Today;
        return localDate == today
            ? "TODAY"
            : localDate == today.AddDays(-1)
                ? "YESTERDAY"
                : localDate.ToString("MMMM d, yyyy").ToUpperInvariant();
    }

    private void ShowStatus(string message, bool isError = false)
    {
        StatusMessage = message;
        IsStatusError = isError;
        IsStatusOpen = true;
    }

    private void UpdateDictationState()
    {
        IsRecording = _dictation.IsRecording;
        IsDictationBusy = _dictation.IsBusy;
        DictationStatus = _dictation.Status;
        OnPropertyChanged(nameof(RecordButtonLabel));
        OnPropertyChanged(nameof(CanToggleDictation));
        OnPropertyChanged(nameof(HasActiveRuntimeStatus));
        ToggleDictationCommand.NotifyCanExecuteChanged();
        if (!IsRecording && !IsDictationBusy) Reload();
    }
}

public sealed record DictationListItem(
    string Id,
    string Text,
    DateTime Timestamp,
    string TimestampLabel,
    string DurationLabel,
    string ModelLabel)
{
    public string TimeLabel => Timestamp.ToLocalTime().ToString("hh:mm tt");

    public static DictationListItem From(PersistedDictation item) => new(
        item.Id,
        string.IsNullOrWhiteSpace(item.Text) ? "No text was saved" : item.Text.Trim(),
        item.Timestamp,
        item.Timestamp.ToLocalTime().ToString("MMM d, yyyy · hh:mm tt"),
        FormatDuration(item.DurationMs),
        string.IsNullOrWhiteSpace(item.ModelProfile) ? "Local model" : item.ModelProfile);

    private static string FormatDuration(int durationMs) => durationMs <= 0
        ? ""
        : TimeSpan.FromMilliseconds(durationMs).TotalHours >= 1
            ? TimeSpan.FromMilliseconds(durationMs).ToString(@"h\:mm\:ss")
        : TimeSpan.FromMilliseconds(durationMs).ToString(@"m\:ss");
}

public sealed record DictationGroup(string Name, IReadOnlyList<DictationListItem> Items);

public partial class MeetingsPageViewModel : ObservableObject, IDisposable
{
    private readonly WinUiLibraryContext _library;
    private readonly WinUiMeetingContext _meetingRuntime;
    private readonly WinUiMeetingDetectionService _detection;
    private readonly IFilePickerService _filePickers;
    private readonly IAppDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action _showLiveTranscript;
    private IReadOnlyList<MeetingListItem> _allItems = [];

    public MeetingsPageViewModel(
        WinUiLibraryContext library,
        WinUiMeetingContext meetingRuntime,
        WinUiMeetingDetectionService detection,
        IFilePickerService filePickers,
        IAppDialogService dialogs,
        IUiDispatcher dispatcher,
        Action showLiveTranscript)
    {
        _library = library;
        _meetingRuntime = meetingRuntime;
        _detection = detection;
        _filePickers = filePickers;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _showLiveTranscript = showLiveTranscript;
        _meetingRuntime.Changed += OnMeetingChanged;
        Reload();
        UpdateRuntimeState();
    }

    private void OnMeetingChanged(object? sender, EventArgs args) => _dispatcher.TryEnqueue(UpdateRuntimeState);
    public void Dispose() => _meetingRuntime.Changed -= OnMeetingChanged;

    [ObservableProperty]
    public partial IReadOnlyList<MeetingListItem> Items { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<MeetingFolderListItem> Folders { get; private set; } = [];
    /// <summary>
    /// The parent picker binds <c>SelectedItem</c>, not <c>SelectedValue</c>. A ComboBox resolves
    /// <c>SelectedValue</c> against its items only while the item set is stable, and this list is
    /// rebuilt on every reload and on every folder selection — which is why the control rendered
    /// blank even though a valid parent was selected. The id stays the value the save path reads.
    /// </summary>
    [ObservableProperty] public partial MeetingFolderListItem? SelectedParentFolder { get; set; }
    [ObservableProperty] public partial IReadOnlyList<MeetingFolderListItem> ParentFolders { get; private set; } = [];

    public string SelectedParentFolderId => SelectedParentFolder?.Id ?? "";

    partial void OnSelectedParentFolderChanged(MeetingFolderListItem? value) =>
        OnPropertyChanged(nameof(SelectedParentFolderId));

    /// <summary>
    /// P3-06. A ComboBox clears its own selection when <c>ItemsSource</c> is replaced, and it does
    /// so during the same change notification that replaced the list — so restoring the value in
    /// line is overwritten again, and the control renders blank with no value at all. Restoring on
    /// the next dispatcher turn lands after the control has settled. The value is re-pushed with an
    /// explicit change notification rather than an assignment, because the view model usually
    /// already holds the right value and an equal assignment raises nothing.
    /// </summary>
    private void RepublishPickerSelections() => _dispatcher.TryEnqueue(() =>
    {
        if (TimeRangeIndex < 0 && TimeRangeOptions.Count > 0)
        {
            TimeRangeIndex = 0;
        }

        OnPropertyChanged(nameof(TimeRangeIndex));
        OnPropertyChanged(nameof(SelectedParentFolder));
    });

    /// <summary>Points the parent picker at <paramref name="folderId"/>, defaulting to top level.</summary>
    private void SetParentSelection(string? folderId)
    {
        SelectedParentFolder =
            ParentFolders.FirstOrDefault(folder => string.Equals(folder.Id, folderId ?? "", StringComparison.Ordinal))
            ?? ParentFolders.FirstOrDefault(folder => folder.IsAllMeetings);
        RepublishPickerSelections();
    }

    [ObservableProperty]
    public partial bool IsRecording { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial bool IsImporting { get; private set; }

    [ObservableProperty]
    public partial string MeetingStatus { get; private set; } = "Ready";

    [ObservableProperty]
    public partial string LiveTranscript { get; private set; } = "";

    [ObservableProperty]
    public partial double ImportProgress { get; private set; }

    [ObservableProperty]
    public partial bool IsPaused { get; private set; }

    [ObservableProperty]
    public partial string CaptureStatus { get; private set; } = "Idle";

    [ObservableProperty]
    public partial string DetectionStatus { get; private set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<RecoverableMeetingListItem> RecoverableSessions { get; private set; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial int SortIndex { get; set; }

    /// <summary>
    /// Mirrors the macOS meeting browser's date filter (All time / Last 2 days / Last week /
    /// Last 2 weeks / Last month / Last 3 months). The options are scoped to the history that
    /// actually exists, exactly like the reference: a range is only offered once the oldest
    /// meeting is old enough to make it meaningful.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> TimeRangeOptions { get; private set; } = ["All time"];

    [ObservableProperty]
    public partial int TimeRangeIndex { get; set; }

    [ObservableProperty]
    public partial string SelectedFolderId { get; private set; } = "";

    [ObservableProperty]
    public partial string FolderName { get; set; } = "";

    /// <summary>
    /// Validation and confirmation for the folder editor, rendered next to the editor's own
    /// controls. Folder problems used to be written to <see cref="MeetingStatus"/>, which surfaces
    /// in the recording InfoBar at the top of the page — a duplicate-name refusal appeared far away
    /// from the field that caused it, or not at all.
    /// </summary>
    [ObservableProperty]
    public partial string FolderEditorStatus { get; private set; } = "";

    public bool HasFolderEditorStatus => !string.IsNullOrWhiteSpace(FolderEditorStatus);

    partial void OnFolderEditorStatusChanged(string value) => OnPropertyChanged(nameof(HasFolderEditorStatus));

    public bool IsActive => IsRecording || IsBusy || IsPaused;
    public bool CanStart => !IsActive && !IsImporting;
    public bool CanStop => (IsRecording || IsPaused) && !IsBusy;
    public bool CanPause => IsRecording && !IsBusy;
    public bool CanResume => IsPaused && !IsBusy;
    public bool HasRecoverable => RecoverableSessions.Count > 0;

    public bool HasItems => Items.Count > 0;

    public bool HasIdleNotice =>
        !IsActive &&
        !IsImporting &&
        !string.IsNullOrWhiteSpace(MeetingStatus) &&
        !string.Equals(MeetingStatus, "Ready", StringComparison.Ordinal);

    public string ResultCountHint =>
        $"{(Items.Count == 1 ? "1 meeting" : $"{Items.Count:N0} meetings")} · Open a meeting to review notes, transcript, and template-driven summaries";

    private string _currentFolderLabel = "All Meetings";

    /// <summary>
    /// The subtitle is the current folder name. The page title stays "Meetings", matching the
    /// macOS browser where the folder is the line under the page heading.
    /// </summary>
    public string CurrentFolderLabel => _currentFolderLabel;

    public string EmptyStateTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SelectedFolderId)) return "No meetings in this folder";
            if (SearchText.Trim().Length > 0) return "No meetings match your search";
            if (TimeRangeIndex != 0) return "No meetings in this time range";
            return "No meetings yet";
        }
    }

    public string EmptyStateInstruction
    {
        get
        {
            if (SearchText.Trim().Length > 0 || !string.IsNullOrWhiteSpace(SelectedFolderId) || TimeRangeIndex != 0)
                return "Try another source, time range, or folder.";
            return "Start a Quick Note or import audio to create your first meeting.";
        }
    }

    [RelayCommand]
    private void Refresh() => Reload();

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartQuickNoteAsync(CancellationToken cancellationToken)
    {
        try
        {
            MeetingStatus = "Starting recording…";
            await _meetingRuntime.StartQuickNoteAsync(cancellationToken);
            MeetingStatus = "Recording Quick Note";
        }
        catch (Exception exception)
        {
            MeetingStatus = $"Could not start recording: {exception.Message}";
        }
        finally
        {
            UpdateRuntimeState();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopRecordingAsync(CancellationToken cancellationToken)
    {
        try
        {
            MeetingStatus = "Finalizing transcript…";
            await _meetingRuntime.StopAndSaveAsync(cancellationToken);
            Reload();
            MeetingStatus = "Meeting saved";
        }
        catch (OperationCanceledException)
        {
            MeetingStatus = "Finalization cancelled; captured audio is retained for recovery";
        }
        catch (Exception exception)
        {
            MeetingStatus = $"Could not finalize meeting: {exception.Message}";
        }
        finally
        {
            UpdateRuntimeState();
        }
    }

    [RelayCommand]
    private async Task DiscardRecordingAsync()
    {
        try
        {
            await _meetingRuntime.CancelAsync();
            MeetingStatus = "Recording discarded";
        }
        catch (Exception exception)
        {
            MeetingStatus = $"Could not discard recording: {exception.Message}";
        }
        finally
        {
            UpdateRuntimeState();
        }
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private async Task PauseRecordingAsync()
    {
        try
        {
            await _meetingRuntime.PauseAsync();
            MeetingStatus = "Recording paused. Audio is retained.";
        }
        catch (Exception exception)
        {
            MeetingStatus = $"Could not pause recording: {exception.Message}";
        }
        finally
        {
            UpdateRuntimeState();
        }
    }

    [RelayCommand(CanExecute = nameof(CanResume))]
    private async Task ResumeRecordingAsync()
    {
        try
        {
            await _meetingRuntime.ResumeAsync();
            MeetingStatus = "Recording resumed";
        }
        catch (Exception exception)
        {
            MeetingStatus = $"Could not resume recording: {exception.Message}";
        }
        finally
        {
            UpdateRuntimeState();
        }
    }

    [RelayCommand]
    private void ShowLiveTranscript() => _showLiveTranscript();

    [RelayCommand]
    private async Task FinalizeRecoveryAsync(RecoverableMeetingListItem? item, CancellationToken cancellationToken)
    {
        if (item is null) return;
        try
        {
            MeetingStatus = "Finalizing recovered audio…";
            await _meetingRuntime.FinalizeRecoverableAsync(item.Session, cancellationToken);
            Reload();
            MeetingStatus = "Recovered meeting saved";
        }
        catch (OperationCanceledException)
        {
            MeetingStatus = "Recovery cancelled; captured audio is still retained.";
        }
        catch (Exception exception)
        {
            MeetingStatus = $"Could not recover meeting: {exception.Message}";
        }
        finally
        {
            UpdateRuntimeState();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task ImportAudioAsync(CancellationToken cancellationToken)
    {
        try
        {
            var picked = await _filePickers.PickOpenFileAsync(
                new FilePickerRequest(
                    "Import meeting audio or video",
                    MediaImportFormats.SupportedExtensions),
                cancellationToken);
            if (picked is null) return;
            IsImporting = true;
            ImportProgress = 0;
            MeetingStatus = $"Transcribing {picked.DisplayName}…";
            var progress = new Progress<TranscriptionProgress>(report =>
            {
                ImportProgress = report.Fraction is { } fraction ? fraction * 100 : 0;
                MeetingStatus = report.Stage == TranscriptionStage.Decoding
                    ? "Decoding imported media…"
                    : "Transcribing imported media…";
            });
            await _meetingRuntime.ImportAsync(picked.Path, progress, cancellationToken);
            Reload();
            MeetingStatus = "Imported meeting saved";
        }
        catch (OperationCanceledException)
        {
            MeetingStatus = "Import cancelled; the original file was not changed";
        }
        catch (Exception exception)
        {
            MeetingStatus = $"Could not import meeting: {exception.Message}";
        }
        finally
        {
            IsImporting = false;
            ImportProgress = 0;
            NotifyCommandState();
        }
    }

    partial void OnItemsChanged(IReadOnlyList<MeetingListItem> value)
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ResultCountHint));
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilterAndSort();
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateInstruction));
    }

    partial void OnSortIndexChanged(int value) => ApplyFilterAndSort();

    /// <summary>
    /// P3-06: a <c>ComboBox</c> reports <c>SelectedIndex = -1</c> whenever its <c>ItemsSource</c> is
    /// replaced, and the TwoWay binding pushes that here. -1 is not a range the user can pick, so it
    /// is normalised back to "All time" instead of leaving a filter control with no visible value.
    /// </summary>
    partial void OnTimeRangeIndexChanged(int value)
    {
        if (value < 0 && TimeRangeOptions.Count > 0)
        {
            TimeRangeIndex = 0;
            return;
        }

        ApplyFilterAndSort();
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateInstruction));
    }

    public void SelectFolder(string? folderId)
    {
        SelectedFolderId = folderId ?? "";
        FolderEditorStatus = "";
        FolderName = Folders.FirstOrDefault(folder => folder.Id == SelectedFolderId)?.Name ?? "";
        var folders = _library.History.LoadMeetingFolders();
        var excluded = MeetingFolderHierarchy.Subtree(folders, SelectedFolderId);
        // A folder may not be moved inside itself or one of its own descendants.
        ParentFolders = Folders.Where(folder => folder.Id.Length == 0 || !excluded.Contains(folder.Id)).ToList();
        SetParentSelection(folders.FirstOrDefault(folder => folder.Id == SelectedFolderId)?.ParentId);
        UpdateFolderContext();
        ApplyFilterAndSort();
    }

    /// <summary>
    /// The source filter mirrors the macOS origin picker (All / This Mac / From iPhone) scoped to
    /// the only history a Windows profile carries: everything was captured on this PC. Both options
    /// therefore resolve to the same local query, which is truthful rather than decorative.
    /// </summary>
    public void SetSourceFilter(int filterIndex)
    {
        if (filterIndex is < 0 or > 1) return;
        SourceFilterIndex = filterIndex;
        ApplyFilterAndSort();
    }

    [ObservableProperty]
    public partial int SourceFilterIndex { get; private set; }

    private void UpdateFolderContext()
    {
        _currentFolderLabel = Folders.FirstOrDefault(folder => folder.Id == SelectedFolderId)?.Name ?? "All Meetings";
        OnPropertyChanged(nameof(CurrentFolderLabel));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateInstruction));
    }

    [RelayCommand]
    private void NewFolder()
    {
        SelectedFolderId = "";
        FolderName = "";
        ParentFolders = Folders;
        SetParentSelection(null);
        FolderEditorStatus = "";
        UpdateFolderContext();
    }

    /// <summary>
    /// Abandons an in-progress rename or move. Nothing has been written yet — the editor is a
    /// staging area — so this just puts the fields back to what is stored for the selected folder
    /// and says so, which is what makes "cancel a rename" observable rather than implicit.
    /// </summary>
    [RelayCommand]
    private void CancelFolderEdit()
    {
        if (string.IsNullOrWhiteSpace(SelectedFolderId))
        {
            NewFolder();
            FolderEditorStatus = "Cleared.";
            return;
        }

        SelectFolder(SelectedFolderId);
        FolderEditorStatus = "Edit cancelled; the folder is unchanged.";
    }

    [RelayCommand]
    private void SaveFolder()
    {
        var name = FolderName.Trim();
        if (name.Length == 0)
        {
            FolderEditorStatus = "Enter a folder name.";
            return;
        }

        if (string.Equals(name, "All Meetings", StringComparison.CurrentCultureIgnoreCase))
        {
            FolderEditorStatus = "“All Meetings” is the name of the whole library. Choose another name.";
            return;
        }

        // Duplicate names are rejected among siblings only: two folders may both be called "Notes"
        // as long as they sit under different parents, which is the point of nesting. A clash with
        // a folder somewhere else in the tree is not a clash the user can see.
        var stored = _library.History.LoadMeetingFolders();
        var parentId = string.IsNullOrWhiteSpace(SelectedParentFolderId) ? null : SelectedParentFolderId;
        var duplicate = stored.Any(folder =>
            !string.Equals(folder.Id, SelectedFolderId, StringComparison.Ordinal) &&
            string.Equals(folder.ParentId ?? "", parentId ?? "", StringComparison.Ordinal) &&
            string.Equals(folder.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (duplicate)
        {
            var where = parentId is null
                ? "at the top level"
                : $"inside “{stored.FirstOrDefault(folder => folder.Id == parentId)?.Name ?? "that folder"}”";
            FolderEditorStatus = $"A folder called “{name}” already exists {where}.";
            return;
        }

        try
        {
            var saved = _library.SaveMeetingFolder(
                string.IsNullOrWhiteSpace(SelectedFolderId) ? null : SelectedFolderId,
                name, parentId);
            _library.MoveMeetingFolder(saved.Id, SelectedParentFolderId);
            Reload();
            SelectFolder(saved.Id);
            FolderEditorStatus = $"Saved “{saved.Name}”.";
        }
        catch (Exception exception) { FolderEditorStatus = $"Could not save folder: {exception.Message}"; }
    }

    [RelayCommand]
    private async Task DeleteFolderAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(SelectedFolderId)) return;
        var folder = Folders.FirstOrDefault(item => item.Id == SelectedFolderId);
        if (folder is null) return;
        var choice = await _dialogs.ConfirmAsync(
            $"Delete the folder “{folder.Name}”? Its meetings will remain in All Meetings.",
            "Delete meeting folder", cancellationToken);
        if (choice != AppDialogChoice.Primary) return;
        if (_library.DeleteMeetingFolder(folder.Id))
        {
            Reload();
            SelectFolder("");
            FolderEditorStatus = $"Deleted “{folder.Name}”; its meetings remain in All Meetings.";
        }
        else FolderEditorStatus = $"“{folder.Name}” could not be deleted.";
    }

    private void Reload()
    {
        var snapshot = _library.ReadSnapshot();
        var folderNameById = snapshot.Folders
            .GroupBy(folder => folder.Id)
            .ToDictionary(group => group.Key, group => group.First().Name);
        _allItems = snapshot.Meetings.Select(item => MeetingListItem.From(
            item,
            string.IsNullOrWhiteSpace(item.FolderId) || !folderNameById.TryGetValue(item.FolderId, out var name)
                ? null
                : name)).ToList();
        Folders = MeetingFolderListItem.BuildTree(snapshot);

        // P3-06: replacing a ComboBox's ItemsSource clears its selection, and the TwoWay binding
        // writes that -1 straight back here. Capture the intent first and restore it after the new
        // list has been published, so a refresh cannot leave the filter rendering blank.
        var desiredTimeRange = TimeRangeIndex < 0 ? 0 : TimeRangeIndex;
        var desiredParent = SelectedParentFolderId;

        // Only republish the range options when they actually changed. Handing a ComboBox an equal
        // but new list is what made it drop its selection on every refresh in the first place.
        var options = BuildTimeRangeOptions(snapshot.Meetings);
        if (!options.SequenceEqual(TimeRangeOptions, StringComparer.Ordinal))
        {
            TimeRangeOptions = options;
        }

        ParentFolders = Folders;
        TimeRangeIndex = desiredTimeRange >= TimeRangeOptions.Count ? 0 : desiredTimeRange;
        SetParentSelection(desiredParent);
        ApplyFilterAndSort();
        UpdateFolderContext();
    }

    /// <summary>
    /// The macOS browser only lists ranges the history can actually satisfy: "Last 2 days" needs
    /// a meeting at least a day old, "Last week" needs three days, and so on. Reproducing that
    /// keeps the filter truthful instead of offering ranges that are guaranteed empty.
    /// </summary>
    private static IReadOnlyList<string> BuildTimeRangeOptions(IReadOnlyList<PersistedMeeting> meetings)
    {
        var options = new List<string> { "All time" };
        var oldest = meetings.Select(meeting => meeting.CreatedAt).DefaultIfEmpty().Min();
        if (oldest == default)
        {
            return options;
        }

        var days = (DateTime.Now.Date - oldest.Date).Days;
        if (days >= 1) options.Add("Last 2 days");
        if (days >= 3) options.Add("Last week");
        if (days >= 8) options.Add("Last 2 weeks");
        if (days >= 15) options.Add("Last month");
        if (days >= 31) options.Add("Last 3 months");
        return options;
    }

    private void ApplyFilterAndSort()
    {
        IEnumerable<MeetingListItem> query = _allItems;
        if (!string.IsNullOrWhiteSpace(SelectedFolderId))
        {
            var subtree = MeetingFolderHierarchy.Subtree(_library.History.LoadMeetingFolders(), SelectedFolderId);
            query = query.Where(item => item.FolderId is not null && subtree.Contains(item.FolderId));
        }
        var search = SearchText.Trim();
        if (search.Length > 0)
        {
            query = query.Where(item =>
                item.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                item.Summary.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        }
        var threshold = TimeRangeThreshold(TimeRangeIndex);
        if (threshold is not null)
        {
            query = query.Where(item => item.Timestamp >= threshold);
        }
        Items = SortIndex == 1
            ? query.OrderBy(item => item.Timestamp).ToList()
            : query.OrderByDescending(item => item.Timestamp).ToList();
    }

    private static DateTime? TimeRangeThreshold(int index) => index switch
    {
        1 => DateTime.Now.AddDays(-2),
        2 => DateTime.Now.AddDays(-7),
        3 => DateTime.Now.AddDays(-14),
        4 => DateTime.Now.AddMonths(-1),
        5 => DateTime.Now.AddMonths(-3),
        _ => null
    };

    private void UpdateRuntimeState()
    {
        IsRecording = _meetingRuntime.IsRecording;
        IsBusy = _meetingRuntime.IsBusy;
        IsPaused = _meetingRuntime.IsPaused;
        LiveTranscript = _meetingRuntime.LiveTranscript;
        CaptureStatus = _meetingRuntime.CaptureStatus;
        DetectionStatus = _detection.Status;
        RecoverableSessions = _meetingRuntime.RecoverableSessions
            .Select(session => new RecoverableMeetingListItem(
                session,
                string.IsNullOrWhiteSpace(session.Journal.Title) ? "Recoverable recording" : session.Journal.Title,
                $"{session.Journal.StartedAtUtc.ToLocalTime():g} · {session.MicrophonePartCount} mic part(s) · {session.SystemPartCount} system part(s)"))
            .ToList();
        if (IsRecording) MeetingStatus = "Recording Quick Note";
        else if (IsPaused) MeetingStatus = "Paused · audio retained";
        NotifyCommandState();
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(HasRecoverable));
        OnPropertyChanged(nameof(HasIdleNotice));
    }

    partial void OnMeetingStatusChanged(string value) => OnPropertyChanged(nameof(HasIdleNotice));

    partial void OnIsImportingChanged(bool value)
    {
        NotifyCommandState();
        OnPropertyChanged(nameof(HasIdleNotice));
    }

    private void NotifyCommandState()
    {
        StartQuickNoteCommand.NotifyCanExecuteChanged();
        ImportAudioCommand.NotifyCanExecuteChanged();
        StopRecordingCommand.NotifyCanExecuteChanged();
        PauseRecordingCommand.NotifyCanExecuteChanged();
        ResumeRecordingCommand.NotifyCanExecuteChanged();
    }
}

public sealed record MeetingListItem(
    string Id,
    string? FolderId,
    string Title,
    string Summary,
    string TimestampLabel,
    string DurationLabel,
    string StatusLabel,
    string ModelLabel,
    bool ShowStatusBadge,
    string? FolderName,
    string? SourceBadgeLabel,
    string? SourceBadgeGlyph)
{
    public DateTime Timestamp { get; init; }

    /// <summary>The folder badge renders only when the meeting lives in a folder, like the macOS
    /// browser's accent folder chip on each card.</summary>
    public bool ShowFolderBadge => FolderName is not null;

    public bool ShowSourceBadge => SourceBadgeLabel is not null;

    public bool HasDuration => !string.IsNullOrEmpty(DurationLabel);

    /// <summary>
    /// P3-01: what a screen reader announces for this row. Without it the ListView container peer
    /// falls back to the record's generated <c>ToString()</c> and reads the whole debug dump
    /// ("MeetingListItem { Id = ui-meeting-today, FolderId = … }"). The order matches the visual
    /// row: title, then the metadata line, then the badges, then the folder.
    /// </summary>
    public string AccessibleName
    {
        get
        {
            var parts = new List<string> { Title, TimestampLabel };
            if (HasDuration) parts.Add(DurationLabel);
            if (ShowStatusBadge) parts.Add(StatusLabel);
            if (ShowSourceBadge && SourceBadgeLabel is not null) parts.Add(SourceBadgeLabel);
            if (FolderName is not null) parts.Add($"in {FolderName}");
            return string.Join(", ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        }
    }

    /// <summary>Belt and braces for any UIA path that still falls back to the record's text.</summary>
    public override string ToString() => AccessibleName;

    public static MeetingListItem From(PersistedMeeting item, string? folderName)
    {
        var source = RecordingBadge(item);
        return new(
            item.Id,
            item.FolderId,
            string.IsNullOrWhiteSpace(item.Title) ? "Untitled meeting" : item.Title,
            string.IsNullOrWhiteSpace(item.Summary) ? "No summary saved" : SingleLine(item.Summary),
            item.CreatedAt.ToLocalTime().ToString("d MMM yyyy 'at' h:mm:ss tt"),
            FormatDuration(item.DurationMs),
            SessionStateLabel(item.SessionState, item.RecoveredFromInterruption),
            string.IsNullOrWhiteSpace(item.ModelProfile) ? "Local model" : item.ModelProfile,
            item.RecoveredFromInterruption ||
            item.SessionState is not (MeetingSessionState.Completed or MeetingSessionState.Idle),
            folderName,
            source?.Label,
            source?.Glyph)
        {
            Timestamp = item.CreatedAt
        };
    }

    private static string FormatDuration(long durationMs)
    {
        if (durationMs <= 0)
        {
            return "";
        }

        var duration = TimeSpan.FromMilliseconds(durationMs);
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return duration.Seconds > 0
                ? $"{duration.Minutes}m {duration.Seconds}s"
                : $"{duration.Minutes}m";
        }

        return $"{duration.Seconds}s";
    }

    /// <summary>
    /// A "Recording" chip mirrors the macOS source indicator for meetings that kept their capture
    /// tracks. Imported media is not labelled here because the persisted schema cannot reliably
    /// separate a user's imported file from a locally owned recording path without a source-kind
    /// discriminator.
    /// </summary>
    private static (string Label, string Glyph)? RecordingBadge(PersistedMeeting item)
    {
        if (string.IsNullOrWhiteSpace(item.MicrophoneAudioPath) && string.IsNullOrWhiteSpace(item.SystemAudioPath))
        {
            return null;
        }

        return ("Recording", "\uE983");
    }

    /// <summary>
    /// The status pill is only rendered when a meeting did not finish cleanly, matching the
    /// macOS browser where a badge means something to notice (Recovered / Needs attention /
    /// Cancelled) rather than labelling every completed item.
    /// </summary>
    private static string SessionStateLabel(MeetingSessionState state, bool recovered) => state switch
    {
        MeetingSessionState.Completed => recovered ? "Recovered" : "Completed",
        MeetingSessionState.Failed => "Needs attention",
        MeetingSessionState.RecoverableInterruption => "Recoverable",
        MeetingSessionState.Cancelled => "Cancelled",
        _ => state.ToString()
    };

    private static string SingleLine(string text) => text.Trim().Replace('\r', ' ').Replace('\n', ' ');
}

public sealed record RecoverableMeetingListItem(RecoverableMeetingSession Session, string Title, string Detail);

/// <summary>
/// One row of the meeting folder tree, used by both the shell rail and the in-page folder list so
/// the two surfaces cannot drift apart.
///
/// COUNT RULE (P3-04), applied identically on both surfaces:
///   * "All Meetings" counts every meeting in the library, <b>including meetings that are in no
///     folder</b>. It is therefore ≥ the sum of the folder rows, exactly as the macOS browser
///     reads 208 for All Meetings against 16 across its five folders.
///   * A folder row counts the meetings in that folder <b>and in all of its subfolders</b>,
///     resolved with <see cref="MeetingFolderHierarchy.Subtree"/> — the same helper the folder
///     filter uses, so the number and the filtered list always agree.
/// </summary>
public sealed record MeetingFolderListItem(string Id, string Name, int Count)
{
    public int Depth { get; init; }

    /// <summary>True when this folder has at least one subfolder, i.e. when its count rolls up.</summary>
    public bool HasChildren { get; init; }

    /// <summary>
    /// Name of the folder this one sits inside, or <c>null</c> at the top level. The duplicate-name
    /// rule deliberately allows two siblings of *different* parents to share a name, so a nested row
    /// is only distinguishable from its namesake by where it sits — which the visual list conveys
    /// through indentation and an announced name cannot. This carries that context.
    /// </summary>
    public string? ParentName { get; init; }

    public bool IsAllMeetings => Id.Length == 0;

    public string DisplayName => new string(' ', Math.Min(Depth, 20) * 2) + Name;
    public string CountLabel => Count.ToString("N0");

    private string CountPhrase => Count == 1 ? "1 meeting" : $"{Count:N0} meetings";

    /// <summary>
    /// Label for the "Parent folder" picker. The same row means "the whole library" when you are
    /// filtering and "no parent" when you are choosing where a folder sits, so it says which.
    /// </summary>
    public string ParentDisplayName => IsAllMeetings ? "None (top level)" : DisplayName;

    /// <summary>
    /// What the parent picker announces. <see cref="ParentDisplayName"/> shows nesting as leading
    /// spaces, which UIA trims away — so two legally-named "Notes" folders under different parents
    /// read identically in the picker unless the parent is spelled out.
    /// </summary>
    public string ParentPickerAccessibleName => IsAllMeetings
        ? ParentDisplayName
        : ParentName is null ? Name : $"{Name}, in {ParentName}";

    /// <summary>
    /// P3-01 + P3-04: the announced name states the count <i>and</i> the rule behind it, so the
    /// difference between the parent total and the sum of the folder rows is explained rather
    /// than left for the reader to infer. Nested rows also name their parent, because the
    /// duplicate-name rule permits a "Notes" inside "Archive" and a "Notes" inside "Research" to
    /// coexist and indentation is the only thing that told them apart.
    /// </summary>
    public string AccessibleName => IsAllMeetings
        ? $"{Name}, {CountPhrase}, including meetings in no folder"
        : HasChildren
            ? $"{Qualified}, {CountPhrase}, including subfolders"
            : $"{Qualified}, {CountPhrase}";

    private string Qualified => ParentName is null ? Name : $"{Name}, in {ParentName}";

    public override string ToString() => AccessibleName;

    /// <summary>
    /// The single builder for the folder tree. The shell rail and the Meetings page both call it,
    /// so nesting depth, labels and counts are the same numbers produced by the same rule on both
    /// surfaces rather than two hand-rolled loops that can drift (P3-04, P3-05).
    /// </summary>
    public static IReadOnlyList<MeetingFolderListItem> BuildTree(WinUiLibrarySnapshot snapshot)
    {
        var nodes = MeetingFolderHierarchy.Flatten(snapshot.Folders);
        var parentIds = snapshot.Folders
            .Where(folder => !string.IsNullOrEmpty(folder.ParentId))
            .Select(folder => folder.ParentId!)
            .ToHashSet(StringComparer.Ordinal);
        var nameById = snapshot.Folders
            .GroupBy(folder => folder.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);

        var result = new List<MeetingFolderListItem>
        {
            new("", "All Meetings", snapshot.MeetingCount)
        };

        foreach (var node in nodes)
        {
            var subtree = MeetingFolderHierarchy.Subtree(snapshot.Folders, node.Folder.Id);
            var count = snapshot.Meetings.Count(meeting =>
                meeting.FolderId is not null && subtree.Contains(meeting.FolderId));
            result.Add(new MeetingFolderListItem(node.Folder.Id, node.Folder.Name, count)
            {
                Depth = node.Depth,
                HasChildren = parentIds.Contains(node.Folder.Id),
                // Only when the row is actually rendered as nested. Flatten() falls back to depth 0
                // for a folder whose ParentId points at something that no longer exists, and naming
                // a parent the tree does not show would contradict the indentation.
                ParentName = node.Depth > 0 && node.Folder.ParentId is { } parentId &&
                             nameById.TryGetValue(parentId, out var parentName)
                    ? parentName
                    : null
            });
        }

        return result;
    }
}
