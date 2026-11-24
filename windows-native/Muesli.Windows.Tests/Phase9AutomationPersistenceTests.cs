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
        using var store = TestLibrary.Open(directory.Path);
        store.SaveMeetings([new PersistedMeeting
        {
            SchemaVersion = PersistedMeeting.CurrentSchemaVersion,
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

}
