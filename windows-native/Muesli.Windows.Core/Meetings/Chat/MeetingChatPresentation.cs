using System.Globalization;

namespace Muesli.Windows.Services;

/// <summary>macOS MeetingChatPreparedPrompt: quick-action text with the scope it applies to.</summary>
public sealed record MeetingChatPreparedPrompt(string Text, MeetingChatScope Scope, bool IsDraft = false, bool NeedsParticipantName = false);

/// <summary>macOS MeetingChatQuickAction (2f10b23d). Labels and prompt wording are verbatim.</summary>
public enum MeetingChatQuickAction { MyNextSteps, Decisions, DraftFollowUp, WeeklyRecap }

public static class MeetingChatQuickActions
{
    public static IReadOnlyList<MeetingChatQuickAction> All { get; } =
        [MeetingChatQuickAction.MyNextSteps, MeetingChatQuickAction.Decisions, MeetingChatQuickAction.DraftFollowUp, MeetingChatQuickAction.WeeklyRecap];

    public static string Label(this MeetingChatQuickAction action) => action switch
    {
        MeetingChatQuickAction.MyNextSteps => "My next steps",
        MeetingChatQuickAction.Decisions => "Decisions",
        MeetingChatQuickAction.DraftFollowUp => "Draft follow-up",
        _ => "Weekly recap"
    };

    /// <param name="now">Local wall-clock time; the weekly window is today and the six days before it.</param>
    public static MeetingChatPreparedPrompt Prepare(this MeetingChatQuickAction action, MeetingChatScope scope, DateTimeOffset now, string? userDisplayName)
    {
        switch (action)
        {
            case MeetingChatQuickAction.MyNextSteps:
                var name = userDisplayName?.Trim();
                if (string.IsNullOrEmpty(name))
                    return new("Which participant’s next steps should I find?", scope, NeedsParticipantName: true);
                return new($"List next steps explicitly assigned to {name}, with stated owners and deadlines. Cite each source. Distinguish recorded completion from unknown status.", scope);
            case MeetingChatQuickAction.Decisions:
                return new("What decisions were agreed in these meetings? Distinguish proposals, agreements, and later changes. Cite each decision.", scope);
            case MeetingChatQuickAction.DraftFollowUp:
                return new("Draft a concise follow-up email based on these meetings. Include agreed decisions and next steps with stated owners and dates. Cite sources and flag unknown details.", scope, IsDraft: true);
            default:
                var today = new DateTimeOffset(now.Date, now.Offset);
                var lower = today.AddDays(-6);
                var upper = today.AddDays(1);
                var start = scope.StartDate is { } s && s > lower ? s : lower;
                var end = scope.EndDateExclusive is { } e && e < upper ? e : upper;
                if (start >= end)
                {
                    // Keep an empty intersection rather than widening the user's filter or describing a
                    // recap whose end precedes its beginning.
                    return new("The selected date range does not overlap the past seven days. Adjust the date filter to prepare a weekly recap.",
                        scope with { StartDate = start, EndDateExclusive = start });
                }
                var through = end.AddSeconds(-1);
                return new($"Summarize meetings from {MeetingChatFormat.AbbreviatedDate(start)} through {MeetingChatFormat.AbbreviatedDate(through)}. Include decisions, commitments, risks, and unresolved questions with citations. State when coverage is partial.",
                    scope with { StartDate = start, EndDateExclusive = end });
        }
    }
}

/// <summary>macOS MeetingChatMentionQuery: a caret-aware "@query" with its UTF-16 range in the composer.</summary>
public sealed record MeetingChatMentionQuery(int Start, int Length, string Query)
{
    public static MeetingChatMentionQuery? Find(string text, int selectionStart, int selectionLength = 0)
    {
        if (selectionLength != 0 || selectionStart < 0 || selectionStart > text.Length) return null;
        var prefix = text[..selectionStart];
        var at = prefix.LastIndexOf('@');
        if (at < 0 || (at > 0 && !char.IsWhiteSpace(prefix[at - 1]))) return null;
        var query = prefix[(at + 1)..];
        if (new StringInfo(query).LengthInTextElements > 80 || query.Any(IsNewline)) return null;
        return new(at, prefix.Length - at, query);
    }

    // Swift Character.isNewline: LF, VT, FF, CR, NEL, LINE SEPARATOR, PARAGRAPH SEPARATOR.
    private static bool IsNewline(char c) => c is >= (char)0x0A and <= (char)0x0D || c == (char)0x85 || c == (char)0x2028 || c == (char)0x2029;

