using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;
using Muesli.Windows.Services.Persistence;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.WinUI.ViewModels;

public partial class SearchPageViewModel(WinUiLibraryContext library, IClipboardService clipboard) : ObservableObject
{
    private IReadOnlyList<SearchResultItem> _dictations = [];
    private IReadOnlyList<SearchResultItem> _meetings = [];

    [ObservableProperty] public partial string Query { get; private set; } = "";
    [ObservableProperty] public partial IReadOnlyList<SearchResultItem> Results { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<SearchResultItem> VisibleResults { get; private set; } = [];
    [ObservableProperty] public partial int FilterIndex { get; set; }
    [ObservableProperty] public partial bool HasError { get; private set; }
    [ObservableProperty] public partial string ErrorMessage { get; private set; } = "";
    [ObservableProperty] public partial string PersistenceWarning { get; private set; } = "";
    [ObservableProperty] public partial string StatusMessage { get; private set; } = "";
    [ObservableProperty] public partial bool IsStatusOpen { get; set; }
    [ObservableProperty] public partial bool IsStatusError { get; private set; }

    public bool HasQuery => Query.Length > 0;
    public bool HasPersistenceWarning => PersistenceWarning.Length > 0;
    public bool HasVisibleResults => VisibleResults.Count > 0;
    public bool ShowResults => HasVisibleResults && !HasError;
    public bool ShowEmptyState => !HasVisibleResults && !HasError;
    public int DictationCount => _dictations.Count;
    public int MeetingCount => _meetings.Count;

    public string PageTitle => HasQuery ? $"Search results for \"{Query}\"" : "Search";

    /// <summary>
    /// P2-06 — "1 meetings" was printing on every single-result search. Both halves are
    /// pluralised from their own count.
    /// </summary>
    public string PageSummary => Plural(DictationCount, "dictation") + " · " + Plural(MeetingCount, "meeting");

    private static string Plural(int count, string noun) =>
        count.ToString("N0") + " " + noun + (count == 1 ? "" : "s");

    public string SectionHeadline => FilterIndex switch
    {
        1 => "DICTATIONS (" + DictationCount + ")",
        2 => "MEETINGS (" + MeetingCount + ")",
        _ => "RESULTS"
    };

    public bool ShowPageSectionLabel => ShowResults && FilterIndex != 0;

    public string EmptyTitle
    {
        get
        {
            if (!HasQuery)
            {
                return "Search dictations and meetings";
            }

            if (FilterIndex == 1 && DictationCount == 0 && MeetingCount > 0)
            {
                return "No matching dictations";
            }

            if (FilterIndex == 2 && MeetingCount == 0 && DictationCount > 0)
            {
                return "No matching meetings";
            }

            return $"No results for \"{Query}\"";
        }
    }

    public string EmptyBody
    {
        get
        {
            if (!HasQuery)
            {
                return "Type in the search box at the top of the sidebar to find dictations and meetings.";
            }

            if (FilterIndex == 1 && DictationCount == 0 && MeetingCount > 0)
            {
                return "Meetings still match this query. Switch tabs to view them.";
            }

            if (FilterIndex == 2 && MeetingCount == 0 && DictationCount > 0)
            {
                return "Dictations still match this query. Switch tabs to view them.";
            }

            return "Try a different search term.";
        }
    }

    public string ResultCount => PageSummary;

    partial void OnFilterIndexChanged(int value) => RebuildVisible();

    public void Search(string? query)
    {
        Query = query?.Trim() ?? "";
        HasError = false;
        ErrorMessage = "";
        PersistenceWarning = "";

        if (Query.Length == 0)
        {
            _dictations = [];
            _meetings = [];
            Results = [];
            RebuildVisible();
            return;
        }

        try
        {
            var snapshot = library.ReadSnapshot();
            PersistenceWarning = snapshot.PersistenceWarning?.Trim() ?? "";

            _dictations = snapshot.Dictations
                .Where(item => ProductionInMemorySearchMatch.DictationMatches(item.Text, item.ModelProfile, Query))
                .Select(item => SearchResultItem.FromDictation(item, Query))
                .ToList();
            _meetings = snapshot.Meetings
                .Where(item => ProductionInMemorySearchMatch.MeetingMatches(
                    item.Title,
                    item.Summary,
                    item.Transcript,
                    "",
                    Query,
                    item.ManualNotes))
                .Select(item => SearchResultItem.FromMeeting(item, Query))
                .ToList();
            Results = _dictations.Concat(_meetings).OrderByDescending(item => item.Timestamp).ToList();
        }
        catch (Exception)
        {
            HasError = true;
            ErrorMessage = "Local history could not be searched. The files on this PC may be in use or unreadable.";
            _dictations = [];
            _meetings = [];
            Results = [];
        }

        RebuildVisible();
    }

    [RelayCommand]
    private void Retry() => Search(Query);

    [RelayCommand]
    private async Task CopyDictationAsync(SearchResultItem? item, CancellationToken cancellationToken)
    {
        if (item is null || !item.IsDictation || string.IsNullOrWhiteSpace(item.CopyText))
        {
            return;
        }

        // Prompt 9 CR-01: see AboutPageViewModel.CopyDiagnosticsAsync — a busy clipboard throws,
        // and this runs from an async void click handler (SearchPage.xaml.cs), so it must not escape.
        try
        {
            await clipboard.SetTextAsync(item.CopyText, cancellationToken);
            IsStatusError = false;
            StatusMessage = "Copied";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            IsStatusError = true;
            StatusMessage = $"Could not copy the dictation: {WinUiClipboardService.DescribeFailure(exception)}";
        }
        IsStatusOpen = true;
    }

    private void RebuildVisible()
    {
        VisibleResults = FilterIndex switch
        {
            1 => TagGroup(_dictations, "", showSectionHeader: false),
            2 => TagGroup(_meetings, "", showSectionHeader: false),
            _ => WithSectionHeaders(_dictations, _meetings)
        };
        NotifyDerived();
    }

    private static IReadOnlyList<SearchResultItem> WithSectionHeaders(
        IReadOnlyList<SearchResultItem> dictations,
        IReadOnlyList<SearchResultItem> meetings)
    {
        if (dictations.Count == 0)
        {
            return TagGroup(meetings, "MEETINGS (" + meetings.Count + ")", showSectionHeader: true);
        }

        if (meetings.Count == 0)
        {
            return TagGroup(dictations, "DICTATIONS (" + dictations.Count + ")", showSectionHeader: true);
        }

        return TagGroup(dictations, "DICTATIONS (" + dictations.Count + ")", showSectionHeader: true)
            .Concat(TagGroup(meetings, "MEETINGS (" + meetings.Count + ")", showSectionHeader: true))
            .ToList();
    }

    private static IReadOnlyList<SearchResultItem> TagGroup(
        IReadOnlyList<SearchResultItem> items,
        string label,
        bool showSectionHeader)
    {
        if (items.Count == 0)
        {
            return items;
        }

        return items
            .Select((item, index) => item with
            {
                ShowSectionHeader = showSectionHeader && index == 0,
                SectionLabel = label,
                IsGroupStart = index == 0,
                IsGroupEnd = index == items.Count - 1
            })
            .ToList();
    }

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(HasQuery));
        OnPropertyChanged(nameof(HasPersistenceWarning));
        OnPropertyChanged(nameof(HasVisibleResults));
        OnPropertyChanged(nameof(ShowResults));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(DictationCount));
        OnPropertyChanged(nameof(MeetingCount));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSummary));
        OnPropertyChanged(nameof(SectionHeadline));
        OnPropertyChanged(nameof(ShowPageSectionLabel));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyBody));
        OnPropertyChanged(nameof(ResultCount));
    }
}

