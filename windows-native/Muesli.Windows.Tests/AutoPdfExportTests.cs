using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// AUTO-01: automatic PDF export shares the Markdown exporter's atomic, collision-safe,
/// manifest-backed machinery.
/// </summary>

public sealed class AutoPdfExportTests
{
    [Theory]
    [InlineData("markdown", true, false)]
    [InlineData("pdf", false, true)]
    [InlineData("markdown_and_pdf", true, true)]
    public void AutoExportMeetingsWritesTheChosenFileFormats(string format, bool markdown, bool pdf)
    {
        var options = PostMeetingAutomationOptions.FromSettings(new MuesliSettings
        {
            AutoExportMarkdownEnabled = true,
            AutoExportFileFormat = format,
            AutoExportMarkdownDirectory = @"C:\exports"
        });

        Assert.Equal(markdown, options.AutoExportEnabled);
        Assert.Equal(pdf, options.AutoExportPdfEnabled);
        Assert.Equal(@"C:\exports", options.AutoExportDirectory);
    }

    [Theory]
    [InlineData("markdown")]
    [InlineData("pdf")]
    [InlineData("markdown_and_pdf")]
    public void NothingIsExportedWhileAutoExportMeetingsIsOff(string format)
    {
        var options = PostMeetingAutomationOptions.FromSettings(new MuesliSettings
        {
            AutoExportMarkdownEnabled = false,
            AutoExportPdfEnabled = true,
            AutoExportFileFormat = format
        });

        Assert.False(options.AutoExportEnabled);
        Assert.False(options.AutoExportPdfEnabled);
    }

    [Theory]
    [InlineData(false, "markdown")]
    [InlineData(true, "markdown_and_pdf")]
    public void SettingsFromBeforeTheFormatPickerKeepTheirPdfChoice(bool legacyPdf, string expected)
    {
        var settings = new MuesliSettings { AutoExportMarkdownEnabled = true, AutoExportPdfEnabled = legacyPdf };

        Assert.Null(settings.AutoExportFileFormat);
        Assert.Equal(expected, AutoExportFileFormats.Resolve(settings));
    }

    [Fact]
    public void TheFileFormatSurvivesASaveAndLoad()
    {
        using var directory = new TestDirectory();
        var store = new SettingsStore(Path.Combine(directory.Path, "settings.json"), new InMemorySecretStore());
        store.Save(store.Load() with { AutoExportFileFormat = AutoExportFileFormats.Pdf, AutoExportMarkdownContent = "transcript" });

        var loaded = store.Load();

        Assert.Equal(AutoExportFileFormats.Pdf, AutoExportFileFormats.Resolve(loaded));
        Assert.Equal("transcript", loaded.AutoExportMarkdownContent);
    }

    [Theory]
    [InlineData(MeetingExportMode.Notes, "Decisions", "## Raw Transcript")]
    [InlineData(MeetingExportMode.Transcript, "## Raw Transcript", "Decisions")]
    public void NotesAndTranscriptExportsCarryOnlyTheirOwnDocument(MeetingExportMode mode, string present, string absent)
    {
        var markdown = MeetingExportFormatter.BuildMarkdown(Meeting("separate") with
        {
            Summary = "## Decisions\n- Ship it",
            Transcript = "[10:00:00] You: hello"
        }, mode);

        Assert.Contains("# Release sync", markdown);
        Assert.Contains(present, markdown);
        Assert.DoesNotContain(absent, markdown);
    }

    [Fact]
    public async Task PdfAutoExportPublishesARealAtomicPdf()
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

    [Fact]
    public async Task MarkdownAndPdfAutoExportsPublishIndependentArtifacts()
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

    [Fact]
    public async Task PdfAutoExportNeverOverwritesAUserFileAndPublishesACollisionFreeCandidate()
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

    [Fact]
    public async Task UnchangedPdfAutoExportReusesTheManifestAndPublishesOnce()
    {
        using var directory = new TestDirectory();
        var service = new PostMeetingAutomationService();
        var meeting = Meeting("reuse");

        var first = await service.RunAsync(
            meeting, Options(directory.Path), PostMeetingCompletionEvent.RecordingCompleted);
        Assert.True(first.PdfExport!.Completed);

        await Task.Delay(1100); // A later run must still produce the same bytes, or the manifest would not match.
        var second = await service.RunAsync(
            meeting with { AutomationResult = first },
            Options(directory.Path),
            PostMeetingCompletionEvent.RecordingCompleted);

        Assert.True(second.PdfExport!.Completed, second.PdfExport.Error);
        Assert.Equal(first.PdfExport.DestinationPath, second.PdfExport.DestinationPath);
        Assert.Single(Directory.GetFiles(directory.Path, "*.pdf"));
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
