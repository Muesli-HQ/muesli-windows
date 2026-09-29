using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// The meeting-detail automation diagnostics surface renders this Core formatter, so it is the
/// shared contract between POST_MEETING_AUTOMATION.md, persistence, and the WinUI shell.
/// </summary>
public sealed class PostMeetingAutomationDiagnosticsTests
{
    [Fact]
    public void NoResultIsReportedWithoutPretendingAutomationRan()
    {
        Assert.False(PostMeetingAutomationDiagnostics.HasResult(null));
        Assert.Equal("Not run", PostMeetingAutomationDiagnostics.StatusLabel(null));
        Assert.Equal(PostMeetingAutomationDiagnostics.NoResultMessage, PostMeetingAutomationDiagnostics.Format(null));
        Assert.Equal("", PostMeetingAutomationDiagnostics.RawOutput(null));
    }

    [Fact]
    public void SuccessfulHookAndMarkdownExportAreSummarized()
    {
        var result = Result(
            PostMeetingAutomationStatus.Succeeded,
            attempts: 1,
            exitCode: 0,
            export: new PostMeetingExportDiagnostic(
                true, true, @"C:\exports\release-sync.md",
                AutomationDestinationOwnership.UserSelectedDestination, null, 1));

        var text = PostMeetingAutomationDiagnostics.Format(result);

        Assert.Equal("Succeeded", PostMeetingAutomationDiagnostics.StatusLabel(result));
        Assert.Contains("Attempts: 1", text, StringComparison.Ordinal);
        Assert.Contains("Hook exit code: 0", text, StringComparison.Ordinal);
        Assert.Contains("Hook: completed", text, StringComparison.Ordinal);
        Assert.Contains(@"Markdown export: completed → C:\exports\release-sync.md", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PDF export:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedExportAndPdfAreReportedWithTheirReasons()
    {
        var result = Result(
            PostMeetingAutomationStatus.Failed,
            attempts: 3,
            exitCode: 17,
            export: new PostMeetingExportDiagnostic(
                true, false, null, AutomationDestinationOwnership.None, "disk full", 0),
            pdfExport: new PostMeetingExportDiagnostic(
                true, false, null, AutomationDestinationOwnership.None,
                "PDF export is disabled until QuestPDF Community-license eligibility is approved (EXP-01).", 0));

        var text = PostMeetingAutomationDiagnostics.Format(result);

        Assert.Equal("Failed", PostMeetingAutomationDiagnostics.StatusLabel(result));
        Assert.Contains("Markdown export: failed (disk full)", text, StringComparison.Ordinal);
        Assert.Contains("PDF export: failed (PDF export is disabled until QuestPDF", text, StringComparison.Ordinal);
        Assert.Contains("Attempts: 3", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledRunIsLabelledOffWithoutClaimingAFailure()
    {
        var result = Result(PostMeetingAutomationStatus.Disabled, attempts: 0, exitCode: null,
            export: PostMeetingExportDiagnostic.NotRequested);

        Assert.Equal("Off", PostMeetingAutomationDiagnostics.StatusLabel(result));
        Assert.Contains("Hook: not run", PostMeetingAutomationDiagnostics.Format(result), StringComparison.Ordinal);
        Assert.Contains("Markdown export: not requested", PostMeetingAutomationDiagnostics.Format(result), StringComparison.Ordinal);
    }

    [Fact]
    public void RawOutputIsEmptyWhenThereIsNothingToShow()
    {
        var result = Result(PostMeetingAutomationStatus.Succeeded, attempts: 1, exitCode: 0,
            export: PostMeetingExportDiagnostic.NotRequested);

        Assert.Equal("", PostMeetingAutomationDiagnostics.RawOutput(result));
    }

    [Fact]
    public void RawOutputMarksTruncatedStreams()
    {
        var result = Result(
            PostMeetingAutomationStatus.Failed,
            attempts: 1,
            exitCode: 17,
            export: PostMeetingExportDiagnostic.NotRequested,
            standardOutput: "[REDACTED CONTENT OUTPUT]",
            standardError: "[REDACTED SECRET]",
            standardOutputTruncated: true,
            standardErrorTruncated: false);

        var text = PostMeetingAutomationDiagnostics.RawOutput(result);

        Assert.Contains("stdout (truncated):", text, StringComparison.Ordinal);
        Assert.Contains("stderr:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("host-crash-password", text, StringComparison.Ordinal);
    }

    private static PostMeetingAutomationResult Result(
        PostMeetingAutomationStatus status,
        int attempts,
        int? exitCode,
        PostMeetingExportDiagnostic export,
        PostMeetingExportDiagnostic? pdfExport = null,
        string standardOutput = "",
        string standardError = "",
        bool standardOutputTruncated = false,
        bool standardErrorTruncated = false) =>
        new(
            Guid.NewGuid(),
            status,
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow,
            attempts,
            exitCode,
            standardOutput,
            standardError,
            standardOutputTruncated,
            standardErrorTruncated,
            null,
            export)
        {
            PdfExport = pdfExport
        };
}
