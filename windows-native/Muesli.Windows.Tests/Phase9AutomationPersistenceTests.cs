namespace Muesli.Windows.Tests;

public sealed class Phase9AutomationPersistenceTests
{
    [Fact]
    public void PerMeetingAutomationDiagnosticsRoundTripSeparatelyFromCaptureWarnings()
    {
        using var directory = new TestDirectory();
        var result = new PostMeetingAutomationResult(
            Guid.NewGuid(),
            PostMeetingAutomationStatus.Failed,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            DateTimeOffset.UtcNow,
            2,
            17,
            "bounded redacted stdout",
            "bounded redacted stderr",
            false,
            true,
            "Hook exited with code 17.",
            new PostMeetingExportDiagnostic(
                true,
                true,
                directory.File("meeting.md"),
                AutomationDestinationOwnership.UserSelectedDestination,
                null,
                1));
        var store = new AppDataStore(directory.Path);
        store.SaveMeetings([new PersistedMeeting
        {
            SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion,
            Id = "meeting-with-automation",
            Title = "Saved before hook",
            CreatedAt = DateTime.UtcNow,
            SessionState = MeetingSessionState.Completed,
            HealthWarnings = ["capture warning"],
            AutomationResult = result
        }]);

        var loaded = Assert.Single(store.LoadMeetings());

        Assert.Equal(result, loaded.AutomationResult);
        Assert.Equal(["capture warning"], loaded.HealthWarnings);
        Assert.Equal(MeetingSessionState.Completed, loaded.SessionState);
    }

    [Fact]
    public void ExistingMeetingsLoadWithoutRetroactiveAutomationDiagnostics()
    {
        using var directory = new TestDirectory();
        var store = new AppDataStore(directory.Path);
        store.SaveMeetings([new PersistedMeeting
        {
            SchemaVersion = 4,
            Id = "pre-automation",
            Title = "Existing meeting",
            CreatedAt = DateTime.UtcNow,
            SessionState = MeetingSessionState.Completed
        }]);

        var loaded = Assert.Single(store.LoadMeetings());

        Assert.Null(loaded.AutomationResult);
        Assert.Equal(AppDataStore.CurrentMeetingSchemaVersion, loaded.SchemaVersion);
    }
}
