using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class MeetingCopyDocumentTests
{
    private static MeetingItem Meeting(string summary) => new(
        Id: "copy",
        Title: "Release sync",
        CreatedAt: new DateTime(2026, 11, 28, 9, 0, 0, DateTimeKind.Utc),
        Transcript: "[09:00:00] You: the secret launch date",
        Summary: summary,
        SourcePath: "",
        ModelProfile: "test",
        DurationMs: 1000,
        FolderId: null,
        HealthWarnings: [],
        SessionState: MeetingSessionState.Completed,
        ManualNotes: "Owner: Priya");

    [Fact]
    public void CopyingNotesWithoutGeneratedNotesTakesOnlyMyNotes()
    {
        var copied = MeetingExportFormatter.BuildVisibleDocument(Meeting(""), MeetingExportMode.Notes);

        Assert.Contains("Owner: Priya", copied);
        Assert.DoesNotContain("secret launch date", copied);
        Assert.DoesNotContain("Raw Transcript", copied);
    }

    [Fact]
    public void ExportingNotesWithoutGeneratedNotesStillIncludesTheTranscriptLikeMacOS()
    {
        var exported = MeetingExportFormatter.BuildMarkdown(Meeting(""), MeetingExportMode.Notes);

        Assert.Contains("secret launch date", exported);
    }

    [Fact]
    public void CopyingNotesWithGeneratedNotesTakesThemAndMyNotes()
    {
        var copied = MeetingExportFormatter.BuildVisibleDocument(Meeting("## Decisions\n- Ship"), MeetingExportMode.Notes);

        Assert.Contains("## Decisions", copied);
        Assert.Contains("Owner: Priya", copied);
        Assert.DoesNotContain("secret launch date", copied);
    }
}
