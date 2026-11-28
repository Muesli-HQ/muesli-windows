using Muesli.Windows.Services;
using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Tests;

/// <summary>"Don't Keep Audio" must really drop the recording from the SQLite library.</summary>
public sealed class MeetingAudioRemovalTests
{
    [Fact]
    public void ANormalSaveKeepsAudioButClearMeetingAudioRemovesIt()
    {
        using var directory = new TestDirectory();
        using var store = MuesliPersistenceStore.Open(PersistencePaths.DatabasePathFor(directory.Path));
        using var adapter = new SqliteLibraryHistoryAdapter(store);
        var meeting = new PersistedMeeting
        {
            Id = "meeting-1",
            Title = "Weekly sync",
            CreatedAt = new DateTime(2026, 11, 28, 9, 0, 0, DateTimeKind.Utc),
            Transcript = "[09:00:00] You: hello",
            Summary = "Notes",
            SourcePath = @"C:\captures\mic.wav; C:\captures\system.wav",
            MicrophoneAudioPath = @"C:\captures\mic.wav",
            SystemAudioPath = @"C:\captures\system.wav",
            SessionState = MeetingSessionState.Completed
        };
        adapter.SaveMeetings([meeting]);

        // A list-shaped save without audio fields means "unchanged", by design.
        adapter.SaveMeetings([meeting with { SourcePath = "", MicrophoneAudioPath = null, SystemAudioPath = null }]);
        Assert.Equal(@"C:\captures\mic.wav", adapter.FindMeeting("meeting-1")!.MicrophoneAudioPath);

        Assert.True(adapter.ClearMeetingAudio("meeting-1"));

        var cleared = adapter.FindMeeting("meeting-1")!;
        Assert.Null(cleared.MicrophoneAudioPath);
        Assert.Null(cleared.SystemAudioPath);
        Assert.True(string.IsNullOrEmpty(cleared.SourcePath));
        Assert.Equal("Notes", cleared.Summary);
        Assert.Equal("[09:00:00] You: hello", cleared.Transcript);
    }
}
