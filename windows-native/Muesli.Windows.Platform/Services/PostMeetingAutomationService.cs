using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Muesli.Windows.Services;

public sealed class PostMeetingAutomationService
{
    internal const int ContractVersion = 1;
    private const int MaximumAttempts = 3;
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(10);
    private const int MaximumOutputCharacters = 1024 * 1024;

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<PostMeetingAutomationResult> RunAsync(
        MeetingItem meeting,
        PostMeetingAutomationOptions options,
        PostMeetingCompletionEvent completionEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        ArgumentNullException.ThrowIfNull(options);

        var runId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        if (completionEvent == PostMeetingCompletionEvent.ManualTest)
        {
            return Result(runId, PostMeetingAutomationStatus.InvalidConfiguration, startedAt,
                error: "Manual-test events are only available through TestHookAsync.");
        }

        if (meeting.SessionState != MeetingSessionState.Completed)
        {
            return Result(runId, PostMeetingAutomationStatus.InvalidConfiguration, startedAt,
                error: "Post-meeting automation requires a completed meeting.");
        }

        if (!options.HookEnabled && !options.AutoExportEnabled && !options.AutoExportPdfEnabled)
        {
            return Result(runId, PostMeetingAutomationStatus.Disabled, startedAt, export: PostMeetingExportDiagnostic.NotRequested);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Result(runId, PostMeetingAutomationStatus.Cancelled, startedAt, export: PostMeetingExportDiagnostic.NotRequested);
        }

        var export = !options.AutoExportEnabled
            ? PostMeetingExportDiagnostic.NotRequested
            : await PostMeetingMarkdownAutoExporter.ExportAsync(
                meeting,
                completionEvent,
                options.AutoExportDirectory,
                options.AutoExportMode,
                options.RetryPolicy?.MaxAttempts ?? 1,
                meeting.AutomationResult?.Export,
                cancellationToken).ConfigureAwait(false);

        var pdfExport = await ExportPdfIfRequestedAsync(
            meeting, completionEvent, options, cancellationToken).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            return Result(
                runId,
                PostMeetingAutomationStatus.Cancelled,
                startedAt,
                error: CombineErrors(export.Error, pdfExport?.Error),
                export: export,
                pdfExport: pdfExport);
        }

        var exportCompleted = (!export.Requested || export.Completed) &&
                              (pdfExport is not { Requested: true } || pdfExport.Completed);
        var exportError = CombineErrors(export.Error, pdfExport?.Error);

        if (!options.HookEnabled)
        {
            return Result(
                runId,
                exportCompleted ? PostMeetingAutomationStatus.Succeeded : PostMeetingAutomationStatus.Failed,
                startedAt,
                error: exportError,
                export: export,
                pdfExport: pdfExport);
        }

        var executableError = ValidateExecutable(options.HookExecutablePath, out var executablePath);
        if (executableError is not null)
        {
            return Result(
                runId,
                PostMeetingAutomationStatus.InvalidConfiguration,
                startedAt,
                error: CombineErrors(executableError, exportError),
                export: export,
                pdfExport: pdfExport);
        }