    public string RemovingFrom(string text)
    {
        if (Start < 0 || Length < 0 || Start > text.Length || Length > text.Length - Start || text.Substring(Start, Length) != "@" + Query) return text;
        return text.Remove(Start, Length);
    }
}

/// <summary>macOS MeetingChatSourceGroup: keep answer citation numbers while grouping passages by meeting.</summary>
public sealed record MeetingChatSourceGroup(string MeetingId, string Title, DateTimeOffset StartDate, IReadOnlyList<MeetingChatSourceGroup.Passage> Passages)
{
    public sealed record Passage(int Number, MeetingChatCitation Citation)
    {
        public string Preview => string.Join(' ', Citation.Excerpt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        public string Metadata => Citation.Kind.Label() + (Citation.Timestamp is { } timestamp ? " · " + timestamp : "");
    }

    public static IReadOnlyList<MeetingChatSourceGroup> Grouped(IReadOnlyList<MeetingChatCitation> citations)
    {
        var groups = new List<(string Id, string Title, DateTimeOffset Start, List<Passage> Passages)>();
        for (var index = 0; index < citations.Count; index++)
        {
            var citation = citations[index];
            var group = groups.FindIndex(item => item.Id == citation.MeetingId);
            if (group < 0) groups.Add((citation.MeetingId, citation.Title, citation.StartDate, [new(index + 1, citation)]));
            else groups[group].Passages.Add(new(index + 1, citation));
        }
        return groups.Select(item => new MeetingChatSourceGroup(item.Id, item.Title, item.Start, item.Passages)).ToList();
    }

    public static string Label(IReadOnlyList<MeetingChatCitation> citations)
    {
        var meetings = citations.Select(item => item.MeetingId).Distinct().Count();
        return $"{meetings} meeting{(meetings == 1 ? "" : "s")} · {citations.Count} passage{(citations.Count == 1 ? "" : "s")}";
    }
}

/// <summary>macOS MeetingChatWaitingStatus: vary only a genuine provider wait; tool events keep their labels.</summary>
public static class MeetingChatWaitingStatus
{
    public static IReadOnlyList<string> Phrases { get; } = ["Thinking", "Working through your question", "Preparing your answer", "Putting the answer together"];

    public static string Label(string phase, TimeSpan elapsed, IReadOnlyList<string>? phrases = null)
    {
        phrases ??= Phrases;
        if (phase is not ("Contacting meeting AI" or "Writing answer" or "Preparing answer") || phrases.Count == 0) return phase;
        return phrases[(int)(Math.Max(0, elapsed.TotalSeconds) / 5) % phrases.Count] + "…";
    }
}

/// <summary>macOS MeetingChatExporter text paths. Clipboard is just the response; provenance belongs to the export.</summary>
public static class MeetingChatExport
{
    public static string AnswerText(MeetingChatTurn turn, bool useEditedDraft = false)
    {
        var answer = MeetingChatClient.DisplayText(useEditedDraft ? turn.EditableDraft ?? turn.OriginalAnswer ?? "" : turn.OriginalAnswer ?? "");
        for (var index = 0; index < turn.Citations.Count; index++)
            answer = answer.Replace($"[[{turn.Citations[index].SourceKey}]]", $"[{index + 1}]", StringComparison.Ordinal);
        return answer;
    }

    public static string ClipboardText(MeetingChatTurn turn, bool useEditedDraft = false) =>
        AnswerText(turn, useEditedDraft).Replace("**", "", StringComparison.Ordinal);

    public static string Markdown(MeetingChatTurn turn, MeetingChatSession session, bool useEditedDraft)
    {
        var parts = new List<string> { $"# {session.Title}", "", $"**Question:** {turn.Question}", $"**AI:** {turn.Provider} · {turn.Model}", "" };
        if (turn.ScopeLabel is { } scopeLabel) parts.AddRange(["**Context:** " + scopeLabel, ""]);
        if (useEditedDraft && turn.EditableDraft is not null) parts.AddRange(["*Edited draft — source references belong to the original answer.*", ""]);
        parts.AddRange([AnswerText(turn, useEditedDraft), "", "## Sources", ""]);
        for (var index = 0; index < turn.Citations.Count; index++)
        {
            var source = turn.Citations[index];
            parts.AddRange([$"[{index + 1}] **{source.Title}** — {MeetingChatFormat.AbbreviatedDateTime(source.StartDate)} · {source.Kind.Label()}{(source.Timestamp is { } t ? " · " + t : "")}", ""]);
            // Indented code prevents meeting text from becoming active Markdown links, images or HTML.
            parts.AddRange(source.Excerpt.Split('\n').Select(line => "    " + line.TrimEnd('\r')));
            parts.Add("");
        }
        if (turn.Coverage is { } coverage)
            parts.AddRange([$"{(coverage.IsPartialRecap ? "Partial recap. " : "")}Evidence from {coverage.EvidenceMeetingCount} of {coverage.EligibleMeetingCount} eligible saved meetings.", ""]);
        return string.Join('\n', parts);
    }

