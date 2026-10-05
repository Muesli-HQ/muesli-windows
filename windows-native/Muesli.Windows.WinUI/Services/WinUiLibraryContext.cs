using Muesli.Windows.Core.Insights;
using Muesli.Windows.Core.Profiles;
using Muesli.Windows.Services;
using Muesli.Windows.Services.Persistence;
using Muesli.Windows.Platform.Profiles;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// WinUI's composition boundary for local history. Production launches use the canonical
/// Muesli profile; tests can opt into an isolated profile with <c>MUESLI_PROFILE_ROOT</c>.
/// No sample rows are manufactured when the profile is empty.
/// </summary>
public sealed class WinUiLibraryContext : IDisposable
{
    private readonly IMuesliProfileLease? _profileLease;
    private readonly AppDataStore _jsonStore;

    public WinUiLibraryContext(IMuesliProfilePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.RootDirectory);
        Directory.CreateDirectory(paths.DataDirectory);
        Directory.CreateDirectory(paths.CaptureDirectory);
        Directory.CreateDirectory(paths.LogDirectory);
        _profileLease = WindowsProfileLeaseFactory.Instance.TryAcquire(paths, TimeSpan.Zero)
            ?? throw new InvalidOperationException(
                $"The WinUI profile is already in use: {paths.RootDirectory}");
        try
        {
            _jsonStore = new AppDataStore(paths.DataDirectory);
            if (PersistenceCutoverGate.IsEnabled || File.Exists(PersistencePaths.DatabasePathFor(paths.DataDirectory)))
            {
                var result = new PersistenceCutover(paths.DataDirectory).EnsureMigrated();
                if (!result.Succeeded)
                    throw new InvalidOperationException("Muesli history could not be opened. No history was changed.");
                History = SqliteLibraryHistoryAdapter.Open(paths.DataDirectory);
            }
            else History = new JsonLibraryHistoryAdapter(_jsonStore);
            Profile = paths;
        }
        catch
        {
            _profileLease.Dispose();
            throw;
        }
    }

    public IMuesliProfilePaths Profile { get; }

    public ILibraryHistoryAdapter History { get; }

    /// <summary>
    /// Permanently removes one dictation from the active profile. The privacy-sensitive
    /// save path also removes recoverable temporary copies that could otherwise retain deleted
    /// transcript text.
    /// </summary>
    public bool DeleteDictation(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var dictations = History.LoadDictations().ToList();
        var removed = dictations.RemoveAll(item =>
            string.Equals(item.Id, id, StringComparison.Ordinal)) > 0;
        if (removed)
        {
            History.SaveDictations(dictations, afterExplicitDeletion: true);
        }

        return removed;
    }

    public PersistedMeeting? FindMeeting(string id) => History.LoadMeetings()
        .FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));

    public bool UpdateMeeting(PersistedMeeting meeting)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        var meetings = History.LoadMeetings().ToList();
        var index = meetings.FindIndex(item => string.Equals(item.Id, meeting.Id, StringComparison.Ordinal));
        if (index < 0) return false;
        meetings[index] = meeting;
        History.SaveMeetings(meetings);
        return true;
    }

    public bool DeleteMeeting(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (History is SqliteLibraryHistoryAdapter sqlite)
            foreach (var link in sqlite.Store.Meetings.ListFollowUpsLinkedTo(id).Where(link => link.Id == $"meeting_thread_{id}"))
                sqlite.Store.Meetings.DeleteFollowUp(link.Id);
        var meetings = History.LoadMeetings().ToList();
        var removed = meetings.RemoveAll(item => string.Equals(item.Id, id, StringComparison.Ordinal)) > 0;
        if (removed) History.SaveMeetings(meetings, afterExplicitDeletion: true);
        return removed;
    }

    public bool SupportsMeetingThreads => History is SqliteLibraryHistoryAdapter;

    public void LinkFollowUp(PersistedMeeting predecessor, PersistedMeeting successor)
    {
        if (History is not SqliteLibraryHistoryAdapter sqlite) throw new InvalidOperationException("Meeting threads require the SQLite library.");
        sqlite.Store.Meetings.UpsertFollowUp(new FollowUpRecord
        {
            Id = $"meeting_thread_{successor.Id}", MeetingId = predecessor.Id, LinkedMeetingId = successor.Id,
            Text = successor.Title, CreatedAtUtc = new DateTimeOffset(successor.CreatedAt).ToUniversalTime(),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
    }

    public PersistedMeeting? Predecessor(string id) => History is SqliteLibraryHistoryAdapter sqlite
        ? sqlite.Store.Meetings.ListFollowUpsLinkedTo(id).Where(link => link.Id == $"meeting_thread_{id}")
            .Select(link => FindMeeting(link.MeetingId)).FirstOrDefault() : null;

    public IReadOnlyList<PersistedMeeting> RelatedMeetings(string id)
    {
        if (History is not SqliteLibraryHistoryAdapter sqlite) return [];
        var parent = Predecessor(id);
        var children = sqlite.Store.Meetings.ListFollowUps(id)
            .Where(link => link.LinkedMeetingId is { } childId && link.Id == $"meeting_thread_{childId}")
            .Select(link => FindMeeting(link.LinkedMeetingId!)).OfType<PersistedMeeting>();
        return (parent is null ? children : new[] { parent }.Concat(children)).OrderBy(meeting => meeting.CreatedAt).ToList();
    }

    public int ClearDictations()
    {
        var count = History.LoadDictations().Count;
        if (count == 0) return 0;
        History.SaveDictations([], afterExplicitDeletion: true);
        return count;
    }

    public int ClearMeetings()
    {
        var count = History.LoadMeetings().Count;
        if (count == 0) return 0;
        History.SaveMeetings([], afterExplicitDeletion: true);
        return count;
    }

    public IReadOnlyList<PersistedMeetingTemplate> LoadMeetingTemplates() =>
        History.LoadMeetingTemplates();

    public void SaveMeetingTemplates(IEnumerable<PersistedMeetingTemplate> templates) =>
        History.SaveMeetingTemplates(templates.ToList());

    public PersistedMeetingFolder SaveMeetingFolder(string? id, string name, string? parentId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var folders = History.LoadMeetingFolders().ToList();
        var existing = folders.FirstOrDefault(item => item.Id == id);
        var folder = new PersistedMeetingFolder(
            string.IsNullOrWhiteSpace(id) ? $"folder_{Guid.NewGuid():N}" : id,
            name.Trim(), existing?.ParentId ?? parentId);
        var index = folders.FindIndex(item => item.Id == folder.Id);
        if (index >= 0) folders[index] = folder;
        else folders.Add(folder);
        History.SaveMeetingFolders(folders);
        return folder;
    }

    public bool DeleteMeetingFolder(string id) =>
        History is ILibraryFolderAdapter folders && folders.DeleteMeetingFolder(id);

    public void MoveMeetingFolder(string id, string? parentId)
    {
        if (History is not ILibraryFolderAdapter folders)
            throw new InvalidOperationException("Folder moves are unavailable.");
        folders.MoveMeetingFolder(id, string.IsNullOrWhiteSpace(parentId) ? null : parentId);
    }

    public IReadOnlyList<DictionaryEntryRecord> LoadDictionary() => History.LoadDictionary();

    public void SaveDictionary(IEnumerable<DictionaryEntryRecord> entries) =>
        History.SaveDictionary(entries.ToList());

    public WinUiLibrarySnapshot ReadSnapshot()
    {
        var dictations = History.LoadDictations()
            .OrderByDescending(item => item.Timestamp)
            .ToList();
        var meetings = History.LoadMeetings()
            .OrderByDescending(item => item.CreatedAt)
            .ToList();
        var folders = History.LoadMeetingFolders()
            .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var dictionary = History.LoadDictionary();
        var templates = History.LoadMeetingTemplates();

        // P2-02: a dictation row has one piece of text. Passing item.Text as both Title and
        // Detail printed every transcript twice — once bold, once as the secondary line.
        // The transcript is the row's title; only a meeting carries a separate summary line.
        var timeline = dictations
            .Select(item => new WinUiTimelineEntry(
                WinUiTimelineKind.Dictation,
                item.Timestamp,
                string.IsNullOrWhiteSpace(item.Text) ? "Untitled dictation" : item.Text.Trim(),
                "",
                item.DurationMs,
                item.ModelProfile,
                null,
                item.Id))
            .Concat(meetings.Select(item => new WinUiTimelineEntry(
                WinUiTimelineKind.Meeting,
                item.CreatedAt,
                string.IsNullOrWhiteSpace(item.Title) ? "Untitled meeting" : item.Title,
                item.Summary,
                item.DurationMs,
                item.ModelProfile,
                item.SessionState.ToString(),
                item.Id)))
            .OrderByDescending(item => item.Timestamp)
            .ToList();

        var dictationWords = dictations.Sum(item => CountWords(item.Text));
        var meetingWords = meetings.Sum(item => item.WordCount > 0 ? item.WordCount : CountWords(item.Transcript));
        // P2-01: pace is dictation-only (see LibraryMetrics), so the two durations stay separate.
        // TotalDurationMs remains the whole library's recorded time for the library-info surface.
        var dictationDurationMs = dictations.Sum(item => Math.Max(0, item.DurationMs));
        var totalDurationMs = dictationDurationMs +
                              meetings.Sum(item => Math.Max(0, item.DurationMs));
        var activeDays = timeline
            .Select(item => item.Timestamp.Date)
            .Distinct()
            .OrderByDescending(date => date)
            .ToList();

        return new WinUiLibrarySnapshot(
            dictations,
            meetings,
            folders,
            dictionary,
            templates,
            timeline,
            dictationWords + meetingWords,
            dictationWords,
            meetingWords,
            totalDurationMs,
            dictationDurationMs,
            activeDays.Count,
            // Prompt 9: dictation-only streak, one definition shared with Insights (LibraryMetrics).
            LibraryMetrics.CurrentStreakDays(dictations.Select(item => item.Timestamp.Date), DateTime.Today),
            LibraryMetrics.LongestStreakDays(dictations.Select(item => item.Timestamp.Date)),
            History.LastWarning);
    }

    public void Dispose()
    {
        (History as IDisposable)?.Dispose();
        _profileLease?.Dispose();
    }

    private static int CountWords(string? text) => LibraryMetrics.CountWords(text);
}

public enum WinUiTimelineKind
{
    Dictation,
    Meeting
}

public sealed record WinUiTimelineEntry(
    WinUiTimelineKind Kind,
    DateTime Timestamp,
    string Title,
    string Detail,
    int DurationMs,
    string ModelProfile,
    string? Status,
    string Id)
{
    public bool IsMeeting => Kind == WinUiTimelineKind.Meeting;

    public string KindLabel => IsMeeting ? "Meeting" : "Dictation";

    public string TimeLabel => Timestamp.ToLocalTime().ToString("hh:mm tt");

    public string DateLabel => Timestamp.ToLocalTime().Date == DateTime.Today
        ? "Today"
        : Timestamp.ToLocalTime().ToString("MMM d, yyyy");

    /// <summary>Row title on one line, so a multi-line dictation still ellipsizes (P2-03).</summary>
    public string TitleLabel => (Title ?? "").Trim().Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>
    /// P2-02: the secondary line exists only when there is genuinely a second piece of text (a
    /// meeting summary). It is no longer padded out with "No transcript or summary saved".
    /// </summary>
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public string DetailLabel => (Detail ?? "").Trim().Replace('\r', ' ').Replace('\n', ' ');

    public string DurationLabel => DurationMs <= 0
        ? ""
        : TimeSpan.FromMilliseconds(DurationMs).TotalHours >= 1
            ? TimeSpan.FromMilliseconds(DurationMs).ToString(@"h\:mm\:ss")
            : TimeSpan.FromMilliseconds(DurationMs).ToString(@"m\:ss");

    public string StatusLabel => string.IsNullOrWhiteSpace(Status) ? "Local" : Status!;
}

public sealed record WinUiLibrarySnapshot(
    IReadOnlyList<PersistedDictation> Dictations,
    IReadOnlyList<PersistedMeeting> Meetings,
    IReadOnlyList<PersistedMeetingFolder> Folders,
    IReadOnlyList<DictionaryEntryRecord> Dictionary,
    IReadOnlyList<PersistedMeetingTemplate> Templates,
    IReadOnlyList<WinUiTimelineEntry> Timeline,
    int TotalWords,
    int DictationWords,
    int MeetingWords,
    int TotalDurationMs,
    int DictationDurationMs,
    int ActiveDays,
    int CurrentStreak,
    int LongestStreak,
    string? PersistenceWarning)
{
    public int MeetingCount => Meetings.Count;

    public int DictationCount => Dictations.Count;

    /// <summary>
    /// P2-01 — the one pace figure. Every surface that prints "avg WPM" reads this property (or
    /// the range-scoped equivalent in <see cref="LibraryMetrics"/>) instead of dividing its own
    /// pair of numbers.
    /// </summary>
    public int AverageWordsPerMinute =>
        LibraryMetrics.AverageWordsPerMinute(DictationWords, DictationDurationMs);
}
