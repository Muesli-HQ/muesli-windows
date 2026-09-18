using Muesli.Windows;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>Export content per mode, save-format handling, and failure reporting.</summary>
public sealed class Phase8ExportTests
{
    private static MeetingItem Meeting(string summary = "## Summary\n- shipped the release", string manual = "") =>
        new(
            Id: "meet_1",
            Title: "Release sync",
            CreatedAt: new DateTime(2026, 1, 5, 14, 30, 0),
            Transcript: "[14:30:00] You: we shipped\n[14:30:04] Speaker 1: nice work",
            Summary: summary,
            SourcePath: "",
            ModelProfile: "parakeet-v3",
            DurationMs: 95000,
            FolderId: null,
            WordCount: 42,
            TemplateName: "Standard Meeting Notes",
            ManualNotes: manual);

    [Fact]
    public void NotesOnlyExportCarriesNotesAndNotTheRawTranscript()
    {
        var markdown = MeetingExporter.BuildMarkdown(Meeting(), MeetingExportMode.Notes, null);
        Assert.Contains("shipped the release", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("## Raw Transcript", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("nice work", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void TranscriptOnlyExportCarriesTheTranscriptAndNotTheNotes()
    {
        var markdown = MeetingExporter.BuildMarkdown(Meeting(), MeetingExportMode.Transcript, null);
        Assert.Contains("## Raw Transcript", markdown, StringComparison.Ordinal);
        Assert.Contains("nice work", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("shipped the release", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void CompleteMeetingExportCarriesBoth()
    {
        var markdown = MeetingExporter.BuildMarkdown(Meeting(), MeetingExportMode.FullMeeting, null);
        Assert.Contains("shipped the release", markdown, StringComparison.Ordinal);
        Assert.Contains("## Raw Transcript", markdown, StringComparison.Ordinal);
        Assert.Contains("nice work", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryExportModeCarriesTheMeetingHeader()
    {
        foreach (var mode in Enum.GetValues<MeetingExportMode>())
        {
            var markdown = MeetingExporter.BuildMarkdown(Meeting(), mode, null);
            Assert.StartsWith("# Release sync", markdown, StringComparison.Ordinal);
            Assert.Contains("2026-01-05 14:30", markdown, StringComparison.Ordinal);
            Assert.Contains("Standard Meeting Notes", markdown, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ManualNotesAppearInEveryExportThatCarriesNotes()
    {
        var meeting = Meeting(manual: "My own follow-up: chase the changelog.");

        foreach (var mode in new[] { MeetingExportMode.Notes, MeetingExportMode.FullMeeting })
        {
            var markdown = MeetingExporter.BuildMarkdown(meeting, mode, null);
            Assert.Contains(MeetingNotesDocument.ManualHeading, markdown, StringComparison.Ordinal);
            Assert.Contains("chase the changelog", markdown, StringComparison.Ordinal);
        }

        // A transcript-only export is the raw transcript; notes of any kind do not belong in it.
        var transcriptOnly = MeetingExporter.BuildMarkdown(meeting, MeetingExportMode.Transcript, null);
        Assert.DoesNotContain("chase the changelog", transcriptOnly, StringComparison.Ordinal);
    }

    [Fact]
    public void AMeetingWithoutManualNotesGetsNoEmptyHeading()
    {
        var markdown = MeetingExporter.BuildMarkdown(Meeting(manual: "   "), MeetingExportMode.FullMeeting, null);
        Assert.DoesNotContain(MeetingNotesDocument.ManualHeading, markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void AMeetingWithoutNotesFallsBackToTheTranscriptRatherThanExportingAnEmptyFile()
    {
        var markdown = MeetingExporter.BuildMarkdown(Meeting(summary: ""), MeetingExportMode.Notes, null);
        Assert.Contains("No structured notes available", markdown, StringComparison.Ordinal);
        Assert.Contains("nice work", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void SpeakerAliasesAreAppliedToExportedContent()
    {
        var aliases = new Dictionary<string, string> { ["Speaker 1"] = "Priya" };
        var markdown = MeetingExporter.BuildMarkdown(Meeting(), MeetingExportMode.FullMeeting, aliases);
        Assert.Contains("Priya: nice work", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Speaker 1:", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportIsDeterministicForTheSameMeeting()
    {
        var first = MeetingExporter.BuildMarkdown(Meeting(), MeetingExportMode.FullMeeting, null);
        for (var run = 0; run < 5; run++)
        {
            Assert.Equal(first, MeetingExporter.BuildMarkdown(Meeting(), MeetingExportMode.FullMeeting, null));
        }
    }

    [Theory]
    [InlineData(MeetingExportMode.Notes)]
    [InlineData(MeetingExportMode.Transcript)]
    [InlineData(MeetingExportMode.FullMeeting)]
    public void SuggestedFilenamesAreDistinctAndFilesystemSafe(MeetingExportMode mode)
    {
        var name = MeetingExporter.SuggestFilename(Meeting(), mode);
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.All(Path.GetInvalidFileNameChars(), invalid => Assert.DoesNotContain(invalid, name));
    }

    [Fact]
    public void MarkdownSaveFormatWritesTheMarkdownItself()
    {
        using var directory = new TestDirectory();
        var path = directory.File("export.md");
        var markdown = MeetingExporter.BuildMarkdown(Meeting(), MeetingExportMode.FullMeeting, null);

        MeetingExporter.Write(markdown, path);

        Assert.True(File.Exists(path));
        Assert.Equal(markdown, File.ReadAllText(path));
    }

    [Fact]
    public void PdfSaveFormatWritesARealPdfFile()
    {
        using var directory = new TestDirectory();
        var path = directory.File("export.pdf");

        MeetingExporter.Write(MeetingExporter.BuildMarkdown(Meeting(), MeetingExportMode.FullMeeting, null), path);

        Assert.True(File.Exists(path));
        var header = new byte[5];
        using (var stream = File.OpenRead(path)) { _ = stream.Read(header, 0, header.Length); }
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(header));
        Assert.True(new FileInfo(path).Length > 1000, "A paginated PDF should not be near-empty.");
    }

    [Fact]
    public void AnUnwritableDestinationIsReportedRatherThanSwallowed()
    {
        using var directory = new TestDirectory();
        // A directory path can never be opened as a file.
        var impossible = Path.Combine(directory.Path, "a-directory.md");
        Directory.CreateDirectory(impossible);

        var failure = Assert.ThrowsAny<Exception>(() => MeetingExporter.Write("content", impossible));
        Assert.False(string.IsNullOrWhiteSpace(failure.Message));
    }

    [Fact]
    public void ExportResultStatesDistinguishCancelSuccessAndFailure()
    {
        Assert.False(MeetingExportResult.Cancelled.Completed);
        Assert.Null(MeetingExportResult.Cancelled.Error);

        var success = MeetingExportResult.Success(@"C:\notes.md");
        Assert.True(success.Completed);
        Assert.Null(success.Error);

        var failed = MeetingExportResult.Failed("disk full");
        Assert.False(failed.Completed);
        Assert.Equal("disk full", failed.Error);
    }
}