public sealed record SearchResultItem(
    string Kind,
    string Id,
    string Title,
    string HighlightSource,
    string Query,
    string CopyText,
    string ModelLabel,
    string Metadata,
    DateTime Timestamp,
    bool ShowSectionHeader = false,
    string SectionLabel = "",
    bool IsGroupStart = false,
    bool IsGroupEnd = false)
{
    public bool IsDictation => Kind == "Dictation";
    public bool IsMeeting => Kind == "Meeting";

    public string TimeLabel => ToLocal(Timestamp).ToString("hh:mm tt");

    public string TimestampLabel => ToLocal(Timestamp).ToString("MMM d, yyyy · h:mm tt");

    public static SearchResultItem FromDictation(PersistedDictation item, string query) => new(
        "Dictation",
        item.Id,
        FirstLine(item.Text),
        item.Text ?? "",
        query,
        item.Text ?? "",
        string.IsNullOrWhiteSpace(item.ModelProfile) ? "Local model" : item.ModelProfile,
        FormatDuration(item.DurationMs),
        item.Timestamp);

    public static SearchResultItem FromMeeting(PersistedMeeting item, string query)
    {
        var title = string.IsNullOrWhiteSpace(item.Title) ? "Untitled meeting" : item.Title.Trim();
        var snippet = ProductionInMemorySearchMatch.MeetingSnippet(
            item.Title,
            item.Summary,
            item.Transcript,
            item.ManualNotes,
            query);
        return new SearchResultItem(
            "Meeting",
            item.Id,
            title,
            snippet,
            query,
            "",
            string.IsNullOrWhiteSpace(item.ModelProfile) ? "Local model" : item.ModelProfile,
            // P2-06: the same record must not read "2026-09-10 10:00" here and
            // "10 Sept 2026 at 10:00:00 AM" in the Meetings list. This is the Meetings list's
            // own format (LibraryPageViewModels.MeetingListItem.From), so the two agree.
            ToLocal(item.CreatedAt).ToString("d MMM yyyy 'at' h:mm:ss tt") + " • " + FormatDuration(item.DurationMs),
            item.CreatedAt);
    }

    private static DateTime ToLocal(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;

    private static string FirstLine(string? value)
    {
        var text = (value ?? "").Trim().Replace('\r', ' ').Replace('\n', ' ');
        return text.Length == 0 ? "Untitled dictation" : text;
    }

    private static string FormatDuration(int durationMs)
    {
        var seconds = Math.Max(0, (int)Math.Round(durationMs / 1000.0));
        if (seconds >= 3600)
        {
            return $"{seconds / 3600}h {(seconds % 3600) / 60}m";
        }

        if (seconds >= 60)
        {
            var minutes = seconds / 60;
            var remaining = seconds % 60;
            return remaining == 0 ? $"{minutes}m" : $"{minutes}m {remaining}s";
        }

        return durationMs <= 0 ? "" : $"{seconds}s";
    }
}