        var payload = BuildPayload(meeting, completionEvent, options.TranscriptPolicy, export);
        var sensitiveValues = SensitiveValues(meeting);
        return await RunHookWithRetriesAsync(
            runId,
            startedAt,
            executablePath!,
            payload,
            sensitiveValues,
            suppressCapturedOutput: sensitiveValues.Count > 0,
            options,
            export,
            pdfExport,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the optional automatic PDF beside the Markdown export. Null when PDF auto-export was
    /// not requested; a fail-closed diagnostic when requested but the EXP-01 license gate is shut.
    /// </summary>
    private static async Task<PostMeetingExportDiagnostic?> ExportPdfIfRequestedAsync(
        MeetingItem meeting,
        PostMeetingCompletionEvent completionEvent,
        PostMeetingAutomationOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.AutoExportPdfEnabled)
        {
            return null;
        }

        if (!MeetingDocumentWriter.PdfExportApproved)
        {
            return new PostMeetingExportDiagnostic(
                Requested: true,
                Completed: false,
                DestinationPath: null,
                DestinationOwnership: AutomationDestinationOwnership.None,
                Error: "PDF export is disabled until QuestPDF Community-license eligibility is approved (EXP-01).",
                Attempts: 0);
        }

        return await PostMeetingMarkdownAutoExporter.ExportPdfAsync(
            meeting,
            completionEvent,
            options.AutoExportDirectory,
            options.AutoExportMode,
            options.RetryPolicy?.MaxAttempts ?? 1,
            meeting.AutomationResult?.PdfExport,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the selected hook even when the normal hook toggle is off. The payload is synthetic and
    /// intentionally contains no transcript, generated notes, manual notes, source path, or export path.
    /// </summary>
    public async Task<PostMeetingAutomationResult> TestHookAsync(
        PostMeetingAutomationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var synthetic = new MeetingItem(
            Id: $"manual-test-{Guid.NewGuid():N}",
            Title: "Muesli post-meeting hook test",
            CreatedAt: DateTime.UtcNow,
            Transcript: "",
            Summary: "",
            SourcePath: "",
            ModelProfile: "manual-test",
            DurationMs: 0,
            FolderId: null,
            HealthWarnings: [],
            SessionState: MeetingSessionState.Completed,
            ManualNotes: "");

        var runId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        if (cancellationToken.IsCancellationRequested)
            return Result(runId, PostMeetingAutomationStatus.Cancelled, startedAt);

        var executableError = ValidateExecutable(options.HookExecutablePath, out var executablePath);
        if (executableError is not null)
            return Result(runId, PostMeetingAutomationStatus.InvalidConfiguration, startedAt, error: executableError);

        var testOptions = options with
        {
            HookEnabled = true,
            AutoExportEnabled = false,
            TranscriptPolicy = PostMeetingTranscriptPolicy.MetadataOnly
        };

        var export = PostMeetingExportDiagnostic.NotRequested;
        var payload = BuildPayload(synthetic, PostMeetingCompletionEvent.ManualTest, testOptions.TranscriptPolicy, export);
        return await RunHookWithRetriesAsync(
            runId,
            startedAt,
            executablePath!,
            payload,
            [],
            suppressCapturedOutput: false,
            testOptions,
            export,
            pdfExport: null,
            cancellationToken).ConfigureAwait(false);
    }

    public static string? ValidateExecutable(string? selectedPath) =>
        ValidateExecutable(selectedPath, out _);

    private static string? ValidateExecutable(string? selectedPath, out string? fullPath)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(selectedPath))
            return "Select a hook executable.";
        if (!Path.IsPathRooted(selectedPath))
            return "The hook executable path must be rooted.";
        if (!string.Equals(Path.GetExtension(selectedPath), ".exe", StringComparison.OrdinalIgnoreCase))
            return "The selected hook must be an .exe file.";

        try
        {
            fullPath = Path.GetFullPath(selectedPath);
            if (!File.Exists(fullPath))
            {
                fullPath = null;
                return "The selected hook executable does not exist.";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return "The selected hook executable path is invalid.";
        }

        return null;
    }

    internal static PostMeetingHookPayload BuildPayload(
        MeetingItem meeting,
        PostMeetingCompletionEvent completionEvent,
        PostMeetingTranscriptPolicy policy,
        PostMeetingExportDiagnostic export)
    {
        var includeInline = policy == PostMeetingTranscriptPolicy.Inline;
        var includePath = policy == PostMeetingTranscriptPolicy.AutoExportPath;
        var exportPath = includePath && export.Completed ? export.DestinationPath : null;

        var trigger = completionEvent switch
        {
            PostMeetingCompletionEvent.RecordingCompleted => "recording-completed",
            PostMeetingCompletionEvent.RecoveryCompleted => "recovery-completed",
            PostMeetingCompletionEvent.ManualTest => "manual-test",
            _ => throw new ArgumentOutOfRangeException(nameof(completionEvent))
        };
        var eventName = completionEvent == PostMeetingCompletionEvent.ManualTest
            ? "meeting.hook.test"
            : "meeting.completed";

        return new PostMeetingHookPayload(
            ContractVersion,
            new PostMeetingHookEvent(eventName, trigger, DateTimeOffset.UtcNow),
            new PostMeetingHookMetadata(
                meeting.Id,
                meeting.Title,
                meeting.CreatedAt.Kind == DateTimeKind.Utc ? meeting.CreatedAt : meeting.CreatedAt.ToUniversalTime(),
                meeting.DurationMs,
                meeting.WordCount,
                meeting.TemplateName,
                meeting.FolderId,
                meeting.RecoveredFromInterruption),
            new PostMeetingHookTranscript(TranscriptPolicyCode(policy), exportPath, includeInline ? meeting.Transcript : null),
            new PostMeetingHookNotes(meeting.Summary ?? "", meeting.ManualNotes ?? ""),
            (meeting.HealthWarnings ?? []).ToArray(),
            new PostMeetingHookCompletion(meeting.SessionState.ToString()),
            new PostMeetingHookExport(
                export.Completed ? export.DestinationPath : null,
                export.Completed ? export.DestinationOwnership : AutomationDestinationOwnership.None));
    }

    private static string TranscriptPolicyCode(PostMeetingTranscriptPolicy policy) => policy switch
    {
        PostMeetingTranscriptPolicy.MetadataOnly => "metadata-only",
        PostMeetingTranscriptPolicy.Inline => "inline",
        PostMeetingTranscriptPolicy.AutoExportPath => "auto-export-path",
        _ => throw new ArgumentOutOfRangeException(nameof(policy))
    };

    internal static string SerializePayload(PostMeetingHookPayload payload) =>
        JsonSerializer.Serialize(payload, PayloadJsonOptions);

    private static IReadOnlyList<string> SensitiveValues(MeetingItem meeting) =>
        new[] { meeting.Transcript, meeting.Summary, meeting.ManualNotes }
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToArray();

    private static async Task<PostMeetingAutomationResult> RunHookWithRetriesAsync(
        Guid runId,
        DateTimeOffset startedAt,
        string executablePath,
        PostMeetingHookPayload payload,
        IReadOnlyList<string> sensitiveValues,
        bool suppressCapturedOutput,
        PostMeetingAutomationOptions options,
        PostMeetingExportDiagnostic export,
        PostMeetingExportDiagnostic? pdfExport,
        CancellationToken cancellationToken)
    {
        var attempts = Math.Clamp(options.RetryPolicy?.MaxAttempts ?? 1, 1, MaximumAttempts);
        var delay = options.RetryPolicy?.Delay ?? TimeSpan.Zero;
        delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay > MaximumRetryDelay ? MaximumRetryDelay : delay;
        var timeout = options.Timeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(30)
            : options.Timeout > MaximumTimeout ? MaximumTimeout : options.Timeout;
        var outputLimit = Math.Clamp(options.MaxCapturedOutputCharacters, 1, MaximumOutputCharacters);
        HookAttemptResult? last = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Result(runId, PostMeetingAutomationStatus.Cancelled, startedAt, attempt - 1, export: export, pdfExport: pdfExport);
            }

            last = await WindowsJobProcessRunner.RunAsync(
                executablePath,
                SerializePayload(payload),
                timeout,
                outputLimit,
                cancellationToken).ConfigureAwait(false);

            var redactedOut = AutomationOutputRedactor.Redact(last.StandardOutput, sensitiveValues);
            var redactedError = AutomationOutputRedactor.Redact(last.StandardError, sensitiveValues);
            var redactedMessage = AutomationOutputRedactor.Redact(last.Error, sensitiveValues);
            if (suppressCapturedOutput)
            {
                redactedOut = string.IsNullOrEmpty(last.StandardOutput) ? "" : "[REDACTED CONTENT OUTPUT]";
                redactedError = string.IsNullOrEmpty(last.StandardError) ? "" : "[REDACTED CONTENT OUTPUT]";
            }
            var outputWasClippedAfterRedaction = redactedOut.Length > outputLimit;
            var errorWasClippedAfterRedaction = redactedError.Length > outputLimit;
            if (outputWasClippedAfterRedaction) redactedOut = redactedOut[..outputLimit];
            if (errorWasClippedAfterRedaction) redactedError = redactedError[..outputLimit];
            last = last with
            {
                StandardOutput = redactedOut,
                StandardError = redactedError,
                StandardOutputTruncated = last.StandardOutputTruncated || outputWasClippedAfterRedaction,
                StandardErrorTruncated = last.StandardErrorTruncated || errorWasClippedAfterRedaction,
                Error = redactedMessage
            };

            if (last.Status == PostMeetingAutomationStatus.Succeeded)
            {
                var exportFailed = (export.Requested && !export.Completed) ||
                                   pdfExport is { Requested: true, Completed: false };
                var overallStatus = exportFailed
                    ? PostMeetingAutomationStatus.Failed
                    : last.Status;
                return Result(runId, overallStatus, startedAt, attempt, last.ExitCode, redactedOut, redactedError,
                    last.StandardOutputTruncated, last.StandardErrorTruncated,
                    CombineErrors(redactedMessage, CombineErrors(export.Error, pdfExport?.Error)), export,
                    pdfExport: pdfExport);
            }

            if (last.Status is PostMeetingAutomationStatus.Cancelled or PostMeetingAutomationStatus.TimedOut || attempt == attempts)
            {
                return Result(runId, last.Status, startedAt, attempt, last.ExitCode, redactedOut, redactedError,
                    last.StandardOutputTruncated, last.StandardErrorTruncated,
                    CombineErrors(redactedMessage, CombineErrors(export.Error, pdfExport?.Error)), export,
                    pdfExport: pdfExport);
            }

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return Result(runId, PostMeetingAutomationStatus.Cancelled, startedAt, attempt, last.ExitCode,
                        redactedOut, redactedError, last.StandardOutputTruncated, last.StandardErrorTruncated,
                        CombineErrors(redactedMessage, CombineErrors(export.Error, pdfExport?.Error)), export,
                        pdfExport: pdfExport);
                }
            }
        }

        throw new InvalidOperationException("The bounded retry loop did not produce a result.");
    }

    private static PostMeetingAutomationResult Result(
        Guid runId,
        PostMeetingAutomationStatus status,
        DateTimeOffset startedAt,
        int attempts = 0,
        int? exitCode = null,
        string standardOutput = "",
        string standardError = "",
        bool standardOutputTruncated = false,
        bool standardErrorTruncated = false,
        string? error = null,
        PostMeetingExportDiagnostic? export = null,
        PostMeetingExportDiagnostic? pdfExport = null) =>
        new(
            runId,
            status,
            startedAt,
            DateTimeOffset.UtcNow,
            attempts,
            exitCode,
            standardOutput,
            standardError,
            standardOutputTruncated,
            standardErrorTruncated,
            error,
            export ?? PostMeetingExportDiagnostic.NotRequested)
        {
            PdfExport = pdfExport
        };

    private static string? CombineErrors(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first)) return string.IsNullOrWhiteSpace(second) ? null : second;
        if (string.IsNullOrWhiteSpace(second)) return first;
        return $"{first} {second}";
    }
}