    public static string PlainText(MeetingChatTurn turn, MeetingChatSession session, bool useEditedDraft) =>
        Markdown(turn, session, useEditedDraft).Replace("**", "", StringComparison.Ordinal);
}

/// <summary>Scope captions shown in the composer footer and tooltips (macOS MeetingChatView.scopeLabel and
/// MeetingChatScopePresentation.dateLabel).</summary>
public static class MeetingChatScopePresentation
{
    public static string ScopeLabel(MeetingChatScope scope, Func<string, string?> folderName, IReadOnlyList<MeetingChatSourceChoice> choices)
    {
        var selection = scope.Selection;
        switch (selection.Kind)
        {
            case MeetingChatSelectionKind.All: return "All saved meetings";
            case MeetingChatSelectionKind.Folder: return $"{folderName(selection.FolderId ?? "") ?? "Folder"} · This folder only";
            default:
                var ids = selection.Ids;
                if (ids.Count == 0) return "No meetings selected";
                if (ids.Count == 1 && choices.FirstOrDefault(item => item.Id == ids[0]) is { } source) return source.Title;
                return $"{ids.Count} selected meeting{(ids.Count == 1 ? "" : "s")}";
        }
    }

    public static string? DateLabel(MeetingChatScope scope)
    {
        var start = scope.StartDate is { } s ? MeetingChatFormat.AbbreviatedDate(s) : null;
        var end = scope.EndDateExclusive is { } e ? MeetingChatFormat.AbbreviatedDate(e.AddSeconds(-1)) : null;
        return (start, end) switch
        {
            ({ } a, { } b) => $"{a} – {b}",
            ({ } a, null) => $"From {a}",
            (null, { } b) => $"Through {b}",
            _ => null
        };
    }
}

/// <summary>Swift Date.formatted equivalents in the user's culture and local time.</summary>
public static class MeetingChatFormat
{
    /// <summary>.formatted(date: .abbreviated, time: .omitted), e.g. "Oct 31, 2026".</summary>
    public static string AbbreviatedDate(DateTimeOffset date) =>
        date.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);

    /// <summary>.formatted(date: .abbreviated, time: .shortened).</summary>
    public static string AbbreviatedDateTime(DateTimeOffset date) =>
        date.ToLocalTime().ToString("MMM d, yyyy, " + CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern, CultureInfo.CurrentCulture);

    /// <summary>.dateTime.month(.abbreviated).day(), e.g. "Oct 31".</summary>
    public static string MonthDay(DateTimeOffset date) =>
        date.ToLocalTime().ToString("MMM d", CultureInfo.CurrentCulture);
}

/// <summary>macOS MeetingChatDocumentTarget: the cited passage a meeting document opens on.</summary>
public sealed record MeetingChatDocumentTarget(MeetingChatCitation Citation, Guid SessionId)
{
    public bool ShowsTranscript => Citation.Kind == MeetingChatSourceKind.Transcript;

    /// <summary>The saved range when it still holds the excerpt, else the excerpt's first occurrence.</summary>
    public MeetingChatTextRange? Locate(string text)
    {
        var range = Citation.Range;
        if (range.Location >= 0 && range.Length == Citation.Excerpt.Length && range.Location <= text.Length - range.Length
            && string.CompareOrdinal(text, range.Location, Citation.Excerpt, 0, range.Length) == 0) return range;
        var found = text.IndexOf(Citation.Excerpt, StringComparison.Ordinal);
        return found < 0 ? null : new MeetingChatTextRange(found, Citation.Excerpt.Length);
    }

    /// <summary>The transcript bubble (one per non-blank line) containing the range start.</summary>
    public static int? TranscriptMessageIndex(string text, MeetingChatTextRange range)
    {
        if (range.Location < 0 || range.Location >= text.Length) return null;
        var next = 0;
        for (var start = 0; start < text.Length;)
        {
            var end = text.IndexOfAny(['\r', '\n'], start);
            var lineEnd = end < 0 ? text.Length : end;
            var after = end < 0 ? text.Length : end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);
            if (text.AsSpan(start, lineEnd - start).Trim().Length > 0)
            {
                if (range.Location >= start && range.Location < after) return next;
                next++;
            }
            start = after;
        }
        return null;
    }
}
