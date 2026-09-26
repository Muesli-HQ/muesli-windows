using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muesli.Windows.Core.Insights;
using Muesli.Windows.WinUI.Services;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.ViewModels;

public partial class InsightsPageViewModel(
    WinUiLibraryContext library,
    IClipboardService clipboard,
    IFilePickerService filePickers,
    IShareService share) : ObservableObject
{
    private InsightsShareCardData _shareData = new("12 months", 0, 0, 0, 0, 0, 0, 0);
    private string? _previewFile;
    [ObservableProperty] public partial int RangeIndex { get; set; } = 2;
    [ObservableProperty] public partial int ActivityModeIndex { get; set; }
    [ObservableProperty] public partial string TotalWords { get; private set; } = "0";
    [ObservableProperty] public partial string Meetings { get; private set; } = "0";
    [ObservableProperty] public partial string AveragePace { get; private set; } = "—";
    [ObservableProperty] public partial string LifetimeTotalWords { get; private set; } = "0";
    [ObservableProperty] public partial string LifetimeMeetings { get; private set; } = "0";
    [ObservableProperty] public partial string LifetimeAveragePace { get; private set; } = "—";
    [ObservableProperty] public partial string CurrentStreak { get; private set; } = "0 days";
    [ObservableProperty] public partial string LongestStreak { get; private set; } = "0 days";
    [ObservableProperty] public partial string ActiveDays { get; private set; } = "0";
    [ObservableProperty] public partial string DictationBreakdown { get; private set; } = "0 words";
    [ObservableProperty] public partial string MeetingBreakdown { get; private set; } = "0 words";
    [ObservableProperty] public partial string DictationSessions { get; private set; } = "0";
    [ObservableProperty] public partial string CompletedMeetings { get; private set; } = "0";
    [ObservableProperty] public partial string SelectedAveragePace { get; private set; } = "—";
    [ObservableProperty] public partial string SelectedActiveDays { get; private set; } = "0";
    [ObservableProperty] public partial string StreakNumber { get; private set; } = "0";
    [ObservableProperty] public partial string StreakSummary { get; private set; } = "Dictate today to start a new streak.";
    [ObservableProperty] public partial string StreakBestLabel { get; private set; } = "Best: 0 days";
    [ObservableProperty] public partial string StreakActiveDaysLabel { get; private set; } = "0 active days";
    [ObservableProperty] public partial string ActivityModeLabel { get; private set; } = "Words";
    [ObservableProperty] public partial double DictationShare { get; private set; }
    [ObservableProperty] public partial double MeetingShare { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<InsightsDayItem> Days { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<InsightsMonthItem> MonthLabels { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<InsightsWeekdayItem> WeekdayLabels { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<InsightsWordItem> DictationWords { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<InsightsWordItem> MeetingWords { get; private set; } = [];
    [ObservableProperty] public partial Uri? PreviewUri { get; private set; }
    [ObservableProperty] public partial bool HasPreview { get; private set; }
    [ObservableProperty] public partial string StatusMessage { get; private set; } = "";
    [ObservableProperty] public partial bool IsStatusOpen { get; set; }
    [ObservableProperty] public partial bool IsStatusError { get; private set; }
    [ObservableProperty] public partial bool IsLoading { get; private set; }
    [ObservableProperty] public partial bool HasData { get; private set; }
    [ObservableProperty] public partial bool HasLoadError { get; private set; }

    public bool IsEmpty => !IsLoading && !HasLoadError && !HasData;

    public bool HasDictationWords => DictationWords.Count > 0;
    public bool HasMeetingWords => MeetingWords.Count > 0;

    partial void OnDictationWordsChanged(IReadOnlyList<InsightsWordItem> value) => OnPropertyChanged(nameof(HasDictationWords));
    partial void OnMeetingWordsChanged(IReadOnlyList<InsightsWordItem> value) => OnPropertyChanged(nameof(HasMeetingWords));
    partial void OnHasDataChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnHasLoadErrorChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    public void Load() => Reload();

    [RelayCommand]
    private void Refresh() => Reload();

    [RelayCommand]
    private void PreviewShare()
    {
        try
        {
            var path = PreviewPath();
            InsightsShareImageService.SavePng(path, _shareData);
            PreviewUri = new Uri(path);
            HasPreview = true;
            ShowStatus("Share image preview generated without transcript text.");
        }
        catch (Exception exception) { ShowStatus($"Could not create the share image: {exception.Message}", true); }
    }

    [RelayCommand]
    private async Task CopyShareAsync(CancellationToken cancellationToken)
    {
        try
        {
            var path = PreviewPath();
            InsightsShareImageService.SavePng(path, _shareData);
            await clipboard.SetImageFileAsync(path, cancellationToken);
            PreviewUri = new Uri(path);
            HasPreview = true;
            ShowStatus("Activity image copied to the clipboard.");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ShowStatus($"Could not copy the activity image: {exception.Message}", true); }
    }

    [RelayCommand]
    private async Task SaveShareAsync(CancellationToken cancellationToken)
    {
        try
        {
            var picked = await filePickers.PickSaveFileAsync(
                new FilePickerRequest("Save activity image", [".png"], "muesli-activity.png"),
                cancellationToken);
            if (picked is null) return;
            InsightsShareImageService.SavePng(picked.Path, _shareData);
            ShowStatus($"Activity image saved to {picked.DisplayName}.");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ShowStatus($"Could not save the activity image: {exception.Message}", true); }
    }

    [RelayCommand]
    private async Task ShareAsync(CancellationToken cancellationToken)
    {
        try
        {
            var path = PreviewPath();
            InsightsShareImageService.SavePng(path, _shareData);
            await share.ShareFileAsync(
                path,
                "My Muesli activity",
                "Transcript-free local activity summary",
                cancellationToken);
            ShowStatus("Windows sharing opened.");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ShowStatus($"Could not open Windows sharing: {exception.Message}", true); }
    }

    partial void OnRangeIndexChanged(int value) => Reload();
    partial void OnActivityModeIndexChanged(int value) => Reload();

    private void Reload()
    {
        IsLoading = true;
        HasLoadError = false;
        OnPropertyChanged(nameof(IsEmpty));
        try
        {
            var source = library.ReadSnapshot();
            var range = RangeIndex switch
            {
                0 => InsightsRange.ThirtyDays,
                1 => InsightsRange.NinetyDays,
                2 => InsightsRange.TwelveMonths,
                _ => InsightsRange.AllTime
            };
            var snapshot = InsightsWordAnalyzer.Build(
                source.Dictations.Select(item => (item.Timestamp, item.Text, item.DurationMs)),
                source.Meetings.Select(item => (item.CreatedAt, item.Transcript)),
                range,
                DateTime.Now);
        TotalWords = snapshot.Selected.TotalWords.ToString("N0");
        Meetings = snapshot.Selected.Meetings.ToString("N0");
        AveragePace = snapshot.Selected.AverageWpm <= 0 ? "—" : $"{snapshot.Selected.AverageWpm:N0} WPM";
        SelectedAveragePace = AveragePace;
        LifetimeTotalWords = snapshot.Lifetime.TotalWords.ToString("N0");
        LifetimeMeetings = snapshot.Lifetime.Meetings.ToString("N0");
        LifetimeAveragePace = snapshot.Lifetime.AverageWpm <= 0 ? "—" : $"{snapshot.Lifetime.AverageWpm:N0} WPM";
        CurrentStreak = Count(snapshot.CurrentStreakDays, "day", "days");
        LongestStreak = Count(snapshot.LongestStreakDays, "day", "days");
        ActiveDays = snapshot.ActiveDaysInRange.ToString("N0");
        SelectedActiveDays = ActiveDays;
        StreakNumber = snapshot.CurrentStreakDays.ToString("N0");
        StreakBestLabel = $"Best: {Count(snapshot.LongestStreakDays, "day", "days")}";
        StreakActiveDaysLabel = snapshot.ActiveDaysInRange == 1
            ? "1 active day"
            : $"{snapshot.ActiveDaysInRange:N0} active days";
        StreakSummary = snapshot.CurrentStreakDays switch
        {
            > 0 when snapshot.CurrentStreakDays == snapshot.LongestStreakDays => "This is your longest streak so far.",
            > 0 => $"Your longest streak is {Count(snapshot.LongestStreakDays, "day", "days")}.",
            _ => "Dictate today to start a new streak."
        };
        DictationSessions = snapshot.Selected.DictationSessions.ToString("N0");
        CompletedMeetings = snapshot.Selected.Meetings.ToString("N0");
        DictationBreakdown =
            $"{Count(snapshot.Selected.DictationWords, "word", "words")} · " +
            $"{Count(snapshot.Selected.DictationSessions, "session", "sessions")}";
        MeetingBreakdown =
            $"{Count(snapshot.Selected.MeetingWords, "word", "words")} · " +
            $"{Count(snapshot.Selected.Meetings, "meeting", "meetings")}";
        var total = Math.Max(1, snapshot.Selected.TotalWords);
        DictationShare = snapshot.Selected.DictationWords / (double)total;
        MeetingShare = snapshot.Selected.MeetingWords / (double)total;
        ActivityModeLabel = ActivityModeIndex == 1 ? "Meetings" : "Words";
        var peak = Math.Max(1, snapshot.DailyActivity
            .Select(day => ActivityModeIndex == 1 ? day.Meetings : day.Words)
            .DefaultIfEmpty(0)
            .Max());
        var cells = snapshot.DailyActivity.Select(day => new InsightsDayItem(
            day.Date.ToString("MMM d, yyyy"),
            day.Words,
            day.Meetings,
                HeatLevel(ActivityModeIndex == 1 ? day.Meetings : day.Words, peak))).ToList();
        var heatmapLead = HeatmapLead(snapshot.DailyActivity);
        Days = AlignHeatmapToMonday(heatmapLead, cells);
        MonthLabels = BuildMonthLabels(snapshot.DailyActivity, heatmapLead);
        WeekdayLabels = [
            new InsightsWeekdayItem("Mon"),
            new InsightsWeekdayItem(""),
            new InsightsWeekdayItem("Wed"),
            new InsightsWeekdayItem(""),
            new InsightsWeekdayItem("Fri"),
            new InsightsWeekdayItem(""),
            new InsightsWeekdayItem("")];
        DictationWords = BuildWords(snapshot.DictationWords);
        MeetingWords = BuildWords(snapshot.MeetingWords);
        var rangeLabel = RangeIndex switch { 0 => "30 days", 1 => "90 days", 2 => "12 months", _ => "All time" };
        _shareData = new InsightsShareCardData(
            rangeLabel,
            snapshot.Selected.TotalWords,
            snapshot.Selected.Meetings,
            (int)Math.Round(snapshot.Selected.AverageWpm),
            snapshot.CurrentStreakDays,
            snapshot.ActiveDaysInRange,
            snapshot.Selected.DictationWords,
            snapshot.Selected.MeetingWords);
        HasPreview = false;
        PreviewUri = null;
        HasData = snapshot.Selected.TotalWords > 0 || snapshot.Selected.Meetings > 0;
        }
        catch (Exception exception)
        {
            HasData = false;
            HasLoadError = true;
            ShowStatus($"Insights could not be loaded: {exception.Message}", true);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>
    /// "1 meeting" rather than "1 meetings" - the same singular-form gap Prompt 2 fixed on Search
    /// (P2-06), which these labels still had.
    /// </summary>
    internal static string Count(int value, string singular, string plural) =>
        $"{value:N0} {(value == 1 ? singular : plural)}";

    private static IReadOnlyList<InsightsWordItem> BuildWords(IReadOnlyList<InsightWordCount> words)
    {
        if (words.Count == 0)
        {
            return [];
        }

        var high = words[0].Count;
        var low = words[^1].Count;
        return words.Select(item => new InsightsWordItem(
            item.Word,
            item.Count,
            WordFontSize(item.Count, high, low),
            item.Count == high)).ToList();
    }

    private static double WordFontSize(int count, int high, int low)
    {
        if (high <= low)
        {
            return 18;
        }

        var ratio = Math.Log(count - low + 1) / Math.Log(high - low + 1);
        return 13 + (ratio * 20);
    }

    /// <summary>
    /// Discrete five-level heatmap matching macOS <c>InsightsPalette.intensity</c>: level 0 is the
    /// quiet cell (surface at 62%), levels 1-2 are accent at 24%/48%, and levels 3-4 are cyan at
    /// 67%/95%. The level is exposed so the page can apply the distinct palette rather than one
    /// blue brush at four opacities.
    /// </summary>
    private static int HeatLevel(int count, int peak)
    {
        if (count <= 0)
        {
            return 0;
        }

        var ratio = Math.Log(count + 1) / Math.Log(Math.Max(1, peak) + 1);
        return Math.Min(4, Math.Max(1, (int)Math.Ceiling(ratio * 4)));
    }

    /// <summary>
    /// Column pitch of the activity grid: <c>UniformGridLayout.MinItemWidth</c> 16 plus
    /// <c>MinColumnSpacing</c> 2 in <c>Pages/InsightsPage.xaml</c>. Month label widths are derived
    /// from it so a label spans exactly the columns its month occupies.
    /// </summary>
    private const double HeatmapColumnPitch = 18;

    /// <summary>
    /// Number of placeholder cells needed before the first real day so Monday is the top row.
    /// </summary>
    private static int HeatmapLead(IReadOnlyList<InsightsDailyActivity> days) =>
        days.Count == 0 ? 0 : ((int)days[0].Date.DayOfWeek + 6) % 7;

    /// <summary>
    /// Leading empty cells so Monday is the first row, matching the macOS contribution heatmap.
    /// Placeholders stay in the grid so later weeks do not shift up.
    /// </summary>
    private static IReadOnlyList<InsightsDayItem> AlignHeatmapToMonday(int lead, IReadOnlyList<InsightsDayItem> days) =>
        lead <= 0 || days.Count == 0
            ? days
            : Enumerable.Repeat(InsightsDayItem.Placeholder, lead).Concat(days).ToList();

    /// <summary>
    /// One label per month, each as wide as the grid columns that month actually occupies.
    /// </summary>
    /// <remarks>
    /// The previous fixed 72 DIP per month drifted against the cells, because a month spans four
    /// or five 7-day columns (72-90 DIP) depending on where it falls in the week. A month whose
    /// days all share a column with the previous month gets no label rather than a zero-width one.
    /// </remarks>
    private static IReadOnlyList<InsightsMonthItem> BuildMonthLabels(IReadOnlyList<InsightsDailyActivity> days, int lead)
    {
        if (days.Count == 0) return [];

        var starts = new List<(string Label, int Column)>();
        for (var index = 0; index < days.Count; index++)
        {
            if (index > 0 && days[index].Date.Month == days[index - 1].Date.Month) continue;
            starts.Add((days[index].Date.ToString("MMM"), (lead + index) / 7));
        }

        var totalColumns = (int)Math.Ceiling((lead + days.Count) / 7.0);
        var labels = new List<InsightsMonthItem>(starts.Count);
        for (var index = 0; index < starts.Count; index++)
        {
            var end = index + 1 < starts.Count ? starts[index + 1].Column : totalColumns;
            var columns = end - starts[index].Column;
            if (columns <= 0) continue;
            labels.Add(new InsightsMonthItem(starts[index].Label, columns * HeatmapColumnPitch));
        }

        return labels;
    }

    /// <summary>
    /// Renders the share card to a fresh file inside the profile and discards the previous one.
    /// A new name per render is what keeps the XAML image cache, which is keyed on the file URI,
    /// from redisplaying a stale card after the underlying activity data changed.
    /// </summary>
    private string PreviewPath()
    {
        var path = Path.Combine(
            library.Profile.RootDirectory,
            $"muesli-activity-preview-{Guid.NewGuid():N}.png");
        var previous = _previewFile;
        _previewFile = path;
        if (previous is not null)
        {
            try { File.Delete(previous); }
            catch (IOException) { /* A viewer or share target may still hold the old render. */ }
            catch (UnauthorizedAccessException) { }
        }

        return path;
    }

    private void ShowStatus(string message, bool error = false)
    {
        StatusMessage = message;
        IsStatusError = error;
        IsStatusOpen = true;
    }
}

public sealed record InsightsDayItem(string DateLabel, int Words, int Meetings, int Level, bool IsPlaceholder = false)
{
    /// <summary>Padding cells render nothing; -1 is the "no level" sentinel.</summary>
    public static InsightsDayItem Placeholder { get; } = new("", 0, 0, -1, true);

    public string AccessibleLabel => IsPlaceholder
        ? "Padding cell"
        : $"{DateLabel}: {InsightsPageViewModel.Count(Words, "word", "words")}, " +
          $"{InsightsPageViewModel.Count(Meetings, "meeting", "meetings")}";
}

public sealed record InsightsWordItem(string Word, int Count, double DisplaySize, bool IsTop)
{
    public string AccessibleLabel => Count == 1
        ? $"{Word}, used once"
        : $"{Word}, used {Count:N0} times";
}

public sealed record InsightsMonthItem(string Label, double Width);

public sealed record InsightsWeekdayItem(string Label);
