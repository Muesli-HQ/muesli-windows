namespace Muesli.Windows.Core.Insights;

public sealed record InsightWordCount(string Word, int Count);

public enum InsightsRange
{
    ThirtyDays,
    NinetyDays,
    TwelveMonths,
    AllTime
}

public sealed record InsightsTotals(
    int DictationWords,
    int DictationSessions,
    int MeetingWords,
    int Meetings,
    double AverageWpm)
{
    public int TotalWords => DictationWords + MeetingWords;
}

public sealed record InsightsDailyActivity(DateTime Date, int Words, int Meetings, double Intensity);

public sealed record InsightsSnapshot(
    InsightsRange Range,
    DateTime GeneratedAt,
    InsightsTotals Lifetime,
    InsightsTotals Selected,
    IReadOnlyList<InsightsDailyActivity> DailyActivity,
    int CurrentStreakDays,
    int LongestStreakDays,
    int ActiveDaysInRange,
    IReadOnlyList<InsightWordCount> DictationWords,
    IReadOnlyList<InsightWordCount> MeetingWords);

public static class InsightsWordAnalyzer
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "about", "actually", "all", "also", "an", "and", "are", "as", "at", "be", "been", "but", "by",
        "can", "could", "did", "do", "does", "even", "for", "from", "get", "go", "got", "had", "has", "have",
        "he", "her", "here", "him", "his", "how", "i", "if", "in", "into", "is", "it", "its", "just", "like",
        "me", "more", "my", "no", "not", "of", "okay", "on", "one", "or", "our", "really", "right", "she",
        "so", "some", "than", "that", "the", "their", "them", "then", "there", "these", "they", "this", "to",
        "too", "up", "us", "very", "was", "we", "well", "were", "what", "when", "where", "which", "who", "why",
        "will", "with", "would", "yeah", "yes", "you", "your"
    };

    public static IReadOnlyList<InsightWordCount> TopWords(IEnumerable<string> texts, int take = 8) =>
        Rank(Accumulate(texts), take);

    public static InsightsSnapshot Build(
        IEnumerable<(DateTime Timestamp, string Text, int DurationMs)> dictations,
        IEnumerable<(DateTime CreatedAt, string Transcript)> meetings,
        InsightsRange range,
        DateTime now)
    {
        var dictationList = dictations.ToList();
        var meetingList = meetings.ToList();
        var start = StartDate(range, now.Date);
        var selectedDictations = dictationList.Where(item => start is null || item.Timestamp.Date >= start).ToList();
        var selectedMeetings = meetingList.Where(item => start is null || item.CreatedAt.Date >= start).ToList();

        return new InsightsSnapshot(
            range,
            now,
            Totals(dictationList, meetingList),
            Totals(selectedDictations, selectedMeetings),
            DailyActivity(selectedDictations, selectedMeetings, start, now.Date),
            LibraryMetrics.CurrentStreakDays(dictationList.Select(item => item.Timestamp.Date), now.Date),
            LibraryMetrics.LongestStreakDays(dictationList.Select(item => item.Timestamp.Date)),
            selectedDictations.Select(item => item.Timestamp.Date)
                .Concat(selectedMeetings.Select(item => item.CreatedAt.Date))
                .Distinct()
                .Count(),
            Rank(Accumulate(selectedDictations.Select(item => item.Text)), 24),
            Rank(Accumulate(selectedMeetings.Select(item => CleanMeetingTranscript(item.Transcript))), 24));
    }

    public static string RangeLabel(InsightsRange range) => range switch
    {
        InsightsRange.ThirtyDays => "Last 30 days",
        InsightsRange.NinetyDays => "Last 90 days",
        InsightsRange.TwelveMonths => "Last 12 months",
        _ => "All time"
    };

    public static InsightsRange ParseRange(string? label) => label switch
    {
        "Last 30 days" => InsightsRange.ThirtyDays,
        "Last 90 days" => InsightsRange.NinetyDays,
        "Last 12 months" => InsightsRange.TwelveMonths,
        _ => InsightsRange.AllTime
    };

    private static DateTime? StartDate(InsightsRange range, DateTime today) => range switch
    {
        InsightsRange.ThirtyDays => today.AddDays(-29),
        InsightsRange.NinetyDays => today.AddDays(-89),
        InsightsRange.TwelveMonths => today.AddYears(-1),
        _ => null
    };

    private static InsightsTotals Totals(
        IReadOnlyList<(DateTime Timestamp, string Text, int DurationMs)> dictations,
        IReadOnlyList<(DateTime CreatedAt, string Transcript)> meetings)
    {
        var dictationWords = dictations.Sum(item => CountWords(item.Text));
        var meetingWords = meetings.Sum(item => CountWords(CleanMeetingTranscript(item.Transcript)));
        // P2-01: the pace figure comes from LibraryMetrics so Insights, Timeline and Dictations
        // report the same number for the same rows. Insights still scopes its inputs to the
        // selected range; the arithmetic is no longer re-derived here.
        var durationMs = dictations.Sum(item => (long)Math.Max(0, item.DurationMs));
        return new InsightsTotals(
            dictationWords,
            dictations.Count,
            meetingWords,
            meetings.Count,
            LibraryMetrics.AverageWordsPerMinute(dictationWords, durationMs));
    }

    private static IReadOnlyList<InsightsDailyActivity> DailyActivity(
        IReadOnlyList<(DateTime Timestamp, string Text, int DurationMs)> dictations,
        IReadOnlyList<(DateTime CreatedAt, string Transcript)> meetings,
        DateTime? start,
        DateTime today)
    {
        var wordsByDay = new Dictionary<DateTime, int>();
        var meetingsByDay = new Dictionary<DateTime, int>();
        foreach (var item in dictations)
        {
            var day = item.Timestamp.Date;
            wordsByDay[day] = wordsByDay.GetValueOrDefault(day) + CountWords(item.Text);
        }

        foreach (var item in meetings)
        {
            var day = item.CreatedAt.Date;
            meetingsByDay[day] = meetingsByDay.GetValueOrDefault(day) + 1;
            wordsByDay[day] = wordsByDay.GetValueOrDefault(day) + CountWords(CleanMeetingTranscript(item.Transcript));
        }

        var from = start ?? today.AddYears(-1);
        if (from < today.AddDays(-365))
        {
            from = today.AddDays(-365);
        }

        var peak = Math.Max(1, wordsByDay.Values.DefaultIfEmpty(0).Max());
        var days = new List<InsightsDailyActivity>();
        for (var cursor = from; cursor <= today; cursor = cursor.AddDays(1))
        {
            var words = wordsByDay.GetValueOrDefault(cursor);
            days.Add(new InsightsDailyActivity(
                cursor,
                words,
                meetingsByDay.GetValueOrDefault(cursor),
                words <= 0 ? 0.12 : 0.35 + (0.65 * words / peak)));
        }

        return days;
    }

    private static Dictionary<string, int> Accumulate(IEnumerable<string> texts)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in texts)
        {
            foreach (var token in (text ?? "").Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var word = token.Trim('"', '\'', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']');
                if (word.Length < 3 || StopWords.Contains(word) || word.All(char.IsDigit) || !word.Any(char.IsLetter))
                {
                    continue;
                }

                counts[word] = counts.GetValueOrDefault(word) + 1;
            }
        }

        return counts;
    }

    private static IReadOnlyList<InsightWordCount> Rank(Dictionary<string, int> counts, int take) =>
        counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(pair => new InsightWordCount(pair.Key, pair.Value))
            .ToList();

    public static string CleanMeetingTranscript(string transcript)
    {
        var lines = (transcript ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', lines.Select(line =>
        {
            var trimmed = line.Trim();
            var colon = trimmed.IndexOf(':');
            return colon > 0 && colon < 28 ? trimmed[(colon + 1)..].Trim() : trimmed;
        }));
    }

    private static int CountWords(string text) => LibraryMetrics.CountWords(text);
}
