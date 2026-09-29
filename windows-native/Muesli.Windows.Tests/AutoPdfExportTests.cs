using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// AUTO-01: automatic PDF export shares the Markdown exporter's atomic, collision-safe,
/// manifest-backed machinery and honors the EXP-01 QuestPDF release gate.
/// </summary>
public sealed class AutoPdfExportTests
{
    [Fact]
    public void SettingsMapIntoLmStudioAndPdfAutomationOptions()
    {
        var options = PostMeetingAutomationOptions.FromSettings(new MuesliSettings
        {
            AutoExportMarkdownEnabled = false,
            AutoExportPdfEnabled = true,
            AutoExportMarkdownDirectory = @"C:\exports"
        });

        Assert.False(options.AutoExportEnabled);
        Assert.True(options.AutoExportPdfEnabled);
        Assert.Equal(@"C:\exports", options.AutoExportDirectory);
    }

    [Fact]
    public async Task PdfAutoExportFailsClosedWhileTheReleaseGateIsClosed()
    {
        var original = MeetingDocumentWriter.PdfExportApproved;
        MeetingDocumentWriter.PdfExportApproved = false;
        try
        {
            using var directory = new TestDirectory();

            var result = await new PostMeetingAutomationService().RunAsync(
                Meeting("closed-gate"),
                Options(directory.Path),
                PostMeetingCompletionEvent.RecordingCompleted);

            Assert.Equal(PostMeetingAutomationStatus.Failed, result.Status);
            var pdf = Assert.IsType<PostMeetingExportDiagnostic>(result.PdfExport);
            Assert.True(pdf.Requested);
            Assert.False(pdf.Completed);
            Assert.Contains("EXP-01", pdf.Error, StringComparison.Ordinal);
            // Fail closed before any directory or file is created.
            Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
        }
        finally
        {
            MeetingDocumentWriter.PdfExportApproved = original;
        }
    }

    [Fact]
    public async Task ApprovedPdfAutoExportPublishesARealAtomicPdf()
    {
        var original = MeetingDocumentWriter.PdfExportApproved;
        MeetingDocumentWriter.PdfExportApproved = true;
        try
        {
            using var directory = new TestDirectory();

            var result = await new PostMeetingAutomationService().RunAsync(
                Meeting("approved"),
                Options(directory.Path),
                PostMeetingCompletionEvent.RecordingCompleted);

            var pdf = Assert.IsType<PostMeetingExportDiagnostic>(result.PdfExport);
            Assert.True(pdf.Completed, pdf.Error);
            Assert.Equal(PostMeetingAutomationStatus.Succeeded, result.Status);
            Assert.Equal(AutomationDestinationOwnership.UserSelectedDestination, pdf.DestinationOwnership);
            Assert.False(result.Export.Requested);

            var files = Directory.GetFiles(directory.Path, "*.pdf");
            var path = Assert.Single(files);
            Assert.Equal(path, pdf.DestinationPath);
            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length > 1000, $"PDF was only {bytes.Length} bytes.");
            Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
            Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
            Assert.True(Directory.Exists(Path.Combine(directory.Path, ".muesli-automation")));
        }
        finally
        {
            MeetingDocumentWriter.PdfExportApproved = original;
        }
    }

    [Fact]
    public async Task MarkdownAndPdfAutoExportsPublishIndependentArtifacts()
    {
        var original = MeetingDocumentWriter.PdfExportApproved;
        MeetingDocumentWriter.PdfExportApproved = true;
        try
        {
            using var directory = new TestDirectory();

            var result = await new PostMeetingAutomationService().RunAsync(
                Meeting("both"),
                Options(directory.Path, markdown: true),
                PostMeetingCompletionEvent.RecordingCompleted);

            Assert.Equal(PostMeetingAutomationStatus.Succeeded, result.Status);
            Assert.True(result.Export.Completed);
            Assert.True(result.PdfExport!.Completed);
            Assert.Single(Directory.GetFiles(directory.Path, "*.md"));
            Assert.Single(Directory.GetFiles(directory.Path, "*.pdf"));
        }
        finally
        {
            MeetingDocumentWriter.PdfExportApproved = original;
        }
    }

    [Fact]
    public async Task PdfAutoExportNeverOverwritesAUserFileAndPublishesACollisionFreeCandidate()
    {
        var original = MeetingDocumentWriter.PdfExportApproved;
        MeetingDocumentWriter.PdfExportApproved = true;
        try
        {
            using var directory = new TestDirectory();
            var service = new PostMeetingAutomationService();

            var first = await service.RunAsync(
                Meeting("collision-one"),
                Options(directory.Path),
                PostMeetingCompletionEvent.RecordingCompleted);
            Assert.True(first.PdfExport!.Completed);

            // A second meeting with the same title produces the same suggested filename against a
            // different control key, so the existing user-visible PDF must be left alone and a
            // collision-free candidate published.
            var second = await service.RunAsync(
                Meeting("collision-two"),
                Options(directory.Path),
                PostMeetingCompletionEvent.RecordingCompleted);

            Assert.True(second.PdfExport!.Completed, second.PdfExport.Error);
            Assert.Equal(PostMeetingAutomationStatus.Succeeded, second.Status);
            Assert.NotEqual(first.PdfExport.DestinationPath, second.PdfExport.DestinationPath);
            Assert.Equal(2, Directory.GetFiles(directory.Path, "*.pdf").Length);
        }
        finally
        {
            MeetingDocumentWriter.PdfExportApproved = original;
        }
    }

    [Fact]
    public async Task UnchangedPdfAutoExportReusesTheManifestAndPublishesOnce()
    {
        var original = MeetingDocumentWriter.PdfExportApproved;
        MeetingDocumentWriter.PdfExportApproved = true;
        try
        {
            using var directory = new TestDirectory();
            var service = new PostMeetingAutomationService();
            var meeting = Meeting("reuse");

            var first = await service.RunAsync(
                meeting, Options(directory.Path), PostMeetingCompletionEvent.RecordingCompleted);
            Assert.True(first.PdfExport!.Completed);

            await Task.Delay(1100); // Exercise reuse across QuestPDF's default metadata timestamp boundary.
            var second = await service.RunAsync(
                meeting with { AutomationResult = first },
                Options(directory.Path),
                PostMeetingCompletionEvent.RecordingCompleted);

            Assert.True(second.PdfExport!.Completed, second.PdfExport.Error);
            Assert.Equal(first.PdfExport.DestinationPath, second.PdfExport.DestinationPath);
            Assert.Single(Directory.GetFiles(directory.Path, "*.pdf"));
        }
        finally
        {
            MeetingDocumentWriter.PdfExportApproved = original;
        }
    }

    private static PostMeetingAutomationOptions Options(string directory, bool markdown = false) => new()
    {
        HookEnabled = false,
        AutoExportEnabled = markdown,
        AutoExportPdfEnabled = true,
        AutoExportDirectory = directory,
        AutoExportMode = MeetingExportMode.FullMeeting,
        RetryPolicy = new PostMeetingRetryPolicy { MaxAttempts = 1 }
    };

    private static MeetingItem Meeting(string id) => new(
        Id: id,
        Title: "Release sync",
        CreatedAt: new DateTime(2026, 9, 26, 9, 0, 0, DateTimeKind.Utc),
        Transcript: "[09:00:00] You: we agreed to ship on Friday.",
        Summary: "## Summary\n- Ship on Friday",
        SourcePath: "",
        ModelProfile: "test",
        DurationMs: 1000,
        FolderId: null,
        HealthWarnings: [],
        SessionState: MeetingSessionState.Completed,
        ManualNotes: "Owner: Priya");
}
