using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// Restores the manual export content/format safety net (EXP-01) that was retired with the WPF test
/// project. Exercises the shared deterministic Markdown renderer and the Markdown/PDF writer on the
/// active tree, including the EXP-01 PDF release gate.
/// </summary>
public sealed class MeetingExportTests
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
        var markdown = MeetingExportFormatter.BuildMarkdown(Meeting(), MeetingExportMode.Notes, null);
        Assert.Contains("shipped the release", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("## Raw Transcript", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("nice work", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void TranscriptOnlyExportCarriesTheTranscriptAndNotTheNotes()
    {
        var markdown = MeetingExportFormatter.BuildMarkdown(Meeting(), MeetingExportMode.Transcript, null);
        Assert.Contains("## Raw Transcript", markdown, StringComparison.Ordinal);
        Assert.Contains("nice work", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("shipped the release", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void CompleteMeetingExportCarriesBoth()
    {
        var markdown = MeetingExportFormatter.BuildMarkdown(Meeting(), MeetingExportMode.FullMeeting, null);
        Assert.Contains("shipped the release", markdown, StringComparison.Ordinal);
        Assert.Contains("## Raw Transcript", markdown, StringComparison.Ordinal);
        Assert.Contains("nice work", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryExportModeCarriesTheMeetingHeader()
    {
        foreach (var mode in Enum.GetValues<MeetingExportMode>())
        {
            var markdown = MeetingExportFormatter.BuildMarkdown(Meeting(), mode, null);
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
            var markdown = MeetingExportFormatter.BuildMarkdown(meeting, mode, null);
            Assert.Contains(MeetingNotesDocument.ManualHeading, markdown, StringComparison.Ordinal);
            Assert.Contains("chase the changelog", markdown, StringComparison.Ordinal);
        }

        var transcriptOnly = MeetingExportFormatter.BuildMarkdown(meeting, MeetingExportMode.Transcript, null);
        Assert.DoesNotContain("chase the changelog", transcriptOnly, StringComparison.Ordinal);
    }

    [Fact]
    public void AMeetingWithoutManualNotesGetsNoEmptyHeading()
    {
        var markdown = MeetingExportFormatter.BuildMarkdown(Meeting(manual: "   "), MeetingExportMode.FullMeeting, null);
        Assert.DoesNotContain(MeetingNotesDocument.ManualHeading, markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void AMeetingWithoutNotesFallsBackToTheTranscriptRatherThanExportingAnEmptyFile()
    {
        var markdown = MeetingExportFormatter.BuildMarkdown(Meeting(summary: ""), MeetingExportMode.Notes, null);
        Assert.Contains("No structured notes available", markdown, StringComparison.Ordinal);
        Assert.Contains("nice work", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void SpeakerAliasesAreAppliedToExportedContent()
    {
        var aliases = new Dictionary<string, string> { ["Speaker 1"] = "Priya" };
        var markdown = MeetingExportFormatter.BuildMarkdown(Meeting(), MeetingExportMode.FullMeeting, aliases);
        Assert.Contains("Priya: nice work", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Speaker 1:", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportIsDeterministicForTheSameMeeting()
    {
        var first = MeetingExportFormatter.BuildMarkdown(Meeting(), MeetingExportMode.FullMeeting, null);
        for (var run = 0; run < 5; run++)
        {
            Assert.Equal(first, MeetingExportFormatter.BuildMarkdown(Meeting(), MeetingExportMode.FullMeeting, null));
        }
    }

    [Theory]
    [InlineData(MeetingExportMode.Notes)]
    [InlineData(MeetingExportMode.Transcript)]
    [InlineData(MeetingExportMode.FullMeeting)]
    public void SuggestedFilenamesAreDistinctAndFilesystemSafe(MeetingExportMode mode)
    {
        var name = MeetingExportFormatter.SuggestFilename(Meeting(), mode);
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.All(Path.GetInvalidFileNameChars(), invalid => Assert.DoesNotContain(invalid, name));
    }

    [Fact]
    public void MarkdownSaveFormatWritesTheMarkdownItself()
    {
        using var directory = new TestDirectory();
        var path = directory.File("export.md");
        var markdown = MeetingExportFormatter.BuildMarkdown(Meeting(), MeetingExportMode.FullMeeting, null);

        MeetingDocumentWriter.Write(markdown, path);

        Assert.True(File.Exists(path));
        Assert.Equal(markdown, File.ReadAllText(path));
    }

    [Fact]
    public void PdfSaveFormatIsRejectedWhileTheReleaseGateIsClosed()
    {
        var original = MeetingDocumentWriter.PdfExportApproved;
        MeetingDocumentWriter.PdfExportApproved = false;
        try
        {
            using var directory = new TestDirectory();
            var path = directory.File("export.pdf");

            var failure = Assert.Throws<InvalidOperationException>(() =>
                MeetingDocumentWriter.Write(
                    MeetingExportFormatter.BuildMarkdown(Meeting(), MeetingExportMode.FullMeeting, null), path));

            Assert.Contains("EXP-01", failure.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(path));
        }
        finally
        {
            MeetingDocumentWriter.PdfExportApproved = original;
        }
    }

    [Fact]
    public void PdfSaveFormatWritesARealPdfFileWhenApproved()
    {
        var original = MeetingDocumentWriter.PdfExportApproved;
        MeetingDocumentWriter.PdfExportApproved = true;
        try
        {
            using var directory = new TestDirectory();
            var path = directory.File("export.pdf");

            MeetingDocumentWriter.Write(
                MeetingExportFormatter.BuildMarkdown(Meeting(), MeetingExportMode.FullMeeting, null), path);

            Assert.True(File.Exists(path));
            var header = new byte[5];
            using (var stream = File.OpenRead(path)) { _ = stream.Read(header, 0, header.Length); }
            Assert.Equal("%PDF-", Encoding.ASCII.GetString(header));
            Assert.True(new FileInfo(path).Length > 1000, "A paginated PDF should not be near-empty.");
        }
        finally
        {
            MeetingDocumentWriter.PdfExportApproved = original;
        }
    }

    [Fact]
    public void AnUnwritableDestinationIsReportedRatherThanSwallowed()
    {
        using var directory = new TestDirectory();
        var impossible = Path.Combine(directory.Path, "a-directory.md");
        Directory.CreateDirectory(impossible);

        var failure = Assert.ThrowsAny<Exception>(() => MeetingDocumentWriter.Write("content", impossible));
        Assert.False(string.IsNullOrWhiteSpace(failure.Message));
    }
}
