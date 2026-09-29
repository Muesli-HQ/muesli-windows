using NAudio.Wave;

namespace Muesli.Windows.Services;

internal sealed record MeetingFinalizationRequest(
    MeetingSessionJournal Journal,
    string Title,
    bool Recovered,
    DateTime StartedAt,
    IReadOnlyList<string> HealthWarnings,
    MeetingLiveTranscriptionResult? LiveResult,
    CancellationToken CancellationToken);

internal sealed record MeetingFinalizationOutcome(
    RecordedMeetingResult Result,
    MeetingSessionJournal Journal,
    MeetingSessionState TerminalState);

/// <summary>
/// Final ASR, timestamp normalization, diarization/aliasing, live-final reconciliation, and
/// health-warning cleanup. Does not delete working audio; the persistence boundary owns that.
/// </summary>
internal sealed class MeetingFinalizationPipeline
{
    internal const string SystemAudioMissingWarning = "System audio was not captured; remote speakers may be missing.";
    internal const string SystemTranscriptEmptyWarning = "System transcript was empty even though system audio was captured.";
    internal const string MicTranscriptEmptyWarning = "Mic transcript was empty even though mic audio was captured.";
    internal const string DiarizationFailedWarning = "Speaker diarization failed; transcript used fallback speaker labels.";
    internal const string DiarizationNoSegmentsWarning = "Speaker diarization returned no speaker segments; transcript used [System audio] fallback.";
    internal const string TranscriptMissingWarning = "Meeting saved with no transcript; audio capture or transcription may have failed.";

    private readonly NativeTranscriptionClient _transcriptionClient;
    private readonly NativeDiarizationClient _diarizationClient;
    private readonly MeetingPersistenceBoundary _persistence;
    private readonly AppLogService? _logService;

    public MeetingFinalizationPipeline(
        NativeTranscriptionClient transcriptionClient,
        MeetingPersistenceBoundary persistence,
        NativeDiarizationClient? diarizationClient = null,
        AppLogService? logService = null)
    {
        _transcriptionClient = transcriptionClient;
        _persistence = persistence;
        _diarizationClient = diarizationClient ?? new NativeDiarizationClient();
        _logService = logService;
    }

    public void DisposeDiarization() => _diarizationClient.Dispose();

