using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Services;

/// <summary>
/// Transcript edit persistence bridge over the active history adapter. It deliberately receives
/// the adapter from the composition root and never constructs an AppDataStore of its own.
/// </summary>
public sealed class LibraryTranscriptMeetingStore : ITranscriptMeetingStore
{
    private readonly ILibraryHistoryAdapter _history;

    public LibraryTranscriptMeetingStore(ILibraryHistoryAdapter history)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
    }

    public PersistedMeeting? Find(string meetingId) =>
        _history.LoadMeetings().FirstOrDefault(meeting =>
            string.Equals(meeting.Id, meetingId, StringComparison.Ordinal));

    public void Save(PersistedMeeting meeting)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        var meetings = _history.LoadMeetings().ToList();
        var index = meetings.FindIndex(item =>
            string.Equals(item.Id, meeting.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            meetings[index] = meeting;
        }
        else
        {
            meetings.Add(meeting);
        }

        _history.SaveMeetings(meetings);
    }
}