public sealed record PostMeetingHookPayload(
    int SchemaVersion,
    PostMeetingHookEvent Event,
    PostMeetingHookMetadata Meeting,
    PostMeetingHookTranscript Transcript,
    PostMeetingHookNotes Notes,
    IReadOnlyList<string> Warnings,
    PostMeetingHookCompletion Completion,
    PostMeetingHookExport Export);

public sealed record PostMeetingHookEvent(string Name, string Trigger, DateTimeOffset OccurredAtUtc);

public sealed record PostMeetingHookMetadata(
    string Id,
    string Title,
    DateTime CreatedAtUtc,
    int DurationMs,
    int WordCount,
    string Template,
    string? FolderId,
    bool RecoveredFromInterruption);

public sealed record PostMeetingHookTranscript(string Policy, string? Path, string? Data);
public sealed record PostMeetingHookNotes(string Generated, string Manual);
public sealed record PostMeetingHookCompletion(string State);
public sealed record PostMeetingHookExport(string? Path, AutomationDestinationOwnership Ownership);

internal enum PostMeetingExportFormat
{
    Markdown,
    Pdf
}

internal static class PostMeetingMarkdownAutoExporter
{
    private const int ManifestVersion = 1;
    private const string ControlOwner = "Muesli.PostMeetingAutomation";
    private const string OwnershipMarkerName = ".owner.json";
    private static readonly TimeSpan ClaimWaitLimit = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions ControlJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly AsyncLocal<Func<string>?> TemporaryTokenFactory = new();
    internal static Func<string>? TemporaryTokenFactoryForTests
    {
        get => TemporaryTokenFactory.Value;
        set => TemporaryTokenFactory.Value = value;
    }

    public static Task<PostMeetingExportDiagnostic> ExportAsync(
        MeetingItem meeting,
        PostMeetingCompletionEvent completionEvent,
        string? selectedDirectory,
        MeetingExportMode mode,
        int requestedAttempts,
        PostMeetingExportDiagnostic? previousExport,
        CancellationToken cancellationToken) =>
        ExportCoreAsync(
            meeting, completionEvent, selectedDirectory, mode, requestedAttempts, previousExport,
            PostMeetingExportFormat.Markdown, cancellationToken);

    /// <summary>
    /// Publishes an automatic PDF using the same atomic, collision-safe, manifest-backed machinery
    /// as Markdown. Fails closed without creating anything when the EXP-01 license gate is closed.
    /// </summary>
    public static Task<PostMeetingExportDiagnostic> ExportPdfAsync(
        MeetingItem meeting,
        PostMeetingCompletionEvent completionEvent,
        string? selectedDirectory,
        MeetingExportMode mode,
        int requestedAttempts,
        PostMeetingExportDiagnostic? previousExport,
        CancellationToken cancellationToken) =>
        ExportCoreAsync(
            meeting, completionEvent, selectedDirectory, mode, requestedAttempts, previousExport,
            PostMeetingExportFormat.Pdf, cancellationToken);

    private static async Task<PostMeetingExportDiagnostic> ExportCoreAsync(
        MeetingItem meeting,
        PostMeetingCompletionEvent completionEvent,
        string? selectedDirectory,
        MeetingExportMode mode,
        int requestedAttempts,
        PostMeetingExportDiagnostic? previousExport,
        PostMeetingExportFormat format,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(selectedDirectory) || !Path.IsPathRooted(selectedDirectory))
            return Failure("Select a rooted auto-export directory.", 0);

        if (format == PostMeetingExportFormat.Pdf && !MeetingDocumentWriter.PdfExportApproved)
        {
            return Failure(
                "PDF export is disabled until QuestPDF Community-license eligibility is approved (EXP-01).",
                0);
        }

        string directory;
        try
        {
            directory = Path.GetFullPath(selectedDirectory);
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return Failure("The selected auto-export directory could not be created.", 0);
        }

        var markdown = MeetingExportFormatter.BuildMarkdown(meeting, mode, meeting.SpeakerAliases);
        byte[] contentBytes;
        string extension;
        if (format == PostMeetingExportFormat.Pdf)
        {
            try
            {
                contentBytes = MeetingDocumentWriter.GeneratePdfBytes(
                    markdown, new DateTimeOffset(meeting.CreatedAt));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or NotSupportedException)
            {
                return Failure("The automatic PDF could not be rendered.", 0);
            }

            extension = ".pdf";
        }
        else
        {
            contentBytes = new UTF8Encoding(false).GetBytes(markdown);
            extension = ".md";
        }

        var contentHash = Convert.ToHexString(SHA256.HashData(contentBytes)).ToLowerInvariant();
        var paths = GetControlPaths(directory, meeting.Id, completionEvent, mode, format);
        var controlError = EnsureOwnedControlDirectory(directory, paths.ControlDirectory);
        if (controlError is not null) return Failure(controlError, 0);

        var existing = ReadManifest(paths.ManifestPath, directory, contentHash);
        if (existing is not null) return existing;

        var attempts = Math.Clamp(requestedAttempts, 1, 3);
        string? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
                return Failure("Auto-export was cancelled.", attempt - 1);

