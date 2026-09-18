using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Muesli.Windows.Services;
using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows;

public sealed partial class FeatureRuntime
{
private void ToggleMeetingSort_Click(object sender, RoutedEventArgs e)
{
    _meetingSortNewestFirst = !_meetingSortNewestFirst;
    var sorted = _meetingSortNewestFirst
        ? Meetings.OrderByDescending(item => item.CreatedAt).ToList()
        : Meetings.OrderBy(item => item.CreatedAt).ToList();
    Meetings.Clear();
    foreach (var item in sorted)
    {
        Meetings.Add(item);
    }
    OnPropertyChanged(nameof(MeetingSortLabel));
}
private void OpenButtonContextMenu_Click(object sender, RoutedEventArgs e)
{
    if (sender is System.Windows.Controls.Button button && button.ContextMenu is not null)
    {
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }
}
private void SetDictationFilter_Click(object sender, RoutedEventArgs e)
{
    if (sender is not System.Windows.Controls.MenuItem { Tag: string filter })
    {
        return;
    }
    _dictationDateFilter = filter;
    FilteredDictations.Refresh();
    OnPropertyChanged(nameof(DictationHeaderLabel));
    OnPropertyChanged(nameof(DictationFilterLabel));
    OnPropertyChanged(nameof(DictationEmptyTitle));
    OnPropertyChanged(nameof(DictationEmptyStateBody));
    OnPropertyChanged(nameof(HasDictations));
}
private void SetMeetingFilter_Click(object sender, RoutedEventArgs e)
{
    if (sender is not System.Windows.Controls.MenuItem { Tag: string filter })
    {
        return;
    }
    _meetingDateFilter = filter;
    RefreshMeetingViews();
    OnPropertyChanged(nameof(MeetingFilterLabel));
    OnPropertyChanged(nameof(MeetingsEmptyTitle));
    OnPropertyChanged(nameof(MeetingsEmptyBody));
}
private bool PassesMeetingFolder(object item)
{
    if (_selectedMeetingFolderId is null)
    {
        return true;
    }

    return item is MeetingItem meeting && FolderIsInSelectedSubtree(meeting.FolderId);
}

private bool FolderIsInSelectedSubtree(string? folderId)
{
    var current = folderId;
    var guard = 0;
    while (!string.IsNullOrWhiteSpace(current) && guard++ < 16)
    {
        if (string.Equals(current, _selectedMeetingFolderId, StringComparison.Ordinal))
        {
            return true;
        }

        current = MeetingFolders.FirstOrDefault(folder => folder.Id == current)?.ParentId;
    }

    return false;
}
private bool PassesSearch(object item)
{
    var query = _searchQuery.Trim();
    if (query.Length == 0)
    {
        return true;
    }

    if (item is DictationItem dictation)
    {
        return _ftsDictationIds is not null
            ? _ftsDictationIds.Contains(dictation.Id)
            : ProductionInMemorySearchMatch.DictationMatches(dictation.Text, dictation.ModelProfile, query);
    }

    if (item is MeetingItem meeting)
    {
        return _ftsMeetingIds is not null
            ? _ftsMeetingIds.Contains(meeting.Id)
            : ProductionInMemorySearchMatch.MeetingMatches(
                meeting.Title,
                meeting.Summary,
                meeting.Transcript,
                meeting.Metadata,
                query,
                meeting.ManualNotes);
    }

    return true;
}

private void RefreshFtsHits()
{
    _ftsDictationIds = null;
    _ftsMeetingIds = null;
    _searchDictationOrder = new Dictionary<string, int>(StringComparer.Ordinal);
    _searchMeetingOrder = new Dictionary<string, int>(StringComparer.Ordinal);
    var query = _searchQuery.Trim();
    if (query.Length == 0 || _dataStore is not ILibrarySearchAdapter search)
    {
        return;
    }

    try
    {
        // Ask the adapter for the complete ordered match set. The previous SQLite-only path used
        // a hard 500-hit cap and then let the WPF collection order leak through; both JSON and
        // SQLite now provide the same ordered, paged contract.
        var hits = search.Search(new SearchQuery
        {
            Text = query,
            Kinds = SearchRecordKinds.Dictation | SearchRecordKinds.Meeting,
            Fields = SearchFields.All,
            PrefixMatchLastTerm = true,
            Limit = Math.Max(1, Dictations.Count + Meetings.Count + 1),
            Sort = SearchSort.Relevance
        });
        var dictationHits = hits.Where(hit => hit.Kind == SearchRecordKind.Dictation).ToList();
        var meetingHits = hits.Where(hit => hit.Kind == SearchRecordKind.Meeting).ToList();
        _ftsDictationIds = dictationHits.Select(hit => hit.RecordId).ToHashSet(StringComparer.Ordinal);
        _ftsMeetingIds = meetingHits.Select(hit => hit.RecordId).ToHashSet(StringComparer.Ordinal);
        _searchDictationOrder = dictationHits
            .Select((hit, index) => (hit.RecordId, index))
            .ToDictionary(item => item.RecordId, item => item.index, StringComparer.Ordinal);
        _searchMeetingOrder = meetingHits
            .Select((hit, index) => (hit.RecordId, index))
            .ToDictionary(item => item.RecordId, item => item.index, StringComparer.Ordinal);
    }
    catch (Exception exception)
    {
        _logService.Info($"Library search fell back to in-memory matching. category={exception.GetType().Name}");
        _ftsDictationIds = null;
        _ftsMeetingIds = null;
    }
}

private sealed class SearchItemComparer(Func<Dictionary<string, int>> orderProvider) : System.Collections.IComparer
{
    public int Compare(object? left, object? right)
    {
        var leftId = left switch
        {
            DictationItem dictation => dictation.Id,
            MeetingItem meeting => meeting.Id,
            _ => ""
        };
        var rightId = right switch
        {
            DictationItem dictation => dictation.Id,
            MeetingItem meeting => meeting.Id,
            _ => ""
        };
        var order = orderProvider();
        var leftOrder = order.TryGetValue(leftId, out var leftValue) ? leftValue : int.MaxValue;
        var rightOrder = order.TryGetValue(rightId, out var rightValue) ? rightValue : int.MaxValue;
        return leftOrder != rightOrder
            ? leftOrder.CompareTo(rightOrder)
            : string.Compare(leftId, rightId, StringComparison.Ordinal);
    }
}
private void RefreshMeetingViews()
{
    UpdateMeetingFolderCounts();
    FilteredMeetings.Refresh();
    OnPropertyChanged(nameof(MeetingCount));
    OnPropertyChanged(nameof(VisibleMeetingCount));
    OnPropertyChanged(nameof(CurrentMeetingFolderName));
    OnPropertyChanged(nameof(HasMeetings));
    OnPropertyChanged(nameof(MeetingsEmptyTitle));
    OnPropertyChanged(nameof(MeetingsEmptyBody));
}
private void UpdateMeetingFolderCounts()
{
    foreach (var folder in MeetingFolders)
    {
        folder.Count = Meetings.Count(meeting => MeetingBelongsToFolderSubtree(folder.Id, meeting.FolderId));
    }
}

private bool MeetingBelongsToFolderSubtree(string rootId, string? folderId)
{
    var current = folderId;
    var guard = 0;
    while (!string.IsNullOrWhiteSpace(current) && guard++ < 16)
    {
        if (string.Equals(current, rootId, StringComparison.Ordinal))
        {
            return true;
        }

        current = MeetingFolders.FirstOrDefault(item => item.Id == current)?.ParentId;
    }

    return false;
}
private static bool PassesDateFilter(object item, string filter)
{
    if (filter == "all")
    {
        return true;
    }
    var date = item switch
    {
        DictationItem dictation => dictation.Timestamp,
        MeetingItem meeting => meeting.CreatedAt,
        _ => DateTime.MinValue
    };
    return date >= DateTime.Now - FilterWindow(filter);
}
private static TimeSpan FilterWindow(string filter)
{
    return filter switch
    {
        "last2Days" => TimeSpan.FromDays(2),
        "lastWeek" => TimeSpan.FromDays(7),
        "last2Weeks" => TimeSpan.FromDays(14),
        "lastMonth" => TimeSpan.FromDays(31),
        "last3Months" => TimeSpan.FromDays(93),
        _ => TimeSpan.MaxValue
    };
}
private static string FilterLabel(string filter)
{
    return filter switch
    {
        "last2Days" => "Last 2 days",
        "lastWeek" => "Last week",
        "last2Weeks" => "Last 2 weeks",
        "lastMonth" => "Last month",
        "last3Months" => "Last 3 months",
        _ => "All time"
    };
}
private string NormalizeSummaryTemplateName(string? value)
{
    var normalized = MeetingSummaryService.NormalizeTemplateName(value);
    if (MeetingSummaryService.IsBuiltInTemplate(normalized))
    {
        return normalized;
    }

    var custom = SummaryTemplates.FirstOrDefault(template => template.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    return custom ?? "Standard Meeting Notes";
}
private void DictationsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
{
    if (sender is System.Windows.Controls.ListBox { SelectedItem: DictationItem item })
    {
        System.Windows.Clipboard.SetText(item.Text);
        DictationStatus = "Copied";
        _toastNotificationService.Show("Copied", item.Text, ToastState.Success);
    }
}
private void MeetingCard_Click(object sender, MouseButtonEventArgs e)
{
    if (sender is FrameworkElement { DataContext: MeetingItem item })
    {
        OpenMeetingDetail(item);
        ShowPage(AppPage.MeetingDetail);
    }
}
private void UpdateSearchPageVisibility()
{
    if (!string.IsNullOrWhiteSpace(SearchQuery))
    {
        if (SearchPage.Visibility != Visibility.Visible)
        {
            ShowPage(AppPage.Search);
        }
        return;
    }
    if (SearchPage.Visibility == Visibility.Visible)
    {
        ShowPage(_appServices.Navigation.State.LastContentPage);
    }
}
private void OpenSearchFromCompactRail_Click(object sender, RoutedEventArgs e)
{
    ShowPage(AppPage.Search);
    Dispatcher.BeginInvoke(() =>
    {
        MainSearchInput.Focus();
        Keyboard.Focus(MainSearchInput);
    });
}
private void RefreshSearchResults()
{
    RefreshFtsHits();
    SearchDictationResults.Refresh();
    SearchMeetingResults.Refresh();
    OnPropertyChanged(nameof(SearchDictationCount));
    OnPropertyChanged(nameof(SearchMeetingCount));
    OnPropertyChanged(nameof(SearchResultsSummary));
    OnPropertyChanged(nameof(HasSearchResults));
    OnPropertyChanged(nameof(SearchShowsDictations));
    OnPropertyChanged(nameof(SearchShowsMeetings));
    OnPropertyChanged(nameof(SearchEmptyTitle));
    OnPropertyChanged(nameof(SearchEmptyBody));
}

private void SelectSearchDictationsTab_Click(object sender, RoutedEventArgs e) => SelectedSearchTab = "dictations";

private void SelectSearchMeetingsTab_Click(object sender, RoutedEventArgs e) => SelectedSearchTab = "meetings";

private void OpenInsights_Click(object sender, RoutedEventArgs e)
{
    IsInsightsOpen = true;
    NotifyInsightsChanged();
}

private void CloseInsights_Click(object sender, RoutedEventArgs e) => IsInsightsOpen = false;
}