    public async Task<MeetingFinalizationOutcome> FinalizeAsync(MeetingFinalizationRequest request)
    {
        var journal = request.Journal;
        request.CancellationToken.ThrowIfCancellationRequested();
        var tracks = _persistence.BuildFinalTracks(journal);
        if (tracks.MicrophonePath is null && tracks.SystemPath is null)
        {
            throw new InvalidOperationException("No valid meeting track remained after recovery.");
        }

        var healthWarnings = request.HealthWarnings.ToList();
        if (tracks.MicrophonePath is null)
        {
            healthWarnings.Add("Microphone audio was not captured; your voice may be missing.");
        }
        if (tracks.SystemPath is null)
        {
            healthWarnings.Add(SystemAudioMissingWarning);
        }

        Task<DiarizationResult>? diarizationTask = null;
        var diarizationObserved = false;
        try
        {
            if (tracks.SystemPath is not null && NativeDiarizationClient.ShouldOverlapWithAsr)
            {
                diarizationTask = _diarizationClient.DiarizeFileAsync(tracks.SystemPath);
            }

            request.CancellationToken.ThrowIfCancellationRequested();
            TranscriptionResult micTranscript;
            TranscriptionResult systemTranscript;
            if (request.LiveResult?.OwnershipMode == LiveTranscriptOwnershipMode.UnifiedLiveAndFinal)
            {
                var owned = await MeetingGapRecoveryService.BuildUnifiedOwnerResultsAsync(
                    request.LiveResult,
                    tracks,
                    _transcriptionClient,
                    request.CancellationToken).ConfigureAwait(false);
                micTranscript = owned.Microphone;
                systemTranscript = owned.System;
                healthWarnings.Add(request.LiveResult.Gaps.Count == 0
                    ? "Final raw transcript is owned by the live model; no gap recovery was required."
                    : $"Final raw transcript is owned by the live model; the configured final model recovered {request.LiveResult.Gaps.Count} measured gap(s).");
            }
            else
            {
                micTranscript = tracks.MicrophonePath is not null
                    ? await _transcriptionClient.TranscribeFileAsync("Meeting microphone", tracks.MicrophonePath).ConfigureAwait(false)
                    : new TranscriptionResult("");
                request.CancellationToken.ThrowIfCancellationRequested();
                systemTranscript = tracks.SystemPath is not null
                    ? await _transcriptionClient.TranscribeFileAsync("Meeting system audio", tracks.SystemPath).ConfigureAwait(false)
                    : new TranscriptionResult("");
            }
            LogTranscriptionDiagnostic("meeting-mic", micTranscript);
            if (tracks.MicrophonePath is not null && (micTranscript.Segments?.Count ?? 0) == 0)
            {
                healthWarnings.Add(MicTranscriptEmptyWarning);
            }

            LogTranscriptionDiagnostic("meeting-system", systemTranscript);
            if (tracks.SystemPath is not null &&
                (systemTranscript.Segments?.Count ?? 0) == 0 &&
                (micTranscript.Segments?.Count ?? 0) == 0)
            {
                healthWarnings.Add(SystemTranscriptEmptyWarning);
            }

            if (diarizationTask is null && tracks.SystemPath is not null)
            {
                diarizationTask = _diarizationClient.DiarizeFileAsync(tracks.SystemPath);
            }
            List<DiarizedSegment> diarizationSegments = [];
            if (diarizationTask is not null)
            {
                try
                {
                    diarizationObserved = true;
                    var diarization = await diarizationTask.ConfigureAwait(false);
                    diarizationSegments = diarization.Segments ?? [];
                    if (diarizationSegments.Count == 0)
                    {
                        healthWarnings.Add(DiarizationNoSegmentsWarning);
                    }
                    foreach (var warning in diarization.Warnings ?? [])
                    {
                        healthWarnings.Add(warning);
                    }
                    _logService?.Info(
                        $"Meeting diarization completed. segments={diarizationSegments.Count}; provider={diarization.Provider}; processingMs={diarization.ProcessingMs}; modelReused={diarization.ModelReused}");
                }
                catch (Exception exception)
                {
                    healthWarnings.Add(DiarizationFailedWarning);
                    _logService?.Info($"Meeting diarization failed. category={exception.GetType().Name}");
                }
            }

            var micTimeline = _persistence.BuildTrackTimeline(journal, MeetingAudioChannel.Microphone);
            var systemTimeline = _persistence.BuildTrackTimeline(journal, MeetingAudioChannel.System);
            var merged = TranscriptFormatter.Merge(
                MeetingTranscriptTimeline.Normalize(micTranscript.Segments, micTimeline),
                MeetingTranscriptTimeline.Normalize(systemTranscript.Segments, systemTimeline),
                MeetingTranscriptTimeline.Normalize(diarizationSegments, systemTimeline),
                request.StartedAt);
            var durationMs = MeasureRecordedDurationMs(tracks);
            var cleanedWarnings = CleanupHealthWarnings(
                healthWarnings,
                merged,
                diarizationSegments.Count > 0);
            MeetingSessionState terminalState;
            if (string.IsNullOrWhiteSpace(merged))
            {
                cleanedWarnings.Add(TranscriptMissingWarning);
                terminalState = MeetingSessionState.Failed;
            }
            else
            {
                terminalState = MeetingSessionState.Completed;
            }

            journal = journal with
            {
                Title = request.Title,
                State = terminalState == MeetingSessionState.Completed
                    ? MeetingSessionState.Finalizing
                    : terminalState,
                Warnings = cleanedWarnings,
                RecoveredFromInterruption = request.Recovered || journal.RecoveredFromInterruption,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            _persistence.Save(journal);
            return new MeetingFinalizationOutcome(
                new RecordedMeetingResult(
                    journal.SessionId,
                    request.Title,
                    request.StartedAt,
                    durationMs,
                    merged,
                    MeetingPersistenceBoundary.OwnedMicrophonePath(journal.RetainRecording, tracks),
                    MeetingPersistenceBoundary.OwnedSystemPath(journal.RetainRecording, tracks),
                    cleanedWarnings,
                    terminalState,
                    journal.SystemCaptureMode,
                    request.Recovered || journal.RecoveredFromInterruption,
                    journal.LiveModelId,
                    journal.LiveTranscriptOwnership,
                    journal.FinalTranscriptOwnerModelId,
                    journal.GapRecoveryModelId),
                journal,
                terminalState);
        }
        finally
        {
            await AwaitDiarizationBeforeCleanupAsync(
                diarizationTask,
                diarizationObserved,
                exception => _logService?.Info($"Deferred diarization cleanup failed. category={exception.GetType().Name}"))
                .ConfigureAwait(false);
        }
    }

    public static List<string> CleanupHealthWarnings(
        IEnumerable<string>? warnings,
        string? transcript = null,
        bool diarizationSucceededWithSegments = false)
    {
        if (warnings is null)
        {
            return [];
        }
        var warningList = warnings
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Select(warning => warning.Trim())
            .ToList();
        var diarizationSucceeded = diarizationSucceededWithSegments ||
                                   warningList.Any(HasSuccessfulDiarizationSegmentCount) ||
                                   TranscriptHasDiarizedSpeakerLabels(transcript);
        var cleaned = new List<string>();
        foreach (var warning in warningList)
        {
            var normalized = NormalizeHealthWarning(warning, diarizationSucceeded);
            if (!string.IsNullOrWhiteSpace(normalized) &&
                !cleaned.Contains(normalized, StringComparer.Ordinal))
            {
                cleaned.Add(normalized);
            }
        }
        return cleaned;
    }

    internal static async Task AwaitDiarizationBeforeCleanupAsync(
        Task? diarizationTask,
        bool alreadyObserved,
        Action<Exception>? reportFailure = null)
    {
        if (diarizationTask is null || alreadyObserved)
        {
            return;
        }
        try
        {
            await diarizationTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            reportFailure?.Invoke(exception);
        }
    }

    private void LogTranscriptionDiagnostic(string context, TranscriptionResult result)
    {
        var diagnostic = string.IsNullOrWhiteSpace(result.Diagnostic)
            ? "none"
            : DiagnosticCategory(result.Diagnostic);
        _logService?.Info(
            $"Meeting transcription completed. context={context}; engine={_transcriptionClient.EngineId}; model={_transcriptionClient.ModelId}; durationMs={result.DurationMs}; segments={result.Segments?.Count ?? 0}; diagnosticCategory={diagnostic}");
    }

    internal static string DiagnosticCategory(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }
        var first = value.Split(new[] { '\r', '\n', ':', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? "unknown";
        var safe = new string(first.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or ' ').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe[..Math.Min(safe.Length, 64)].Replace(' ', '-');
    }

    internal static int MeasureRecordedDurationMs(MeetingAudioPaths paths)
    {
        var durations = new List<double>();
        foreach (var path in new[] { paths.MicrophonePath, paths.SystemPath }.OfType<string>())
        {
            try
            {
                using var reader = new AudioFileReader(path);
                durations.Add(reader.TotalTime.TotalMilliseconds);
            }
            catch
            {
            }
        }
        return durations.Count == 0 ? 0 : (int)Math.Round(durations.Max());
    }

    private static string? NormalizeHealthWarning(string warning, bool diarizationSucceeded)
    {
        var text = warning.Trim();
        if (text.Equals(SystemTranscriptEmptyWarning, StringComparison.OrdinalIgnoreCase) ||
            text.Contains("System transcript was empty", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (text.Equals(DiarizationNoSegmentsWarning, StringComparison.OrdinalIgnoreCase) ||
            text.Contains("returned no speaker segments", StringComparison.OrdinalIgnoreCase))
        {
            return diarizationSucceeded ? null : DiarizationNoSegmentsWarning;
        }
        if (text.Equals(DiarizationFailedWarning, StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Speaker diarization failed", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Diarization failed.", StringComparison.OrdinalIgnoreCase))
        {
            return diarizationSucceeded ? null : DiarizationFailedWarning;
        }
        if (text.StartsWith("ASR engine:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Diarization segments:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Timing diarization ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Worker stderr:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Input path:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Input bytes:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Model cache:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Backend:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("torchvision", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("site-packages", StringComparison.OrdinalIgnoreCase) ||
            text.Contains(".py:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains('\\') ||
            text.Contains('/'))
        {
            return null;
        }
        return text.StartsWith("Microphone", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("System audio", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Muesli", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Recording", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Meeting", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Speaker", StringComparison.OrdinalIgnoreCase)
            ? text
            : null;
    }

    private static bool HasSuccessfulDiarizationSegmentCount(string warning)
    {
        if (!warning.StartsWith("Diarization segments:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var parts = warning.Split(':', 2);
        return parts.Length == 2 && int.TryParse(parts[1].Trim(), out var count) && count > 0;
    }

    private static bool TranscriptHasDiarizedSpeakerLabels(string? transcript) =>
        !string.IsNullOrWhiteSpace(transcript) &&
        System.Text.RegularExpressions.Regex.IsMatch(
            transcript,
            @"\[\d{2}:\d{2}:\d{2}\]\s+[^:\r\n]+:\s");
}
