using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muesli.Windows.Services;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Core.Insights;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.WinUI.ViewModels;

public partial class TimelinePageViewModel : ObservableObject
{
    private readonly WinUiLibraryContext _library;
    private readonly IClipboardService _clipboard;
    private readonly IAppDialogService _dialogs;

    public TimelinePageViewModel(WinUiLibraryContext library, IClipboardService clipboard, IAppDialogService dialogs)
    {
        _library = library;
        _clipboard = clipboard;
        _dialogs = dialogs;
        Snapshot = library.ReadSnapshot();
        ApplyFilterAndSort();
    }

    // macOS TimelineView reuses DictationRowView: copy, copy CUA trace and delete on every dictation row.
    [RelayCommand]
    private async Task CopyAsync(WinUiTimelineEntry? entry, CancellationToken cancellationToken)
    {
        if (entry is null || entry.IsMeeting) return;
        try { await _clipboard.SetTextAsync(entry.Title, cancellationToken); }
        catch (Exception) { }
    }

    [RelayCommand]
    private async Task CopyTraceAsync(WinUiTimelineEntry? entry, CancellationToken cancellationToken)
    {
        if (entry?.Run is not { } run) return;
        try { await _clipboard.SetTextAsync(run.TraceText(entry.Title), cancellationToken); }
        catch (Exception) { }
    }

    [RelayCommand]
    private async Task DeleteAsync(WinUiTimelineEntry? entry, CancellationToken cancellationToken)
    {
        if (entry is null || entry.IsMeeting) return;
        var choice = await _dialogs.ConfirmAsync(
            "Are you sure you want to delete this dictation? This cannot be undone.",
            "Delete Dictation",
            cancellationToken);
        if (choice != AppDialogChoice.Primary) return;
        _library.DeleteDictation(entry.Id);
        Refresh();
    }

    [ObservableProperty]
    public partial WinUiLibrarySnapshot Snapshot { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<WinUiTimelineEntry> Entries { get; private set; } = [];

    /// <summary>
    /// Timeline sections mirror the desktop library: a small date heading followed by one
    /// divided surface containing the activity for that day, revealed a page at a time.
    /// </summary>
    public IncrementalGroups<TimelineGroup> Groups { get; } = new(group => group.Items.Count);

    /// <summary>macOS timeline date filter: every <see cref="HistoryFormat.DateFilters"/> range is offered.</summary>
    [ObservableProperty]
    public partial int DateFilterIndex { get; set; }

    /// <summary>macOS TargetApplicationFilterMenu: only dictations pasted into this app.</summary>
    [ObservableProperty]
    public partial DictationTargetApp? AppFilter { get; set; }

    /// <summary>The apps dictations were pasted into, for the app filter.</summary>
    public IReadOnlyList<DictationTargetApp> Apps { get; private set; } = [];

    public bool ShowAppFilter => Apps.Count > 0 || AppFilter is not null;

    public bool HasEntries => Entries.Count > 0;

    public string EmptyStateTitle => AppFilter is { } app ? $"No dictations for {app.Name}" : "No activity yet";

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

    public void Refresh()
    {
        Snapshot = _library.ReadSnapshot();
        ApplyFilterAndSort();
        NotifyDerived();
    }

    partial void OnSnapshotChanged(WinUiLibrarySnapshot value) => NotifyDerived();

    partial void OnDateFilterIndexChanged(int value) => ApplyFilterAndSort();

    partial void OnAppFilterChanged(DictationTargetApp? value) => ApplyFilterAndSort();

    partial void OnEntriesChanged(IReadOnlyList<WinUiTimelineEntry> value) => NotifyDerived();

    /// <summary>
    /// One chronological mixed feed, newest first, narrowed by the macOS date and app filters.
    /// P2-05 removed this page's own search field — the shell's single search entry point routes
    /// cross-library queries to <c>SearchPage</c>.
    /// </summary>
    private void ApplyFilterAndSort()
    {
        Apps = DictationTargetApps.Distinct(Snapshot.Timeline.Select(item => item.TargetApp));
        var start = HistoryFormat.DateFilterStart(DateFilterIndex, DateTime.Now);
        var query = Snapshot.Timeline.Where(item => start is null || item.Timestamp.ToLocalTime() >= start);
        if (AppFilter is { } app)
            query = query.Where(item => !item.IsMeeting && DictationTargetApps.Same(item.TargetApp, app));
        Entries = query.OrderByDescending(item => item.Timestamp).ToList();

        Groups.Reset(Entries
            .GroupBy(item => item.Timestamp.ToLocalTime().Date)
            .Select(group => new TimelineGroup(
                HistoryFormat.DayHeading(group.Key, DateTime.Today),
                group.ToList()))
            .ToList());
    }

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(Entries));
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(Apps));
        OnPropertyChanged(nameof(ShowAppFilter));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(ResultCountLabel));
        OnPropertyChanged(nameof(DictationWordsLabel));
        OnPropertyChanged(nameof(AveragePaceLabel));
    }
}

