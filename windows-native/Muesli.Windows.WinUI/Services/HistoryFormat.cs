using System.ComponentModel;
using System.Globalization;

namespace Muesli.Windows.WinUI.Services;

/// <summary>A dictation row's text with macOS "Show more" / "Show less".</summary>
public sealed class ExpandableText(string full) : INotifyPropertyChanged
{
    public string Full { get; } = full;
    public bool CanExpand { get; } = HistoryFormat.CanExpand(full);

    public bool IsExpanded
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            foreach (var name in new[] { nameof(IsExpanded), nameof(Shown), nameof(MaxLines), nameof(ToggleLabel), nameof(ToggleName) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public string Shown => IsExpanded ? Full : HistoryFormat.Collapsed(Full);

    /// <summary>Three lines while collapsed, unlimited when expanded or short (macOS lineLimit).</summary>
    public int MaxLines => IsExpanded || !CanExpand ? 0 : 3;

    public string ToggleLabel => IsExpanded ? "Show less" : "Show more";
    public string ToggleName => IsExpanded ? "Collapse dictation text" : "Show full dictation text";

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// macOS history rules shared by Timeline, Dictations and Meetings: <c>HistoryDateFilter</c>, the
/// day headings, and <c>DictationRowView</c>'s "Show more" text.
/// </summary>
public static class HistoryFormat
{
    /// <summary>macOS <c>HistoryDateFilter.allCases</c> labels, in order.</summary>
    public static readonly IReadOnlyList<string> DateFilters =
        ["All time", "Last 2 days", "Last week", "Last 2 weeks", "Last month", "Last 3 months"];

    /// <summary>
    /// macOS <c>availableFilters</c>: a range is offered once the oldest record is old enough for
    /// it to differ from the one before. Always a prefix of <see cref="DateFilters"/>.
    /// </summary>
    public static IReadOnlyList<string> AvailableDateFilters(DateTime? oldest, DateTime now)
    {
        if (oldest is null) return [DateFilters[0]];
        var days = (int)(now - oldest.Value).TotalDays;
        return DateFilters.Take(1 + new[] { 1, 3, 8, 15, 31 }.Count(minimum => days >= minimum)).ToList();
    }

    /// <summary>macOS <c>HistoryDateFilter.fromDate</c> for the filter at <paramref name="index"/>.</summary>
    public static DateTime? DateFilterStart(int index, DateTime now) => index switch
    {
        1 => now.AddDays(-2),
        2 => now.AddDays(-7),
        3 => now.AddDays(-14),
        4 => now.AddMonths(-1),
        5 => now.AddMonths(-3),
        _ => null,
    };

    /// <summary>"TODAY", "YESTERDAY", else "MON, 12 OCT" (macOS "EEE, d MMM", uppercased).</summary>
    public static string DayHeading(DateTime day, DateTime today) =>
        day.Date == today.Date ? "TODAY"
        : day.Date == today.Date.AddDays(-1) ? "YESTERDAY"
        : day.ToString("ddd, d MMM", CultureInfo.CurrentCulture).ToUpper(CultureInfo.CurrentCulture);

    /// <summary>macOS <c>canExpandText</c>: over 160 characters or more than two line breaks.</summary>
    public static bool CanExpand(string text) => text.Length > 160 || text.Count(character => character == '\n') > 2;

    /// <summary>macOS <c>displayedText</c> while collapsed: the first 160 characters and "…".</summary>
    public static string Collapsed(string text) => text.Length > 160 ? text[..160].Trim() + "…" : text;
}