            var ownerToken = Guid.NewGuid().ToString("N");
            var claim = new ExportClaim(
                ManifestVersion,
                ownerToken,
                Environment.ProcessId,
                Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                DateTimeOffset.UtcNow,
                null,
                contentHash,
                ControlOwner);

            ClaimAcquisition acquired;
            try
            {
                acquired = await AcquireClaimAsync(paths, directory, claim, contentHash, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Failure("Auto-export was cancelled.", attempt - 1);
            }
            if (acquired.Reused is not null) return acquired.Reused;
            if (!acquired.Acquired)
            {
                lastError = acquired.Error;
                continue;
            }

            string? exportTemporaryPath = null;
            var exportTemporaryOwned = false;
            try
            {
                var manifestAfterClaim = PrepareManifestForPublication(paths.ManifestPath, directory, contentHash);
                if (manifestAfterClaim is not null)
                {
                    DeleteClaimIfOwned(paths.ClaimPath, ownerToken);
                    return manifestAfterClaim;
                }

                var priorPath = ValidPreviousPath(previousExport, directory, contentHash);
                if (priorPath is not null)
                {
                    var priorManifest = new ExportManifest(
                        ManifestVersion,
                        Path.GetFileName(priorPath),
                        contentHash,
                        DateTimeOffset.UtcNow);
                    PublishManifest(paths.ManifestPath, priorManifest);
                    return Success(priorPath, 0);
                }

                exportTemporaryPath = Path.Combine(directory, $".muesli-{paths.Key}.{NextTemporaryToken()}.tmp");
                WriteThroughNewFile(exportTemporaryPath, contentBytes, out exportTemporaryOwned);

                var suggested = Path.ChangeExtension(MeetingExportFormatter.SuggestFilename(meeting, mode), extension);
                var stem = Path.GetFileNameWithoutExtension(suggested);
                for (var suffix = 1; suffix <= 10_000; suffix++)
                {
                    var filename = suffix == 1 ? $"{stem}{extension}" : $"{stem} ({suffix}){extension}";
                    claim = claim with { FileName = filename };
                    ReplaceOwnedClaim(paths.ClaimPath, claim);
                    var destination = Path.Combine(directory, filename);
                    try
                    {
                        File.Move(exportTemporaryPath!, destination, overwrite: false);
                    }
                    catch (IOException) when (File.Exists(destination))
                    {
                        // Never overwrite a user-owned collision. The next candidate is recorded in
                        // the service-owned claim before it can be atomically published.
                        continue;
                    }

                    exportTemporaryOwned = false;
                    exportTemporaryPath = null;
                    PublishManifest(paths.ManifestPath, new ExportManifest(
                        ManifestVersion,
                        filename,
                        contentHash,
                        DateTimeOffset.UtcNow));
                    return Success(destination, attempt);
                }

                lastError = "No collision-free auto-export filename was available.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
            {
                lastError = $"Auto-export failed ({ex.GetType().Name}).";
            }
            finally
            {
                if (exportTemporaryOwned) DeleteServiceFile(exportTemporaryPath);
                DeleteClaimIfOwned(paths.ClaimPath, ownerToken);
            }
        }

        return Failure(lastError ?? "Auto-export failed.", attempts);
    }

    internal static ExportControlPaths GetControlPaths(
        string directory,
        string meetingId,
        PostMeetingCompletionEvent completionEvent,
        MeetingExportMode mode,
        PostMeetingExportFormat format = PostMeetingExportFormat.Markdown)
    {
        var keyMaterial = $"{meetingId}\n{completionEvent}\n{mode}\n{format}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial)))
            .ToLowerInvariant()[..32];
        var controlDirectory = Path.Combine(directory, ".muesli-automation");
        return new ExportControlPaths(
            key,
            controlDirectory,
            Path.Combine(controlDirectory, $"{key}.claim.json"),
            Path.Combine(controlDirectory, $"{key}.manifest.json"));
    }

