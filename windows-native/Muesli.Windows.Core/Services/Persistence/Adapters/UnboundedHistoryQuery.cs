namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// Dashboard-sized reads. JSON loaded the entire file; the repository default
/// <c>Limit = 100</c> would silently drop the rest. Wave 2 must keep using these (or
/// <c>Count()</c>) until the UI pages.
/// </summary>
public static class UnboundedHistoryQuery
{
    public static DictationQuery Dictations { get; } = new()
    {
        Limit = int.MaxValue,
        Sort = HistorySort.OldestFirst
    };

    public static MeetingQuery Meetings { get; } = new()
    {
        Limit = int.MaxValue,
        Sort = HistorySort.OldestFirst
    };
}
