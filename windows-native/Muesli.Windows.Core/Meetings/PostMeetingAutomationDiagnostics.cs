using System.Text;

namespace Muesli.Windows.Services;

/// <summary>
/// Renders the per-meeting post-meeting automation diagnostics promised by
/// <c>docs/POST_MEETING_AUTOMATION.md</c>. The output is human-readable, redacted content already
/// bounded by the runner, and shared by the WinUI meeting detail so the shell and the contract
/// cannot drift.
/// </summary>
public static class PostMeetingAutomationDiagnostics
{
    public const string NoResultMessage = "No post-meeting automation has run for this meeting.";

    public static bool HasResult(PostMeetingAutomationResult? result) => result is not null;

    public static string StatusLabel(PostMeetingAutomationResult? result) => result is null
        ? "Not run"
        : result.Status switch
        {
            PostMeetingAutomationStatus.Disabled => "Off",
            PostMeetingAutomationStatus.Succeeded => "Succeeded",
            PostMeetingAutomationStatus.InvalidConfiguration => "Invalid configuration",
            PostMeetingAutomationStatus.Failed => "Failed",
            PostMeetingAutomationStatus.TimedOut => "Timed out",
            PostMeetingAutomationStatus.Cancelled => "Cancelled",
            _ => result.Status.ToString()
        };

    public static string Format(PostMeetingAutomationResult? result)
    {
        if (result is null)
        {
            return NoResultMessage;
        }

        var lines = new List<string>
        {
            $"Status: {StatusLabel(result)}",
            $"When: {result.StartedAtUtc.ToLocalTime():g} → {result.CompletedAtUtc.ToLocalTime():g}",
            $"Attempts: {result.Attempts}",
            result.ExitCode is { } exitCode ? $"Hook exit code: {exitCode}" : "Hook exit code: none",
            $"Hook: {HookState(result)}",
            $"Markdown export: {ExportState(result.Export)}"
        };
        if (result.PdfExport is { } pdf)
        {
            lines.Add($"PDF export: {ExportState(pdf)}");
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            lines.Add($"Failure: {result.Error}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The bounded, already-redacted stdout/stderr retained for the latest run. Returns an empty
    /// string when there is nothing to show rather than a placeholder that looks like output.
    /// </summary>
    public static string RawOutput(PostMeetingAutomationResult? result)
    {
        if (result is null)
        {
            return "";
        }

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            builder.AppendLine(result.StandardOutputTruncated ? "stdout (truncated):" : "stdout:");
            builder.AppendLine(result.StandardOutput);
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.AppendLine(result.StandardErrorTruncated ? "stderr (truncated):" : "stderr:");
            builder.AppendLine(result.StandardError);
        }

        return builder.ToString().TrimEnd();
    }

    private static string HookState(PostMeetingAutomationResult result)
    {
        if (!result.Export.Requested && result.Attempts == 0 && result.ExitCode is null &&
            result.Status is PostMeetingAutomationStatus.Disabled or PostMeetingAutomationStatus.InvalidConfiguration)
        {
            return "not run";
        }

        return result.Status switch
        {
            PostMeetingAutomationStatus.Succeeded => "completed",
            PostMeetingAutomationStatus.TimedOut => "timed out",
            PostMeetingAutomationStatus.Cancelled => "cancelled",
            PostMeetingAutomationStatus.InvalidConfiguration => "not run (invalid configuration)",
            _ => "not completed"
        };
    }

    private static string ExportState(PostMeetingExportDiagnostic export)
    {
        if (!export.Requested)
        {
            return "not requested";
        }

        if (export.Completed)
        {
            return string.IsNullOrWhiteSpace(export.DestinationPath)
                ? "completed"
                : $"completed → {export.DestinationPath}";
        }

        var reason = string.IsNullOrWhiteSpace(export.Error) ? "failed" : export.Error;
        return $"failed ({reason})";
    }
}
