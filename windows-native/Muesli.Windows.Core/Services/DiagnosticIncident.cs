using System.Reflection;
using System.Runtime.InteropServices;
using Muesli.Windows.Core.Insights;

namespace Muesli.Windows.Services;

/// <summary>
/// macOS DiagnosticIncident: an allowlisted summary of a hard failure that the user can review and
/// open as a GitHub issue. Only fixed categories, the exception type, its HRESULT and a random id are
/// kept; messages, paths, transcripts and logs never are.
/// </summary>
public sealed record DiagnosticIncident(
    Guid Id,
    string Kind,
    string Stage,
    string Model,
    DiagnosticErrorFingerprint Error,
    string AppVersion,
    string WindowsVersion,
    string Architecture,
    DateTimeOffset OccurredAt)
{
    public const string ManualReport = "manual_report";
    public const string DictationAudioFailed = "dictation_audio_failed";
    public const string DictationTranscriptionFailed = "dictation_transcription_failed";
    public const string MeetingStartFailed = "meeting_start_failed";
    public const string MeetingProcessingFailed = "meeting_processing_failed";

    public const string IssuesUrl = ContributionShare.GitHubUrl + "/issues/new";

    private static readonly Dictionary<string, string> Titles = new()
    {
        [ManualReport] = "Manual problem report",
        [DictationAudioFailed] = "Dictation audio capture failed",
        [DictationTranscriptionFailed] = "Dictation transcription failed",
        [MeetingStartFailed] = "Meeting recording failed to start",
        [MeetingProcessingFailed] = "Meeting processing failed",
    };

    public static DiagnosticIncident Create(string kind, string stage, string? model = null, Exception? exception = null) => new(
        Guid.NewGuid(), kind, stage, string.IsNullOrWhiteSpace(model) ? "unknown" : model,
        DiagnosticErrorFingerprint.For(exception, kind),
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown",
        Environment.OSVersion.Version.ToString(),
        RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        DateTimeOffset.Now);

    public bool IsManual => Kind == ManualReport;
    public string Title => Titles.GetValueOrDefault(Kind, Kind);
    public string Severity => IsManual ? "info" : "error";
    public string UserImpact => IsManual ? "informational" : "operation_blocked";
    public string ErrorDisplay => Error.Domain is { } domain && Error.Code is { } code ? $"{domain} {code}" : Error.Signature;

    public string IssueTitle => $"[Diagnostic] {Title}";

    public string IssueBody => $"""
        ### What happened?
        Please describe what you were trying to do and what you expected to happen.

        ### Privacy
        This report was generated from an allowlisted diagnostic summary. It does not include transcripts, audio, meeting titles, calendar titles, clipboard contents, screen/OCR text, API keys, auth tokens, local file paths, raw error messages, raw logs, or database contents.

        ### Anonymized diagnostics
        - Incident: {Kind}
        - Severity: {Severity}
        - User impact: {UserImpact}
        - Stage: {Stage}
        - App: Muesli for Windows
        - Version: {AppVersion}
        - Windows: {WindowsVersion}
        - Architecture: {Architecture}
        - Model: {Model}
        - Error signature: {Error.Signature}
        - Error domain: {Error.Domain ?? "unclassified"}
        - Error code: {Error.Code ?? "unclassified"}
        - Error meaning: {Error.Summary}
        - Incident ID: {Id}
        """;

    public Uri IssueUri => new($"{IssuesUrl}?title={Uri.EscapeDataString(IssueTitle)}&body={Uri.EscapeDataString(IssueBody)}");
}

/// <summary>macOS DiagnosticErrorFingerprint: the exception's type and HRESULT, never its message.</summary>
public sealed record DiagnosticErrorFingerprint(string Signature, string Summary, string? Domain, string? Code)
{
    // Most specific first: the first assignable entry wins.
    private static readonly (Type Type, string Summary)[] Known =
    [
        (typeof(UnauthorizedAccessException), "Windows denied access to a device or file"),
        (typeof(FileNotFoundException), "A required file was missing"),
        (typeof(DirectoryNotFoundException), "A required folder was missing"),
        (typeof(IOException), "A file or device could not be read or written"),
        (typeof(TimeoutException), "The operation timed out"),
        (typeof(OutOfMemoryException), "The app ran out of memory"),
        (typeof(COMException), "A Windows component reported an error"),
        (typeof(InvalidOperationException), "The app was in an unexpected state"),
    ];

    public static DiagnosticErrorFingerprint For(Exception? exception, string kind)
    {
        if (exception is null) return new($"app_state.{kind}", "The app reached a failed state without an exception", null, null);
        var type = exception.GetType();
        // Type names from our code and the runtime are safe identifiers; anything else stays anonymous.
        var ns = type.Namespace ?? "";
        var domain = ns.StartsWith("System", StringComparison.Ordinal) || ns.StartsWith("Microsoft", StringComparison.Ordinal) ||
                     ns.StartsWith("Muesli", StringComparison.Ordinal) || ns.StartsWith("Windows", StringComparison.Ordinal)
            ? type.Name : "external";
        var code = $"0x{exception.HResult:X8}";
        var summary = Known.FirstOrDefault(entry => entry.Type.IsAssignableFrom(type)).Summary
            ?? "Unclassified failure; private error details were omitted";
        return new($"{domain}.{code}", summary, domain, code);
    }
}
