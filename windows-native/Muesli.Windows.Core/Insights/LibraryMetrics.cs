using Muesli.Windows.Services.Text;

namespace Muesli.Windows.Core.Insights;

/// <summary>
/// The single definition of the two library figures the shell prints in more than one place:
/// "words dictated" and "avg WPM".
/// </summary>
/// <remarks>
/// <para>
/// P2-01. Three independent implementations disagreed on the same profile: the WinUI library
/// snapshot divided <em>dictation + meeting</em> words by <em>dictation + meeting</em> duration
/// (2 WPM), the Dictations page divided dictation words by dictation duration (63 WPM), and
/// <see cref="InsightsWordAnalyzer"/> did the same but over a date range and with a narrower word
/// splitter (67 WPM). Timeline additionally labelled the combined dictation + meeting total
/// "words dictated" (129) next to the Dictations page's 63.
/// </para>
/// <para>
/// The agreed rule, which is what the macOS reference shows (the same 98.4k / 139 WPM on both
/// <c>01-timeline.png</c> and <c>02-dictations.png</c>):
/// </para>
/// <list type="bullet">
///   <item><description><b>words dictated</b> counts dictation text only. Meeting transcripts are
///   other people speaking, so they are reported separately as meeting words and never folded into
///   this figure.</description></item>
///   <item><description><b>avg WPM</b> is dictated words ÷ minutes spent dictating. Meeting words
///   and meeting duration never enter it — a one-hour meeting with a short transcript would
///   otherwise crush the user's measured speaking pace toward zero, which is exactly the 2 WPM
///   the Timeline card was showing.</description></item>
/// </list>
/// <para>
/// Callers may legitimately scope the inputs (Insights offers 30 / 90 / 365 day and all-time
/// ranges and labels which one is shown); they must not re-derive the arithmetic.
/// </para>
/// </remarks>
public static class LibraryMetrics
{
    /// <summary>
    /// Whitespace-delimited word count, used for every "words" figure in the shell so that two
    /// surfaces never disagree by a couple of words because one splitter knew about non-breaking
    /// space and the other did not.
    /// </summary>
    /// <remarks>
    /// Routed through <see cref="TranscriptTextProcessing.Current"/> so the shared Swift core is the
    /// canonical word-count implementation on Windows. Before the composition root initializes the
    /// processor this resolves to the parity-identical managed implementation.
    /// </remarks>
    public static int CountWords(string? text) => TranscriptTextProcessing.Current.CountWords(text);

    /// <summary>
    /// Average dictation pace in whole words per minute, or 0 when nothing has been dictated.
    /// </summary>
    /// <param name="dictatedWords">Words of dictation text in scope.</param>
    /// <param name="dictationDurationMs">Milliseconds of dictation audio in the same scope.</param>
    public static int AverageWordsPerMinute(int dictatedWords, long dictationDurationMs)
    {
        if (dictatedWords <= 0 || dictationDurationMs <= 0)
        {
            return 0;
        }

        return (int)Math.Round(dictatedWords / (dictationDurationMs / 60_000d));
    }

    /// <summary>
    /// Consecutive days with at least one dictation, ending today or yesterday; 0 otherwise.
    /// </summary>
    /// <remarks>
    /// Prompt 9. The streak is a <b>dictation</b> streak, as on macOS
    /// (<c>DictationStore.dictationStreaks</c> selects only from <c>dictations</c>). The Timeline and
    /// Dictations "day streak" cards used to count meeting days too, so the same fixture read
    /// "6 day streak" there and "5 days current streak" on Insights.
    /// </remarks>
    /// <param name="dictationDates">Dates of dictations (any time component is ignored).</param>
    /// <param name="today">The local date to anchor the streak on.</param>
    public static int CurrentStreakDays(IEnumerable<DateTime> dictationDates, DateTime today)
    {
        var set = dictationDates.Select(date => date.Date).ToHashSet();
        if (set.Count == 0)
        {
            return 0;
        }

        today = today.Date;
        var anchor = set.Contains(today) ? today : today.AddDays(-1);
        if (!set.Contains(anchor))
        {
            return 0;
        }

        var streak = 0;
        var cursor = anchor;
        while (set.Contains(cursor))
        {
            streak++;
            cursor = cursor.AddDays(-1);
        }

        return streak;
    }

    /// <summary>Longest run of consecutive days with at least one dictation.</summary>
    public static int LongestStreakDays(IEnumerable<DateTime> dictationDates)
    {
        var ordered = dictationDates.Select(date => date.Date).Distinct().OrderBy(date => date).ToList();
        if (ordered.Count == 0)
        {
            return 0;
        }

        var longest = 1;
        var current = 1;
        for (var index = 1; index < ordered.Count; index++)
        {
            current = ordered[index] == ordered[index - 1].AddDays(1) ? current + 1 : 1;
            longest = Math.Max(longest, current);
        }

        return longest;
    }
}