    private static string? EnsureOwnedControlDirectory(string parentDirectory, string controlDirectory)
    {
        if (Directory.Exists(controlDirectory))
            return ValidateControlMarker(controlDirectory)
                ? null
                : "The existing .muesli-automation directory is not owned by Muesli and was left untouched.";
        if (File.Exists(controlDirectory))
            return "The .muesli-automation path is not a Muesli-owned directory and was left untouched.";

        var token = NextTemporaryToken();
        var preparedDirectory = Path.Combine(parentDirectory, $".muesli-automation.{token}.tmp");
        if (!CreateDirectoryNative(preparedDirectory, IntPtr.Zero))
        {
            return "A prepared auto-export control path already exists or could not be created; it was left untouched.";
        }

        var preparedOwned = true;
        try
        {
            var markerPath = Path.Combine(preparedDirectory, OwnershipMarkerName);
            WriteThroughNewFile(
                markerPath,
                JsonSerializer.SerializeToUtf8Bytes(
                    new ControlOwnership(ManifestVersion, ControlOwner, token),
                    ControlJsonOptions),
                out _);

            try
            {
                Directory.Move(preparedDirectory, controlDirectory);
                preparedOwned = false;
            }
            catch (IOException) when (Directory.Exists(controlDirectory))
            {
                if (!ValidateControlMarker(controlDirectory))
                    return "The existing .muesli-automation directory is not owned by Muesli and was left untouched.";
                return null;
            }

            try
            {
                File.SetAttributes(controlDirectory, File.GetAttributes(controlDirectory) | FileAttributes.Hidden);
            }
            catch
            {
                // Ownership is the marker, not an optional filesystem presentation attribute.
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
        {
            return $"The Muesli auto-export control directory could not be prepared ({ex.GetType().Name}).";
        }
        finally
        {
            if (preparedOwned)
            {
                try { Directory.Delete(preparedDirectory, recursive: true); } catch { }
            }
        }
    }

    private static bool ValidateControlMarker(string controlDirectory)
    {
        try
        {
            var marker = JsonSerializer.Deserialize<ControlOwnership>(
                File.ReadAllText(Path.Combine(controlDirectory, OwnershipMarkerName)),
                ControlJsonOptions);
            return marker?.SchemaVersion == ManifestVersion
                && string.Equals(marker.Owner, ControlOwner, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(marker.InstanceToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static async Task<ClaimAcquisition> AcquireClaimAsync(
        ExportControlPaths paths,
        string directory,
        ExportClaim claim,
        string expectedContentHash,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + ClaimWaitLimit;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var manifest = ReadManifest(paths.ManifestPath, directory, expectedContentHash);
            if (manifest is not null) return new ClaimAcquisition(false, manifest, null);

            var claimTemporaryPath = $"{paths.ClaimPath}.{NextTemporaryToken()}.tmp";
            var claimTemporaryOwned = false;
            try
            {
                WriteThroughNewFile(
                    claimTemporaryPath,
                    JsonSerializer.SerializeToUtf8Bytes(claim, ControlJsonOptions),
                    out claimTemporaryOwned);
                File.Move(claimTemporaryPath, paths.ClaimPath, overwrite: false);
                claimTemporaryOwned = false;

                // The manifest can be published after the initial read but before this claim
                // becomes visible (the previous owner publishes it before releasing its claim).
                // Recheck while we own the claim so a waiter cannot create a second Markdown file
                // after the winning export has already committed.
                var publishedAfterClaim = ReadManifest(paths.ManifestPath, directory, expectedContentHash);
                if (publishedAfterClaim is not null)
                {
                    DeleteClaimIfOwned(paths.ClaimPath, claim.OwnerToken);
                    return new ClaimAcquisition(false, publishedAfterClaim, null);
                }

                return new ClaimAcquisition(true, null, null);
            }
            catch (IOException) when (File.Exists(paths.ClaimPath))
            {
                var existingClaim = ReadClaim(paths.ClaimPath);
                if (existingClaim is null || !string.Equals(existingClaim.Owner, ControlOwner, StringComparison.Ordinal))
                {
                    return new ClaimAcquisition(false, null, "The auto-export claim is not owned by Muesli.");
                }

                if (!IsClaimOwnerAlive(existingClaim))
                {
                    var recovered = RecoverPublishedClaim(existingClaim, paths, directory, expectedContentHash);
                    if (recovered is not null) return new ClaimAcquisition(false, recovered, null);
                    DeleteServiceFile(paths.ClaimPath);
                    continue;
                }

                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return new ClaimAcquisition(false, null, $"Auto-export claim failed ({ex.GetType().Name}).");
            }
            finally
            {
                if (claimTemporaryOwned) DeleteServiceFile(claimTemporaryPath);
            }
        }

        return new ClaimAcquisition(false, null, "Another auto-export still owns the meeting claim.");
    }

    private static PostMeetingExportDiagnostic? RecoverPublishedClaim(
        ExportClaim? claim,
        ExportControlPaths paths,
        string directory,
        string expectedContentHash)
    {
        if (claim is null
            || !string.Equals(claim.Owner, ControlOwner, StringComparison.Ordinal)
            || !TryGetManifestDestination(directory, claim.FileName, out var candidate)
            || !HashesEqual(claim.ContentSha256, expectedContentHash)) return null;
        if (!File.Exists(candidate)) return null;
        try
        {
            using var stream = File.OpenRead(candidate);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actualHash, expectedContentHash, StringComparison.Ordinal)) return null;

            var manifestAfterRecovery = PrepareManifestForPublication(
                paths.ManifestPath,
                directory,
                expectedContentHash);
            if (manifestAfterRecovery is not null)
            {
                if (manifestAfterRecovery.Completed) DeleteServiceFile(paths.ClaimPath);
                return manifestAfterRecovery;
            }

            PublishManifest(paths.ManifestPath, new ExportManifest(
                ManifestVersion,
                claim.FileName!,
                expectedContentHash,
                DateTimeOffset.UtcNow));
            DeleteServiceFile(paths.ClaimPath);
            return Success(candidate, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static PostMeetingExportDiagnostic? ReadManifest(
        string manifestPath,
        string directory,
        string expectedContentHash)
    {
        var inspection = InspectManifest(manifestPath, directory);
        return inspection.State switch
        {
            ManifestInspectionState.Missing or ManifestInspectionState.Stale => null,
            ManifestInspectionState.Invalid => Failure(inspection.Error!, 0),
            ManifestInspectionState.Valid when !HashesEqual(
                inspection.Manifest!.ContentSha256,
                expectedContentHash) => null,
            ManifestInspectionState.Valid => Success(inspection.DestinationPath!, 0),
            _ => Failure("The auto-export manifest is invalid and was left untouched.", 0)
        };
    }

    private static PostMeetingExportDiagnostic? PrepareManifestForPublication(
        string manifestPath,
        string directory,
        string expectedContentHash)
    {
        var inspection = InspectManifest(manifestPath, directory);
        switch (inspection.State)
        {
            case ManifestInspectionState.Missing:
                return null;
            case ManifestInspectionState.Invalid:
                return Failure(inspection.Error!, 0);
            case ManifestInspectionState.Valid when HashesEqual(
                inspection.Manifest!.ContentSha256,
                expectedContentHash):
                return Success(inspection.DestinationPath!, 0);
            case ManifestInspectionState.Valid:
            case ManifestInspectionState.Stale:
                try
                {
                    if (File.Exists(manifestPath)) File.Delete(manifestPath);
                    return null;
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    return Failure(
                        $"The stale auto-export manifest could not be invalidated ({ex.GetType().Name}) and was left untouched.",
                        0);
                }
            default:
                return Failure("The auto-export manifest is invalid and was left untouched.", 0);
        }
    }

    private static ManifestInspection InspectManifest(string manifestPath, string directory)
    {
        if (!File.Exists(manifestPath))
            return new(ManifestInspectionState.Missing, null, null, null);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<ExportManifest>(
                    File.ReadAllText(manifestPath),
                    ControlJsonOptions);
                if (manifest?.SchemaVersion != ManifestVersion
                    || !TryGetManifestDestination(directory, manifest.FileName, out var destination)
                    || !IsSha256Hash(manifest.ContentSha256))
                {
                    return InvalidManifestInspection();
                }

                if (!File.Exists(destination))
                    return new(ManifestInspectionState.Stale, manifest, destination, null);

                using var stream = File.OpenRead(destination);
                var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!HashesEqual(actualHash, manifest.ContentSha256))
                    return new(ManifestInspectionState.Stale, manifest, destination, null);

                return new(ManifestInspectionState.Valid, manifest, destination, null);
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
            {
                return new(
                    ManifestInspectionState.Invalid,
                    null,
                    null,
                    $"The auto-export manifest could not be read ({ex.GetType().Name}) and was left untouched.");
            }
        }
    }

    private static ExportClaim? ReadClaim(string claimPath)
    {
        try
        {
            return JsonSerializer.Deserialize<ExportClaim>(File.ReadAllText(claimPath), ControlJsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool IsClaimOwnerAlive(ExportClaim claim)
    {
        try
        {
            using var process = Process.GetProcessById(claim.ProcessId);
            if (claim.ProcessId == Environment.ProcessId
                && claim.ClaimedAtUtc >= process.StartTime.ToUniversalTime())
                return true;
            return Math.Abs((process.StartTime.ToUniversalTime() - claim.ProcessStartedAtUtc.UtcDateTime).TotalSeconds) < 1;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static string? ValidPreviousPath(
        PostMeetingExportDiagnostic? previous,
        string directory,
        string expectedContentHash)
    {
        if (previous?.Completed != true || string.IsNullOrWhiteSpace(previous.DestinationPath)) return null;
        try
        {
            var fullPath = Path.GetFullPath(previous.DestinationPath);
            if (!string.Equals(Path.GetDirectoryName(fullPath), directory, StringComparison.OrdinalIgnoreCase)) return null;
            if (!File.Exists(fullPath)) return null;
            using var stream = File.OpenRead(fullPath);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return string.Equals(actualHash, expectedContentHash, StringComparison.Ordinal) ? fullPath : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteThroughNewFile(string path, byte[] content, out bool createdByThisRun)
    {
        createdByThisRun = false;
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        createdByThisRun = true;
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    private static void ReplaceOwnedClaim(string claimPath, ExportClaim claim)
    {
        var current = ReadClaim(claimPath);
        if (current is null
            || !string.Equals(current.Owner, ControlOwner, StringComparison.Ordinal)
            || !string.Equals(current.OwnerToken, claim.OwnerToken, StringComparison.Ordinal))
        {
            throw new IOException("The auto-export claim ownership changed.");
        }

        var temporary = $"{claimPath}.{NextTemporaryToken()}.tmp";
        var temporaryOwned = false;
        try
        {
            WriteThroughNewFile(
                temporary,
                JsonSerializer.SerializeToUtf8Bytes(claim, ControlJsonOptions),
                out temporaryOwned);
            File.Move(temporary, claimPath, overwrite: true);
            temporaryOwned = false;
        }
        finally
        {
            if (temporaryOwned) DeleteServiceFile(temporary);
        }
    }

    private static void PublishManifest(string manifestPath, ExportManifest manifest)
    {
        if (File.Exists(manifestPath))
            throw new IOException("The auto-export manifest path already exists and was left untouched.");
        var temporary = $"{manifestPath}.{NextTemporaryToken()}.tmp";
        var temporaryOwned = false;
        try
        {
            WriteThroughNewFile(
                temporary,
                JsonSerializer.SerializeToUtf8Bytes(manifest, ControlJsonOptions),
                out temporaryOwned);
            File.Move(temporary, manifestPath, overwrite: false);
            temporaryOwned = false;
        }
        finally
        {
            if (temporaryOwned) DeleteServiceFile(temporary);
        }
    }

    private static void DeleteClaimIfOwned(string claimPath, string ownerToken)
    {
        var current = ReadClaim(claimPath);
        if (current is not null
            && string.Equals(current.Owner, ControlOwner, StringComparison.Ordinal)
            && string.Equals(current.OwnerToken, ownerToken, StringComparison.Ordinal))
            DeleteServiceFile(claimPath);
    }

    private static void DeleteServiceFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static bool SafeFileName(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return false;
        try
        {
            return !string.Equals(filename, ".", StringComparison.Ordinal)
                && !string.Equals(filename, "..", StringComparison.Ordinal)
                && string.Equals(filename, Path.GetFileName(filename), StringComparison.Ordinal)
                && filename.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryGetManifestDestination(
        string directory,
        string? filename,
        out string destination)
    {
        destination = string.Empty;
        if (!SafeFileName(filename)) return false;

        try
        {
            var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            var candidate = Path.GetFullPath(Path.Combine(normalizedDirectory, filename!));
            var candidateDirectory = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(candidate)!);
            if (!string.Equals(candidateDirectory, normalizedDirectory, StringComparison.OrdinalIgnoreCase))
                return false;

            destination = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsSha256Hash(string? value)
    {
        if (value is null || value.Length != 64) return false;
        foreach (var character in value)
        {
            var isDecimal = character is >= '0' and <= '9';
            var isLowerHex = character is >= 'a' and <= 'f';
            var isUpperHex = character is >= 'A' and <= 'F';
            if (!isDecimal && !isLowerHex && !isUpperHex) return false;
        }

        return true;
    }

    private static bool HashesEqual(string? left, string? right) =>
        IsSha256Hash(left)
        && IsSha256Hash(right)
        && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static ManifestInspection InvalidManifestInspection() =>
        new(
            ManifestInspectionState.Invalid,
            null,
            null,
            "The auto-export manifest is invalid and was left untouched.");

    private static string NextTemporaryToken() =>
        TemporaryTokenFactoryForTests?.Invoke() ?? Guid.NewGuid().ToString("N");

    private static PostMeetingExportDiagnostic Success(string path, int attempts) =>
        new(true, true, path, AutomationDestinationOwnership.UserSelectedDestination, null, attempts);

    private static PostMeetingExportDiagnostic Failure(string error, int attempts) =>
        new(true, false, null, AutomationDestinationOwnership.None, error, attempts);

    private enum ManifestInspectionState
    {
        Missing,
        Valid,
        Stale,
        Invalid
    }

    private sealed record ManifestInspection(
        ManifestInspectionState State,
        ExportManifest? Manifest,
        string? DestinationPath,
        string? Error);

    internal sealed record ExportControlPaths(string Key, string ControlDirectory, string ClaimPath, string ManifestPath);
    internal sealed record ExportClaim(
        int SchemaVersion,
        string OwnerToken,
        int ProcessId,
        DateTimeOffset ProcessStartedAtUtc,
        DateTimeOffset ClaimedAtUtc,
        string? FileName,
        string ContentSha256,
        string Owner);
    private sealed record ControlOwnership(int SchemaVersion, string Owner, string InstanceToken);
    private sealed record ExportManifest(int SchemaVersion, string FileName, string ContentSha256, DateTimeOffset PublishedAtUtc);
    private sealed record ClaimAcquisition(bool Acquired, PostMeetingExportDiagnostic? Reused, string? Error);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryNative(string path, IntPtr securityAttributes);
}

internal sealed record HookAttemptResult(
    PostMeetingAutomationStatus Status,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool StandardOutputTruncated,
    bool StandardErrorTruncated,
    string? Error);

internal static partial class AutomationOutputRedactor
{
    [GeneratedRegex(@"(?i)\b(authorization\s*:\s*(?:bearer\s+)?|api[-_]?key\s*[:=]\s*|access[-_]?token\s*[:=]\s*|refresh[-_]?token\s*[:=]\s*|password\s*[:=]\s*)[^\s,;\""']+")]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"\b(?:sk|pk|rk)-[A-Za-z0-9_-]{8,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex KeyPattern();

    public static string Redact(string? value, IReadOnlyList<string> sensitiveValues)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        var result = value;
        foreach (var sensitive in sensitiveValues)
        {
            if (string.IsNullOrEmpty(sensitive)) continue;
            result = result.Replace(sensitive, "[REDACTED CONTENT]", StringComparison.Ordinal);
            var jsonEscaped = JsonEncodedText.Encode(sensitive).ToString();
            if (!string.Equals(jsonEscaped, sensitive, StringComparison.Ordinal))
                result = result.Replace(jsonEscaped, "[REDACTED CONTENT]", StringComparison.Ordinal);
        }

        result = SecretPattern().Replace(result, match => $"{match.Groups[1].Value}[REDACTED SECRET]");
        return KeyPattern().Replace(result, "[REDACTED SECRET]");
    }
}

internal static class WindowsJobProcessRunner
{
    public static async Task<HookAttemptResult> RunAsync(
        string executablePath,
        string jsonPayload,
        TimeSpan timeout,
        int outputLimit,
        CancellationToken cancellationToken)
    {
        WindowsKillOnCloseJob? job = null;
        SuspendedHookProcess? launched = null;
        try
        {
            job = WindowsKillOnCloseJob.Create();
            launched = SuspendedHookProcess.Start(executablePath, job);
            var process = launched.Process;

            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            var stdoutTask = ReadBoundedAsync(launched.StandardOutput, outputLimit);
            var stderrTask = ReadBoundedAsync(launched.StandardError, outputLimit);
            var stdinTask = IgnorePipeWriteFailureAsync(
                WritePayloadAsync(launched.StandardInput, jsonPayload, linked.Token));

            PostMeetingAutomationStatus terminalStatus;
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                terminalStatus = process.ExitCode == 0
                    ? PostMeetingAutomationStatus.Succeeded
                    : PostMeetingAutomationStatus.Failed;

                // A successful hook is a bounded foreground operation, not a daemon launcher. Closing
                // the job here terminates descendants that inherited redirected pipe handles before we
                // wait for EOF, so a parent that exits cannot make this attempt hang forever.
                job.Dispose();
                job = null;
            }
            catch (OperationCanceledException)
            {
                terminalStatus = cancellationToken.IsCancellationRequested
                    ? PostMeetingAutomationStatus.Cancelled
                    : PostMeetingAutomationStatus.TimedOut;
                TryTerminate(process, job);
                job = null;
                await WaitAfterTerminationAsync(process).ConfigureAwait(false);
            }

            try
            {
                await Task.WhenAll(stdinTask, stdoutTask, stderrTask).WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                terminalStatus = cancellationToken.IsCancellationRequested
                    ? PostMeetingAutomationStatus.Cancelled
                    : PostMeetingAutomationStatus.TimedOut;
                TryTerminate(process, job);
                job = null;
                launched.CloseRedirectedStreams();
            }

            var stdout = stdoutTask.IsCompletedSuccessfully
                ? stdoutTask.Result
                : new BoundedText("", true);
            var stderr = stderrTask.IsCompletedSuccessfully
                ? stderrTask.Result
                : new BoundedText("", true);
            int? exitCode = process.HasExited ? process.ExitCode : null;
            var error = terminalStatus switch
            {
                PostMeetingAutomationStatus.Failed => $"Hook exited with code {exitCode?.ToString() ?? "unknown"}.",
                PostMeetingAutomationStatus.TimedOut => "Hook timed out and its job was terminated.",
                PostMeetingAutomationStatus.Cancelled => "Hook was cancelled and its job was terminated.",
                _ => null
            };

            return new HookAttemptResult(
                terminalStatus,
                exitCode,
                stdout.Text,
                stderr.Text,
                stdout.Truncated,
                stderr.Truncated,
                error);
        }
        catch (Exception ex) when (ex is Win32Exception
                                      or IOException
                                      or InvalidOperationException
                                      or ArgumentException
                                      or UnauthorizedAccessException
                                      or NotSupportedException)
        {
            if (launched is not null) TryTerminate(launched.Process, job);
            return Failure($"Hook launch failed ({ex.GetType().Name}).");
        }
        finally
        {
            job?.Dispose();
            launched?.Dispose();
        }
    }

    private static HookAttemptResult Failure(string error) =>
        new(PostMeetingAutomationStatus.Failed, null, "", "", false, false, error);

    private static async Task WritePayloadAsync(StreamWriter input, string jsonPayload, CancellationToken token)
    {
        try
        {
            await input.WriteAsync(jsonPayload.AsMemory(), token).ConfigureAwait(false);
            await input.FlushAsync(token).ConfigureAwait(false);
        }
        finally
        {
            input.Close();
        }
    }

    private static async Task IgnorePipeWriteFailureAsync(Task writeTask)
    {
        try
        {
            await writeTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private static async Task<BoundedText> ReadBoundedAsync(StreamReader reader, int limit)
    {
        var builder = new StringBuilder(Math.Min(limit, 4096));
        var buffer = new char[4096];
        var truncated = false;
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0) break;
                var remaining = limit - builder.Length;
                if (remaining > 0) builder.Append(buffer, 0, Math.Min(remaining, read));
                if (read > remaining) truncated = true;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            truncated = true;
        }

        return new BoundedText(builder.ToString(), truncated);
    }

    private static void TryTerminate(Process process, WindowsKillOnCloseJob? job)
    {
        try { job?.Dispose(); } catch { }
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static async Task WaitAfterTerminationAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch { }
    }

    private sealed record BoundedText(string Text, bool Truncated);

    /// <summary>
    /// Creates the process with its only thread suspended. No executable code can run until the raw
    /// process handle has been assigned to the kill-on-close job and ResumeThread succeeds.
    /// </summary>
    private sealed class SuspendedHookProcess : IDisposable
    {
        private SuspendedHookProcess(
            Process process,
            StreamWriter standardInput,
            StreamReader standardOutput,
            StreamReader standardError)
        {
            Process = process;
            StandardInput = standardInput;
            StandardOutput = standardOutput;
            StandardError = standardError;
        }

        public Process Process { get; }
        public StreamWriter StandardInput { get; }
        public StreamReader StandardOutput { get; }
        public StreamReader StandardError { get; }

        public static SuspendedHookProcess Start(string executablePath, WindowsKillOnCloseJob job)
        {
            SafeFileHandle? childStdinRead = null;
            SafeFileHandle? parentStdinWrite = null;
            SafeFileHandle? parentStdoutRead = null;
            SafeFileHandle? childStdoutWrite = null;
            SafeFileHandle? parentStderrRead = null;
            SafeFileHandle? childStderrWrite = null;
            SafeKernelHandle? processHandle = null;
            SafeKernelHandle? threadHandle = null;
            Process? process = null;
            StreamWriter? input = null;
            StreamReader? output = null;
            StreamReader? error = null;
            var created = false;
            IntPtr attributeList = IntPtr.Zero;
            IntPtr inheritedHandleList = IntPtr.Zero;

            try
            {
                CreatePipePair(out childStdinRead, out parentStdinWrite, parentHandleIsRead: false);
                CreatePipePair(out parentStdoutRead, out childStdoutWrite, parentHandleIsRead: true);
                CreatePipePair(out parentStderrRead, out childStderrWrite, parentHandleIsRead: true);

                nuint attributeListSize = 0;
                _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
                if (attributeListSize == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                attributeList = Marshal.AllocHGlobal(checked((int)attributeListSize));
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                inheritedHandleList = Marshal.AllocHGlobal(IntPtr.Size * 3);
                Marshal.WriteIntPtr(inheritedHandleList, 0, childStdinRead.DangerousGetHandle());
                Marshal.WriteIntPtr(inheritedHandleList, IntPtr.Size, childStdoutWrite.DangerousGetHandle());
                Marshal.WriteIntPtr(inheritedHandleList, IntPtr.Size * 2, childStderrWrite.DangerousGetHandle());
                if (!UpdateProcThreadAttribute(
                        attributeList,
                        0,
                        ProcThreadAttributeHandleList,
                        inheritedHandleList,
                        (nuint)(IntPtr.Size * 3),
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var startup = new StartupInfoEx
                {
                    StartupInfo = new StartupInfo
                    {
                        Size = Marshal.SizeOf<StartupInfoEx>(),
                        Flags = StartfUseStdHandles,
                        StandardInput = childStdinRead.DangerousGetHandle(),
                        StandardOutput = childStdoutWrite.DangerousGetHandle(),
                        StandardError = childStderrWrite.DangerousGetHandle()
                    },
                    AttributeList = attributeList
                };

                if (!CreateProcess(
                        executablePath,
                        null,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        inheritHandles: true,
                        CreateSuspended | CreateNoWindow | ExtendedStartupInfoPresent,
                        IntPtr.Zero,
                        Path.GetDirectoryName(executablePath),
                        ref startup,
                        out var processInformation))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                created = true;
                processHandle = new SafeKernelHandle(processInformation.Process, ownsHandle: true);
                threadHandle = new SafeKernelHandle(processInformation.Thread, ownsHandle: true);
                childStdinRead.Dispose();
                childStdinRead = null;
                childStdoutWrite.Dispose();
                childStdoutWrite = null;
                childStderrWrite.Dispose();
                childStderrWrite = null;

                job.Assign(processInformation.Process);
                process = Process.GetProcessById(unchecked((int)processInformation.ProcessId));

                input = new StreamWriter(
                    new FileStream(parentStdinWrite, FileAccess.Write, 4096, isAsync: false),
                    new UTF8Encoding(false));
                parentStdinWrite = null;
                output = new StreamReader(
                    new FileStream(parentStdoutRead, FileAccess.Read, 4096, isAsync: false),
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true);
                parentStdoutRead = null;
                error = new StreamReader(
                    new FileStream(parentStderrRead, FileAccess.Read, 4096, isAsync: false),
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true);
                parentStderrRead = null;

                if (ResumeThread(processInformation.Thread) == uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                var result = new SuspendedHookProcess(process, input, output, error);
                process = null;
                input = null;
                output = null;
                error = null;
                return result;
            }
            catch
            {
                if (created && processHandle is not null && !processHandle.IsInvalid)
                {
                    try { TerminateProcess(processHandle.DangerousGetHandle(), 1); } catch { }
                }
                throw;
            }
            finally
            {
                input?.Dispose();
                output?.Dispose();
                error?.Dispose();
                process?.Dispose();
                childStdinRead?.Dispose();
                parentStdinWrite?.Dispose();
                parentStdoutRead?.Dispose();
                childStdoutWrite?.Dispose();
                parentStderrRead?.Dispose();
                childStderrWrite?.Dispose();
                threadHandle?.Dispose();
                processHandle?.Dispose();
                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }
                if (inheritedHandleList != IntPtr.Zero) Marshal.FreeHGlobal(inheritedHandleList);
            }
        }

        public void CloseRedirectedStreams()
        {
            try { StandardInput.Close(); } catch { }
            try { StandardOutput.Close(); } catch { }
            try { StandardError.Close(); } catch { }
        }

        public void Dispose()
        {
            CloseRedirectedStreams();
            Process.Dispose();
        }

        private static void CreatePipePair(
            out SafeFileHandle parentOrChildRead,
            out SafeFileHandle childOrParentWrite,
            bool parentHandleIsRead)
        {
            var security = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                InheritHandle = true
            };
            if (!CreatePipe(out var read, out var write, ref security, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            parentOrChildRead = read;
            childOrParentWrite = write;
            var parentHandle = parentHandleIsRead ? read : write;
            if (!SetHandleInformation(parentHandle, HandleFlagInherit, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private const uint StartfUseStdHandles = 0x00000100;
        private const uint CreateSuspended = 0x00000004;
        private const uint CreateNoWindow = 0x08000000;
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint HandleFlagInherit = 0x00000001;
        private static readonly IntPtr ProcThreadAttributeHandleList = new(0x00020002);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string? Reserved;
            public string? Desktop;
            public string? Title;
            public uint X;
            public uint Y;
            public uint XSize;
            public uint YSize;
            public uint XCountChars;
            public uint YCountChars;
            public uint FillAttribute;
            public uint Flags;
            public ushort ShowWindow;
            public ushort Reserved2Count;
            public IntPtr Reserved2;
            public IntPtr StandardInput;
            public IntPtr StandardOutput;
            public IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public uint ProcessId;
            public uint ThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
        }

        private sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeKernelHandle(IntPtr handle, bool ownsHandle) : base(ownsHandle) => SetHandle(handle);
            protected override bool ReleaseHandle() => CloseHandle(handle);
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcess(
            string applicationName,
            string? commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string? currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeProcThreadAttributeList(
            IntPtr attributeList,
            int attributeCount,
            uint flags,
            ref nuint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr attributeList,
            uint flags,
            IntPtr attribute,
            IntPtr value,
            nuint size,
            IntPtr previousValue,
            IntPtr returnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreatePipe(
            out SafeFileHandle readPipe,
            out SafeFileHandle writePipe,
            ref SecurityAttributes pipeAttributes,
            uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}

internal sealed class WindowsKillOnCloseJob : IDisposable
{
    private readonly SafeJobHandle _handle;

    private WindowsKillOnCloseJob(SafeJobHandle handle) => _handle = handle;

    public static WindowsKillOnCloseJob Create()
    {
        var handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());

        var information = new NativeMethods.JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new NativeMethods.JobObjectBasicLimitInformation
            {
                LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose
            }
        };
        var length = Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(information, pointer, false);
            if (!NativeMethods.SetInformationJobObject(handle, 9, pointer, (uint)length))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch
        {
            handle.Dispose();
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        return new WindowsKillOnCloseJob(handle);
    }

    public void Assign(Process process)
        => Assign(process.Handle);

    public void Assign(IntPtr processHandle)
    {
        if (!NativeMethods.AssignProcessToJobObject(_handle, processHandle))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose() => _handle.Dispose();

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle() : base(true) { }
        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private static class NativeMethods
    {
        internal const uint JobObjectLimitKillOnJobClose = 0x00002000;

        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern SafeJobHandle CreateJobObject(IntPtr securityAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass, IntPtr information, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