public sealed record TimelineGroup(string Name, IReadOnlyList<WinUiTimelineEntry> Items);

/// <summary>
/// Day groups shown a page of rows at a time. The history lists sit inside their page's
/// ScrollViewer, which turns ListView virtualization off, so building every row of a long history
/// made each visit to Timeline or Dictations take seconds. Pages call <see cref="ShowMore"/> as the
/// user nears the bottom; appending only builds the new rows.
/// </summary>
public sealed class IncrementalGroups<TGroup>(Func<TGroup, int> rowCount) : System.Collections.ObjectModel.ObservableCollection<TGroup>
{
    public const int PageRows = 40;
    private IReadOnlyList<TGroup> _all = [];

    public bool HasMore => Count < _all.Count;

    public void Reset(IReadOnlyList<TGroup> all)
    {
        _all = all;
        Clear();
        ShowMore();
    }

    public void ShowMore()
    {
        for (var rows = 0; rows < PageRows && Count < _all.Count;) rows += Add(_all[Count]);
    }

    /// <summary>Reveals groups up to and including the first one matching <paramref name="contains"/>.</summary>
    public void ShowThrough(Func<TGroup, bool> contains)
    {
        var index = _all.ToList().FindIndex(group => contains(group));
        while (Count <= index) Add(_all[Count]);
    }

    private new int Add(TGroup group)
    {
        base.Add(group);
        return Math.Max(1, rowCount(group));
    }
}

public partial class DictationsPageViewModel : ObservableObject, IDisposable
{
    private readonly WinUiLibraryContext _library;
    private readonly IClipboardService _clipboard;
    private readonly IAppDialogService _dialogs;
    private readonly WinUiDictationContext _dictation;
    private readonly IUiDispatcher _dispatcher;
    private IReadOnlyList<DictationListItem> _allItems = [];
    private IReadOnlyList<PersistedDictation> _allDictations = [];

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

    public IncrementalGroups<DictationGroup> Groups { get; } = new(group => group.Items.Count);

    /// <summary>macOS DictationsView date filter; only ranges the history can fill are offered.</summary>
    [ObservableProperty]
    public partial int DateFilterIndex { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<string> DateFilters { get; private set; } = [HistoryFormat.DateFilters[0]];

    /// <summary>macOS TargetApplicationFilterMenu: only dictations pasted into this app.</summary>
    [ObservableProperty]
    public partial DictationTargetApp? AppFilter { get; set; }

    public IReadOnlyList<DictationTargetApp> Apps { get; private set; } = [];

    public bool ShowAppFilter => Apps.Count > 0 || AppFilter is not null;

    public bool IsFiltered => DateFilterIndex > 0 || AppFilter is not null;

    public string EmptyStateTitle => AppFilter is { } app ? $"No dictations for {app.Name}" : "No dictations yet";

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

    /// <summary>macOS voiceNoteButton: records, transcribes and saves without pasting anywhere.</summary>
    public string RecordButtonLabel => IsRecording ? "Stop Voice Note" : "Record Voice Note";
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

    public void Refresh() => Reload();

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
        // macOS copies silently; only a failure is reported.
        try
        {
            await _clipboard.SetTextAsync(item.Text, cancellationToken);
        }
        catch (Exception exception)
        {
            ShowStatus($"Could not copy the dictation: {WinUiClipboardService.DescribeFailure(exception)}", isError: true);
        }
    }

