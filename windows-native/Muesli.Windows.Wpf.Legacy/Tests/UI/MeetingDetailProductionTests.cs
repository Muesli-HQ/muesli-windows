using System.IO;

namespace Muesli.Windows.UITests;

[Collection("UiAutomation")]
[Trait(UiAutomationEnvironment.TraitName, UiAutomationEnvironment.TraitValue)]
public sealed class MeetingDetailProductionTests
{
    [UiAutomationFact]
    public void Sqlite_meeting_detail_edit_cancel_save_candidate_and_restart_persist() => StaRunner.Run(() =>
    {
        using var session = MuesliUiSession.LaunchProduction(
            enableSqliteHistoryCutover: true,
            seedDeterministicMeeting: true);
        session.Run(() =>
        {
            var databasePath = Path.Combine(session.ProfileMuesliRoot, "data", "muesli.db");
            Assert.True(File.Exists(databasePath), "The enabled L27 cutover did not create muesli.db in the isolated profile.");

            session.NavigateTo("meetings", "Navigate to Meetings", "Manage Templates");
            OpenSeededMeeting(session);
            session.ClickAccessibleName("Transcript");
            session.RequireAccessibleName("Re-transcription candidate ready", mustBeOnscreen: true);
            Assert.Contains(
                "Ready candidate must be explicitly accepted or rejected.",
                session.ReadValueAutomationId("MeetingRetranscriptionCandidate"),
                StringComparison.Ordinal);

            // A ready candidate never changes the displayed transcript until explicit acceptance.
            session.InvokeAutomationId("MeetingTranscriptRejectCandidateButton");
            var original = session.ReadValueAutomationId("MeetingTranscriptReadOnly");
            Assert.Contains("Original transcript survives", original, StringComparison.Ordinal);

            session.InvokeAutomationId("MeetingTranscriptEditButton");
            session.SetValueAutomationId("MeetingTranscriptEditor", "Cancelled draft must not persist.");
            session.InvokeAutomationId("MeetingTranscriptCancelButton");
            Assert.Equal(original, session.ReadValueAutomationId("MeetingTranscriptReadOnly"));

            const string savedTranscript = "Saved transcript survives a production restart.";
            session.InvokeAutomationId("MeetingTranscriptEditButton");
            session.SetValueAutomationId("MeetingTranscriptEditor", savedTranscript);
            session.InvokeAutomationId("MeetingTranscriptSaveButton");
            Assert.Equal(savedTranscript, session.ReadValueAutomationId("MeetingTranscriptReadOnly"));

            session.ClickAccessibleName("Notes");
            Assert.Equal("Seeded launch meeting", session.ReadValueAutomationId("MeetingTitleEditor"));
            Assert.Equal(
                "Manual note survives transcript editing.",
                session.ReadValueAutomationId("MeetingManualNotesEditor"));
            session.RequireAccessibleName("Your title · kept when notes are regenerated", mustBeOnscreen: true);

            session.RestartProduction();
            session.NavigateTo("meetings-after-restart", "Navigate to Meetings", "Manage Templates");
            OpenSeededMeeting(session);
            session.ClickAccessibleName("Transcript");
            Assert.Equal(savedTranscript, session.ReadValueAutomationId("MeetingTranscriptReadOnly"));
            session.ClickAccessibleName("Notes");
            Assert.Equal("Seeded launch meeting", session.ReadValueAutomationId("MeetingTitleEditor"));
            Assert.Equal(
                "Manual note survives transcript editing.",
                session.ReadValueAutomationId("MeetingManualNotesEditor"));
            session.RequireAccessibleName("Your title · kept when notes are regenerated", mustBeOnscreen: true);
        });
    });

    private static void OpenSeededMeeting(MuesliUiSession session)
    {
        session.RequireAccessibleName("Seeded launch meeting", mustBeOnscreen: true);
        // The meeting card opens on a MouseLeftButtonUp handler, and its ListBoxItem ancestor
        // exposes SelectionItem — so a pattern-first click only selects the row and the detail
        // page never opens. Open it the way a user does. The assertion below is unchanged.
        session.ClickAccessibleNameWithPointer("Seeded launch meeting");
        session.RequireAccessibleName("Meeting title", mustBeOnscreen: true);
    }
}