    [RelayCommand]
    private async Task CopyTraceAsync(DictationListItem? item, CancellationToken cancellationToken)
    {
        if (item?.Run is not { } run) return;
        try
        {
            await _clipboard.SetTextAsync(run.TraceText(item.Text), cancellationToken);
        }
        catch (Exception exception)
        {
            ShowStatus($"Could not copy the trace: {WinUiClipboardService.DescribeFailure(exception)}", isError: true);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(DictationListItem? item, CancellationToken cancellationToken)
    {
        if (item is null) return;
        // macOS DictationRowView's alert, word for word.
        var choice = await _dialogs.ConfirmAsync(
            "Are you sure you want to delete this dictation? This cannot be undone.",
            "Delete Dictation",
            cancellationToken);
        if (choice != AppDialogChoice.Primary) return;

        try
        {
            if (_library.DeleteDictation(item.Id))
            {
                Reload();
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

    private ComputerUseRun? LoadRun(PersistedDictation item) =>
        item.ModelProfile == ComputerUseConfiguration.HistoryLabel ? ComputerUseRun.Load(_library.Profile.RootDirectory, item.Id) : null;

    partial void OnItemsChanged(IReadOnlyList<DictationListItem> value)
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ResultCountLabel));
    }

    partial void OnDateFilterIndexChanged(int value) => ApplyFilterAndSort();

    partial void OnAppFilterChanged(DictationTargetApp? value) => ApplyFilterAndSort();

    private void Reload()
    {
        var snapshot = _library.ReadSnapshot();
        _allDictations = snapshot.Dictations;
        _allItems = snapshot.Dictations.Select(item => DictationListItem.From(item) with
        {
            Run = LoadRun(item),
            TargetApp = snapshot.TargetApps.GetValueOrDefault(item.Id),
        }).ToList();
        Apps = DictationTargetApps.Distinct(_allItems.Select(item => item.TargetApp));
        var filters = HistoryFormat.AvailableDateFilters(
            _allItems.Count == 0 ? null : _allItems.Min(item => item.Timestamp.ToLocalTime()), DateTime.Now);
        if (!filters.SequenceEqual(DateFilters, StringComparer.Ordinal)) DateFilters = filters;
        if (DateFilterIndex >= DateFilters.Count) DateFilterIndex = 0;
        ApplyFilterAndSort();
    }

    /// <summary>
    /// Newest first, narrowed by the macOS date and app filters. P2-05 removed this page's own
    /// search field in favour of the shell's single search entry point.
    /// </summary>
    private void ApplyFilterAndSort()
    {
        var start = HistoryFormat.DateFilterStart(DateFilterIndex, DateTime.Now);
        Items = _allItems
            .Where(item => start is null || item.Timestamp.ToLocalTime() >= start)
            .Where(item => AppFilter is null || DictationTargetApps.Same(item.TargetApp, AppFilter))
            .OrderByDescending(item => item.Timestamp)
            .ToList();

        Groups.Reset(Items
            .GroupBy(item => item.Timestamp.ToLocalTime().Date)
            .Select(group => new DictationGroup(
                HistoryFormat.DayHeading(group.Key, DateTime.Today),
                group.ToList()))
            .ToList());

        // macOS filteredDictationStats: the cards describe the dictations the filters show, with
        // the same word, streak and pace rules as Timeline and Insights (LibraryMetrics, P2-01).
        var shown = Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var dictations = _allDictations.Where(item => shown.Contains(item.Id)).ToList();
        var words = dictations.Sum(item => LibraryMetrics.CountWords(item.Text));
        var pace = LibraryMetrics.AverageWordsPerMinute(words, dictations.Sum(item => Math.Max(0, item.DurationMs)));
        DictationWordsLabel = words.ToString("N0");
        CurrentStreakLabel = LibraryMetrics.CurrentStreakDays(dictations.Select(item => item.Timestamp.Date), DateTime.Today).ToString("N0");
        AveragePaceLabel = pace == 0 ? "—" : pace.ToString("N0");

        OnPropertyChanged(nameof(Apps));
        OnPropertyChanged(nameof(ShowAppFilter));
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(EmptyStateTitle));
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

    /// <summary>The run behind a Computer Use command, or null for a dictation.</summary>
    public ComputerUseRun? Run { get; init; }
    public bool IsComputerUse => ModelLabel == ComputerUseConfiguration.HistoryLabel;
    public bool HasRun => Run is not null;

    /// <summary>The app the dictation was pasted into, when known.</summary>
    public DictationTargetApp? TargetApp { get; init; }
    public bool HasTargetApp => TargetApp is not null;

    /// <summary>The text with macOS "Show more".</summary>
    public ExpandableText Body => field ??= new(Text);

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
    private readonly IClipboardService _clipboard;
    private string? _notesMeetingId;
    private bool _loadingNotes;
    private IReadOnlyList<MeetingListItem> _allItems = [];

    public MeetingsPageViewModel(
        WinUiLibraryContext library,
        WinUiMeetingContext meetingRuntime,
        WinUiMeetingDetectionService detection,
        IFilePickerService filePickers,
        IAppDialogService dialogs,
        IUiDispatcher dispatcher,
        Action showLiveTranscript,
        IClipboardService clipboard)
    {
        _library = library;
        _meetingRuntime = meetingRuntime;
        _detection = detection;
        _filePickers = filePickers;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _showLiveTranscript = showLiveTranscript;
        _clipboard = clipboard;
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

    [ObservableProperty] public partial string ManualNotes { get; set; } = "";
    [ObservableProperty] public partial string NotesSaveStatus { get; private set; } = "Saved";
    [ObservableProperty] public partial bool ShowLiveTab { get; set; }
    [ObservableProperty] public partial bool IsSourceMissing { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<MeetingLiveCaption> LiveCaptions { get; private set; } = [];
    [ObservableProperty] public partial string PartialYou { get; private set; } = "";
    [ObservableProperty] public partial string PartialOthers { get; private set; } = "";

    public bool ShowRecordingNotes => !ShowLiveTab;
    public bool CanEditRecordingNotes => _meetingRuntime.ActiveMeetingId is not null && CanStop;
    public bool HasLiveCaptions => LiveCaptions.Count > 0;
    public bool HasPartialYou => !string.IsNullOrWhiteSpace(PartialYou);
    public bool HasPartialOthers => !string.IsNullOrWhiteSpace(PartialOthers);
    public string LiveCaptionHint => _meetingRuntime.IsRollingPreview
        ? "Captions appear as the meeting model finishes each audio chunk."
        : "Captions appear as speech is recognized.";

    partial void OnShowLiveTabChanged(bool value) => OnPropertyChanged(nameof(ShowRecordingNotes));
    // Typing updates the in-memory draft (which Stop and discard already persist) and writes to
    // disk only after a pause.
    private CancellationTokenSource? _notesDebounce;

    partial void OnManualNotesChanged(string value)
    {
        if (_loadingNotes || !CanEditRecordingNotes) return;
        _meetingRuntime.UpdateManualNotesDraft(value);
        NotesSaveStatus = "Saving…";
        _notesDebounce?.Cancel();
        _notesDebounce = new CancellationTokenSource();
        _ = SaveNotesAfterPauseAsync(_notesDebounce.Token);
    }

    private async Task SaveNotesAfterPauseAsync(CancellationToken token)
    {
        try { await Task.Delay(700, token); }
        catch (OperationCanceledException) { return; }
        if (CanEditRecordingNotes) SaveRecordingNotes();
    }

    [RelayCommand(CanExecute = nameof(CanEditRecordingNotes))]
    private void SaveRecordingNotes()
    {
        try
        {
            _meetingRuntime.SaveManualNotes(ManualNotes);
            NotesSaveStatus = "Saved";
        }
        catch (Exception exception) { NotesSaveStatus = $"Could not save notes: {exception.Message}"; }
    }

    [RelayCommand] private void KeepRecording() => _meetingRuntime.KeepRecording();

    [RelayCommand]
    private async Task CopyLiveTranscriptAsync(CancellationToken cancellationToken)
    {
        try { await _clipboard.SetTextAsync(LiveTranscript, cancellationToken); }
        catch (Exception exception) { MeetingStatus = $"Could not copy transcript: {WinUiClipboardService.DescribeFailure(exception)}"; }
    }

    [RelayCommand]
    private async Task CopyLiveCaptionAsync(MeetingLiveCaption? caption, CancellationToken cancellationToken)
    {
        if (caption is null) return;
        try { await _clipboard.SetTextAsync(caption.Text, cancellationToken); }
        catch (Exception exception) { MeetingStatus = $"Could not copy caption: {WinUiClipboardService.DescribeFailure(exception)}"; }
    }

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

    public bool IsActive => IsRecording || IsBusy || IsPaused;
    public bool CanStart => !IsActive && !IsImporting;
    public bool CanStop => (IsRecording || IsPaused) && !IsBusy;
    public bool CanPause => IsRecording && !IsBusy;
    public bool CanResume => IsPaused && !IsBusy;
    public bool HasRecoverable => RecoverableSessions.Count > 0;

    public bool HasItems => Items.Count > 0;

    /// <summary>Only a failure is shown; macOS says nothing when an action works.</summary>
    public bool HasIdleNotice => !IsActive && !IsImporting && IsFailure(MeetingStatus);

    private static bool IsFailure(string status) =>
        status.StartsWith("Could not", StringComparison.Ordinal) ||
        status.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("without a transcript", StringComparison.Ordinal) ||
        status.Contains("cancelled", StringComparison.OrdinalIgnoreCase);

    /// <summary>macOS browserHeaderMeta: "N meeting(s)".</summary>
    public string ResultCountLabel => Items.Count == 1 ? "1 meeting" : $"{Items.Count:N0} meetings";

    /// <summary>The meeting being recorded, which the banner's Open Notes and a Quick Note open.</summary>
    public string? ActiveMeetingId => _meetingRuntime.ActiveMeetingId;
    public bool HasActiveMeeting => ActiveMeetingId is not null && (IsRecording || IsPaused || IsBusy);
    public bool ShowBanner => IsActive || IsImporting || IsStarting;

    [ObservableProperty] public partial bool IsStarting { get; private set; }
    [ObservableProperty] public partial string BannerTitle { get; private set; } = "";

    /// <summary>macOS activeMeetingStatusText: Recording now / Recording paused / Finalizing notes.</summary>
    public string BannerStatus =>
        IsImporting ? MeetingStatus
        : IsStarting && !IsActive ? "Meeting transcription will start shortly."
        : IsPaused ? _meetingRuntime.FinalizationFailed ? "Saving failed · audio kept. Choose Stop to retry." : "Recording paused"
        : IsRecording ? "Recording now"
        : "Finalizing notes";

    /// <summary>Current sort, shown on the compact sort pill (MeetingsView.swift parity).</summary>
    public string SortLabel => SortIndex == 1 ? "Oldest first" : "Newest first";

    /// <summary>Newest-first is the neutral default; a non-default sort gets the accent pill.</summary>
    public bool IsDefaultSort => SortIndex != 1;

    public string TimeRangeLabel =>
        TimeRangeIndex >= 0 && TimeRangeIndex < TimeRangeOptions.Count ? TimeRangeOptions[TimeRangeIndex] : "All time";

    /// <summary>At All time the date pill is neutral (no label), matching macOS.</summary>
    public bool IsAllTimeRange => TimeRangeIndex <= 0;

    private string _currentFolderLabel = "All Meetings";

    /// <summary>
    /// The subtitle is the current folder name. The page title stays "Meetings", matching the
    /// macOS browser where the folder is the line under the page heading.
    /// </summary>
    public string CurrentFolderLabel => _currentFolderLabel;

    public string EmptyStateTitle =>
        string.IsNullOrWhiteSpace(SelectedFolderId) ? "No meetings yet" : "No meetings in this folder";

    public string EmptyStateInstruction =>
        TimeRangeIndex > 0 ? "Try another time range or folder."
        : string.IsNullOrWhiteSpace(SelectedFolderId) ? "Start a Quick Note or import audio to create your first meeting note."
        : "Choose another folder or move a meeting here from the browser.";

    /// <summary>macOS person.2.wave.2 for the library, folder inside one.</summary>
    public string EmptyStateGlyph => string.IsNullOrWhiteSpace(SelectedFolderId) ? "\uE716" : "\uE8B7";

    public void Refresh() => Reload();

    /// <summary>Raised once a Quick Note is recording, so the page can open its notes as macOS does.</summary>
    public event EventHandler? QuickNoteStarted;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartQuickNoteAsync(CancellationToken cancellationToken)
    {
        try
        {
            IsStarting = true;
            BannerTitle = "Quick Note";
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
            IsStarting = false;
            UpdateRuntimeState();
        }
        if (HasActiveMeeting) QuickNoteStarted?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopRecordingAsync(CancellationToken cancellationToken)
    {
        try
        {
            MeetingStatus = "Finalizing transcript…";
            var meeting = await _meetingRuntime.StopAndSaveAsync(cancellationToken);
            Reload();
            MeetingStatus = meeting.SessionState == MeetingSessionState.Failed || string.IsNullOrWhiteSpace(meeting.Transcript)
                ? "Meeting saved without a transcript"
                : "Meeting saved";
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

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task DiscardRecordingAsync()
    {
        try
        {
            var status = await _meetingRuntime.ConfirmAndDiscardAsync(_dialogs, !string.IsNullOrWhiteSpace(ManualNotes));
            if (status is null) return;
            Reload();
            MeetingStatus = status;
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
            BannerTitle = picked.DisplayName;
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

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task DiscardRecoveryAsync(RecoverableMeetingListItem? item)
    {
        if (item is null) return;
        var choice = await _dialogs.ConfirmAsync("Discard this recovered audio? Saved written notes and any original meeting will be kept.",
            "Discard recovered recording", "Discard audio", "Cancel");
        if (choice != AppDialogChoice.Primary) return;
        try
        {
            await _meetingRuntime.DiscardRecoveryAsync(item.Session);
            Reload();
            MeetingStatus = "Recovered audio discarded. Saved writing kept.";
        }
        catch (Exception exception) { MeetingStatus = $"Could not discard recovered audio: {exception.Message}"; }
        finally { UpdateRuntimeState(); }
    }

    partial void OnItemsChanged(IReadOnlyList<MeetingListItem> value)
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ResultCountLabel));
    }

    partial void OnIsStartingChanged(bool value) => NotifyBanner();

    private void NotifyBanner()
    {
        OnPropertyChanged(nameof(ShowBanner));
        OnPropertyChanged(nameof(BannerStatus));
        OnPropertyChanged(nameof(HasActiveMeeting));
        OnPropertyChanged(nameof(ActiveMeetingId));
    }

    /// <summary>MeetingListItemView folder popover: a folder, or null for Unfiled.</summary>
    public void MoveMeeting(string meetingId, string? folderId)
    {
        if (_library.FindMeeting(meetingId) is not { } meeting) return;
        try
        {
            if (_library.UpdateMeeting(meeting with { FolderId = string.IsNullOrWhiteSpace(folderId) ? null : folderId })) Reload();
        }
        catch (Exception exception) { MeetingStatus = $"Could not move the meeting: {exception.Message}"; }
    }

    /// <summary>MeetingListItemView "New Folder...": create a top-level folder and move the meeting into it.</summary>
    public void CreateFolderAndMove(string meetingId, string name)
    {
        try
        {
            var folder = _library.SaveMeetingFolder(null, name);
            MoveMeeting(meetingId, folder.Id);
        }
        catch (Exception exception) { MeetingStatus = $"Could not create the folder: {exception.Message}"; }
    }

    public async Task DeleteMeetingAsync(string meetingId)
    {
        var choice = await _dialogs.ConfirmAsync(
            "Are you sure you want to delete this meeting? Saved notes, transcript, and any retained recording will be removed.",
            "Delete Meeting");
        if (choice != AppDialogChoice.Primary) return;
        try
        {
            if (_library.DeleteMeeting(meetingId)) Reload();
        }
        catch (Exception exception) { MeetingStatus = $"Could not delete the meeting: {exception.Message}"; }
    }

    partial void OnSortIndexChanged(int value)
    {
        ApplyFilterAndSort();
        OnPropertyChanged(nameof(SortLabel));
        OnPropertyChanged(nameof(IsDefaultSort));
    }

    /// <summary>
    /// P3-06: an index of -1 is not a range the user can pick, so it is normalised back to
    /// "All time" instead of leaving the date filter with no visible value.
    /// </summary>
    partial void OnTimeRangeIndexChanged(int value)
    {
        if (value < 0 && TimeRangeOptions.Count > 0)
        {
            TimeRangeIndex = 0;
            return;
        }

        ApplyFilterAndSort();
        OnPropertyChanged(nameof(TimeRangeLabel));
        OnPropertyChanged(nameof(IsAllTimeRange));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateInstruction));
    }

    public void SelectFolder(string? folderId)
    {
        SelectedFolderId = folderId ?? "";
        UpdateFolderContext();
        ApplyFilterAndSort();
    }

    private void UpdateFolderContext()
    {
        _currentFolderLabel = Folders.FirstOrDefault(folder => folder.Id == SelectedFolderId)?.Name ?? "All Meetings";
        OnPropertyChanged(nameof(CurrentFolderLabel));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateInstruction));
        OnPropertyChanged(nameof(EmptyStateGlyph));
    }

    private void Reload()
    {
        var snapshot = _library.ReadSnapshot();
        var folderNameById = snapshot.Folders
            .GroupBy(folder => folder.Id)
            .ToDictionary(group => group.Key, group => group.First().Name);
        var (predecessors, parents) = _library.ThreadLinks(snapshot.Meetings.Select(item => item.Id));
        _allItems = snapshot.Meetings.Select(item => MeetingListItem.From(
            item,
            FolderPath(snapshot.Folders, item.FolderId)) with
        {
            IsFollowUp = predecessors.Contains(item.Id),
            HasFollowUps = parents.Contains(item.Id)
        }).ToList();
        Folders = MeetingFolderListItem.BuildTree(snapshot);
        var desiredTimeRange = TimeRangeIndex < 0 ? 0 : TimeRangeIndex;

        // Only republish the range options when they actually changed. Handing a ComboBox an equal
        // but new list is what made it drop its selection on every refresh in the first place.
        var options = BuildTimeRangeOptions(snapshot.Meetings);
        if (!options.SequenceEqual(TimeRangeOptions, StringComparer.Ordinal))
        {
            TimeRangeOptions = options;
        }

        TimeRangeIndex = desiredTimeRange >= TimeRangeOptions.Count ? 0 : desiredTimeRange;
        ApplyFilterAndSort();
        UpdateFolderContext();
    }

    /// <summary>
    /// The macOS browser only lists ranges the history can actually satisfy: "Last 2 days" needs
    /// a meeting at least a day old, "Last week" needs three days, and so on. Reproducing that
    /// keeps the filter truthful instead of offering ranges that are guaranteed empty.
    /// </summary>
    /// <summary>macOS currentFolderName breadcrumb: "Grandparent / Parent / Folder".</summary>
    public static string? FolderPath(IReadOnlyList<PersistedMeetingFolder> folders, string? folderId)
    {
        var byId = folders.GroupBy(folder => folder.Id).ToDictionary(group => group.Key, group => group.First());
        if (string.IsNullOrWhiteSpace(folderId) || !byId.TryGetValue(folderId, out var folder)) return null;
        var parts = new List<string> { folder.Name };
        var seen = new HashSet<string> { folder.Id };
        while (folder.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent) && seen.Add(parentId))
        {
            parts.Insert(0, parent.Name);
            folder = parent;
        }
        return string.Join(" / ", parts);
    }

    private static IReadOnlyList<string> BuildTimeRangeOptions(IReadOnlyList<PersistedMeeting> meetings) =>
        HistoryFormat.AvailableDateFilters(meetings.Count == 0 ? null : meetings.Min(meeting => meeting.CreatedAt), DateTime.Now);

    private void ApplyFilterAndSort()
    {
        IEnumerable<MeetingListItem> query = _allItems;
        if (!string.IsNullOrWhiteSpace(SelectedFolderId))
        {
            var subtree = MeetingFolderHierarchy.Subtree(_library.History.LoadMeetingFolders(), SelectedFolderId);
            query = query.Where(item => item.FolderId is not null && subtree.Contains(item.FolderId));
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

    private static DateTime? TimeRangeThreshold(int index) => HistoryFormat.DateFilterStart(index, DateTime.Now);

    private void UpdateRuntimeState()
    {
        var wasActive = IsActive;
        IsRecording = _meetingRuntime.IsRecording;
        IsBusy = _meetingRuntime.IsBusy;
        IsPaused = _meetingRuntime.IsPaused;
        LiveTranscript = _meetingRuntime.LiveTranscript;
        IsSourceMissing = _meetingRuntime.IsSourceMissing;
        if (_meetingRuntime.ActiveMeetingId is { } id && id != _notesMeetingId)
        {
            _notesMeetingId = id;
            _loadingNotes = true;
            ManualNotes = _meetingRuntime.ManualNotes;
            _loadingNotes = false;
            NotesSaveStatus = _meetingRuntime.AreNotesSaved ? "Saved" : "Notes have not been saved. Retry saving.";
            ShowLiveTab = false;
            LiveCaptions = [];
        }
        var snapshot = _meetingRuntime.LatestLiveSnapshot;
        if ((snapshot?.Committed.Count ?? 0) != LiveCaptions.Count)
            LiveCaptions = snapshot?.Committed.OrderBy(segment => segment.StartSample)
                .Select(segment => new MeetingLiveCaption(segment.Text, segment.Channel == LiveTranscriptChannel.Microphone))
                .ToArray() ?? [];
        PartialYou = snapshot?.PartialMicrophone ?? "";
        PartialOthers = snapshot?.PartialSystem ?? "";
        OnPropertyChanged(nameof(HasLiveCaptions));
        OnPropertyChanged(nameof(HasPartialYou));
        OnPropertyChanged(nameof(HasPartialOthers));
        OnPropertyChanged(nameof(LiveCaptionHint));
        OnPropertyChanged(nameof(CanEditRecordingNotes));
        SaveRecordingNotesCommand.NotifyCanExecuteChanged();
        CaptureStatus = _meetingRuntime.CaptureStatus;
        DetectionStatus = _detection.Status;
        RecoverableSessions = _meetingRuntime.RecoverableSessions
            .Select(session => new RecoverableMeetingListItem(
                session,
                string.IsNullOrWhiteSpace(session.Journal.Title) ? "Recoverable recording" : session.Journal.Title,
                $"{session.Journal.StartedAtUtc.ToLocalTime():g} · {session.MicrophonePartCount} mic part(s) · {session.SystemPartCount} system part(s)"))
            .ToList();
        if (IsRecording) MeetingStatus = $"Recording · {_meetingRuntime.ActiveTitle}";
        else if (IsPaused) MeetingStatus = _meetingRuntime.FinalizationFailed
            ? "Saving failed · audio kept. Choose Stop to retry."
            : "Paused · audio retained";
        if (wasActive && !IsActive) Reload();
        if (IsActive && _meetingRuntime.ActiveTitle is { Length: > 0 } title) BannerTitle = title;
        NotifyBanner();
        NotifyCommandState();
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(HasRecoverable));
        OnPropertyChanged(nameof(HasIdleNotice));
    }

    partial void OnMeetingStatusChanged(string value)
    {
        OnPropertyChanged(nameof(HasIdleNotice));
        OnPropertyChanged(nameof(BannerStatus));
    }

    partial void OnIsImportingChanged(bool value)
    {
        NotifyBanner();
        NotifyCommandState();
        OnPropertyChanged(nameof(HasIdleNotice));
    }

    private void NotifyCommandState()
    {
        StartQuickNoteCommand.NotifyCanExecuteChanged();
        DiscardRecoveryCommand.NotifyCanExecuteChanged();
        ImportAudioCommand.NotifyCanExecuteChanged();
        StopRecordingCommand.NotifyCanExecuteChanged();
        DiscardRecordingCommand.NotifyCanExecuteChanged();
        PauseRecordingCommand.NotifyCanExecuteChanged();
        ResumeRecordingCommand.NotifyCanExecuteChanged();
    }
}

public sealed record MeetingLiveCaption(string Text, bool IsYou)
{
    public string Speaker => IsYou ? "You" : "Others";
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

    /// <summary>Badge colours, as macOS MeetingStatus.displayColor: success, warning, tertiary or accent.</summary>
    public string StatusTone { get; init; } = "tertiary";
    public string SourceTone { get; init; } = "secondary";
    public string SourceBadgeHelp { get; init; } = "";
    public bool IsFollowUp { get; init; }
    public bool HasFollowUps { get; init; }

    /// <summary>macOS folder.fill when filed, folder.badge.plus when not.</summary>
    public string FolderGlyph => FolderName is null ? "\uE8F4" : "\uE8B7";

    /// <summary>The folder badge renders only when the meeting lives in a folder, like the macOS
    /// browser's accent folder chip on each card.</summary>
    public bool ShowFolderBadge => FolderName is not null;

    public bool ShowSourceBadge => SourceBadgeLabel is not null;

    public bool HasDuration => !string.IsNullOrEmpty(DurationLabel);

    /// <summary>macOS formatMeta: "time  •  duration".</summary>
    public string MetaLabel => HasDuration ? $"{TimestampLabel}  •  {DurationLabel}" : TimestampLabel;

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
        var imported = item.SystemCaptureMode == "imported-media";
        var source = imported ? ("Imported", "\uE896") : RecordingBadge(item);
        return new(
            item.Id,
            item.FolderId,
            string.IsNullOrWhiteSpace(item.Title) ? "Untitled meeting" : item.Title,
            // macOS previewText: written notes for an unfinished meeting, else notes, else transcript.
            MeetingPreviewText.Snippet(
                !string.IsNullOrWhiteSpace(item.ManualNotes) && item.SessionState != MeetingSessionState.Completed ? item.ManualNotes
                : string.IsNullOrWhiteSpace(item.Summary) ? item.Transcript : item.Summary),
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
            Timestamp = item.CreatedAt,
            StatusTone = item.SessionState switch
            {
                MeetingSessionState.Completed => "success",
                MeetingSessionState.Failed or MeetingSessionState.RecoverableInterruption => "warning",
                _ => "tertiary"
            },
            SourceTone = imported ? "accent" : "secondary",
            SourceBadgeHelp = imported ? "Imported audio" : "Saved recording available"
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

        return ("Recording", "\uE8D6"); // Audio, standing in for the macOS waveform symbol
    }

    /// <summary>
    /// The status pill is only rendered when a meeting did not finish cleanly, matching the
    /// macOS browser where a badge means something to notice (Recovered / Needs attention /
    /// Cancelled) rather than labelling every completed item.
    /// </summary>
    private static string SessionStateLabel(MeetingSessionState state, bool recovered) => state switch
    {
        MeetingSessionState.Completed => recovered ? "Recovered" : "Completed",
        MeetingSessionState.NoteOnly => "Note only",
        MeetingSessionState.Failed => "Needs attention",
        MeetingSessionState.RecoverableInterruption => "Recoverable",
        MeetingSessionState.Cancelled => "Cancelled",
        _ => state.ToString()
    };
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
