namespace Muesli.Windows.Services;

public sealed class MeetingRecordingCoordinator : IDisposable
{
    private const string SystemAudioMissingWarning = "System audio was not captured; remote speakers may be missing.";
    private const string SystemTranscriptEmptyWarning = "System transcript was empty even though system audio was captured.";
    private const string MicTranscriptEmptyWarning = "Mic transcript was empty even though mic audio was captured.";
    private const string DiarizationFailedWarning = "Speaker identification was unavailable. The transcript remains chronological with [You] and [System audio] labels.";
    private const string DiarizationNoSegmentsWarning = "Speaker identification found no distinct remote speakers. The transcript remains chronological with [System audio] labels.";
    private const string HfTokenMissingWarning = "HF_TOKEN is not set; pyannote speaker diarization may fail unless the model is already cached.";
    private const string DiarizationDependenciesMissingWarning = "Speaker identification is not installed. The transcript remains chronological with [You] and [System audio] labels. See Settings > Models > Speaker Diarization for setup status.";
    private const string DiarizationModelAccessWarning = "Speaker diarization model access failed. Check HF_TOKEN and accepted Hugging Face model access.";
    private const string TranscriptMissingWarning = "Meeting saved with no transcript; audio capture or transcription may have failed.";
    private const string PartialTranscriptWarning = "Meeting saved with a partial transcript because one or more audio tracks failed.";
    private const string AllSystemFallbackWarning = "All system audio was captured because meeting-process capture was unavailable; unrelated computer sounds may be included.";

    private readonly AudioCaptureService _micCapture = new();
    private readonly SystemAudioCaptureService _systemCapture = new();
    private readonly TranscriptionWorkerClient _workerClient = new();
    private readonly AppLogService? _logService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _startedAt;
    private string? _systemCaptureWarning;

    public bool IsRecording { get; private set; }
    public bool IsBusy { get; private set; }
    public event EventHandler<string>? CaptureWarning;

    public MeetingRecordingCoordinator(AppLogService? logService = null)
    {
        _logService = logService;
        _systemCapture.FallbackActivated += (_, warning) =>
        {
            _logService?.Info(warning);
            CaptureWarning?.Invoke(this, warning);
        };
    }

    public async Task<MeetingRecordingStartResult> StartAsync(
        string? microphoneName,
        int? targetProcessId,
        string sessionId)
    {
        await _gate.WaitAsync();
        try
        {
            if (IsRecording || IsBusy)
            {
                throw new InvalidOperationException("Meeting recording is already active.");
            }

            IsBusy = true;
            _startedAt = DateTime.Now;
            await _micCapture.StartAsync(microphoneName, $"meeting-{sessionId}-microphone");
            SystemAudioCaptureStartResult? systemStart = null;
            _systemCaptureWarning = null;
            try
            {
                systemStart = await _systemCapture.StartAsync(targetProcessId, $"meeting-{sessionId}-remote");
                _systemCaptureWarning = systemStart.Warning;
            }
            catch (Exception exception)
            {
                // Some machines block loopback capture. Mic recording still remains useful.
                _systemCaptureWarning = $"Remote-speaker audio could not start: {exception.Message}";
            }

            IsRecording = true;
            return new MeetingRecordingStartResult(
                systemStart?.Mode ?? "microphone-only",
                systemStart?.UsedFallback ?? false,
                _systemCaptureWarning);
        }
        finally
        {
            IsBusy = false;
            _gate.Release();
        }
    }

    public async Task<RecordedMeetingResult> StopAsync(
        string title,
        TranscriptionOptions options,
        Action<RecordedMeetingCapture>? captureReady = null)
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsRecording || IsBusy)
            {
                throw new InvalidOperationException("Meeting recording is not running.");
            }

            IsBusy = true;
            IsRecording = false;
            CapturedAudio? micAudio = null;
            CapturedAudio? systemAudio = null;
            List<string> healthWarnings = new();
            if (!string.IsNullOrWhiteSpace(_systemCaptureWarning))
            {
                healthWarnings.Add(_systemCaptureWarning);
            }

            try
            {
                micAudio = await _micCapture.StopAsync(includeBytes: false);
                _logService?.Info($"Mic audio: {micAudio.LastCapturePath} ({micAudio.LengthBytes} bytes)");
                if (!string.IsNullOrWhiteSpace(micAudio.Warning))
                {
                    healthWarnings.Add(micAudio.Warning);
                    _logService?.Info(micAudio.Warning);
                }
            }
            catch (Exception exception)
            {
                _logService?.Error("Microphone audio finalization failed.", exception);
                healthWarnings.Add($"Microphone capture failed: {exception.Message}");
            }

            try
            {
                systemAudio = await _systemCapture.StopAsync();
                _logService?.Info($"System audio: {systemAudio.LastCapturePath} ({systemAudio.LengthBytes} bytes)");
                if (!string.IsNullOrWhiteSpace(systemAudio.Warning)
                    && !healthWarnings.Contains(systemAudio.Warning, StringComparer.OrdinalIgnoreCase))
                {
                    healthWarnings.Add(systemAudio.Warning);
                }
            }
            catch (Exception exception)
            {
                _logService?.Info($"System audio unavailable: {exception.Message}");
                systemAudio = null;
            }

            if (micAudio is null && systemAudio is null)
            {
                throw new InvalidOperationException("Neither meeting audio track could be finalized.");
            }

            if (systemAudio is null)
            {
                healthWarnings.Add(SystemAudioMissingWarning);
            }
            else if (systemAudio.LengthBytes <= 44)
            {
                healthWarnings.Add("System audio was nearly silent; remote speakers may be missing.");
            }

            if (micAudio is null)
            {
                healthWarnings.Add("Microphone audio was not captured; your voice may be missing.");
            }
            else if (micAudio.LengthBytes <= 44)
            {
                healthWarnings.Add("Microphone audio was nearly silent; your voice may not have been recorded.");
            }

            var durationMs = (int)Math.Min(int.MaxValue, (DateTime.Now - _startedAt).TotalMilliseconds);
            captureReady?.Invoke(new RecordedMeetingCapture(
                title,
                _startedAt,
                durationMs,
                micAudio?.LastCapturePath ?? "",
                systemAudio?.LastCapturePath,
                healthWarnings.ToList()));

            var trackFailures = 0;
            var micTranscript = new TranscriptionResult("");
            if (micAudio is not null && micAudio.LengthBytes > 44)
            {
                try
                {
                    micTranscript = await _workerClient.TranscribeFileAsync($"{title} mic", micAudio.LastCapturePath, options);
                    if (HasChunkFailure(micTranscript))
                    {
                        trackFailures++;
                        healthWarnings.Add("Microphone transcription failed: a later audio chunk could not be processed; earlier chunks were preserved.");
                    }
                }
                catch (Exception exception)
                {
                    trackFailures++;
                    _logService?.Error("Microphone track transcription failed.", exception);
                    healthWarnings.Add($"Microphone transcription failed: {exception.Message}");
                }
            }
            _logService?.Info($"Mic transcript segments: {micTranscript.Segments?.Count ?? 0}");

            if (micAudio is not null && micAudio.LengthBytes > 44 && (micTranscript.Segments?.Count ?? 0) == 0 && trackFailures == 0)
            {
                healthWarnings.Add(MicTranscriptEmptyWarning);
            }

            var systemTranscript = new TranscriptionResult("");
            if (systemAudio is not null && systemAudio.LengthBytes > 44)
            {
                try
                {
                    systemTranscript = await _workerClient.TranscribeFileAsync($"{title} system", systemAudio.LastCapturePath, options);
                    if (HasChunkFailure(systemTranscript))
                    {
                        trackFailures++;
                        healthWarnings.Add("System transcription failed: a later audio chunk could not be processed; earlier chunks were preserved.");
                    }
                }
                catch (Exception exception)
                {
                    trackFailures++;
                    _logService?.Error("System track transcription failed.", exception);
                    healthWarnings.Add($"System transcription failed: {exception.Message}");
                }
            }
            _logService?.Info($"System transcript segments: {systemTranscript.Segments?.Count ?? 0}");

            if (systemAudio is not null && systemAudio.LengthBytes > 44 && (systemTranscript.Segments?.Count ?? 0) == 0 && trackFailures == 0)
            {
                if ((micTranscript.Segments?.Count ?? 0) > 0)
                {
                    _logService?.Info("System transcript empty; treating as non-actionable because meeting still has mic transcript.");
                }
                else
                {
                    _logService?.Info("System transcript empty and no mic transcript segments were available.");
                }
            }

            // Attempt speaker diarization on system audio when available
            List<DiarizedSegment> diarizationSegments = new();
            var diarizationStatus = "not-applicable";
            if (systemAudio is not null && systemAudio.LengthBytes > 44)
            {
                diarizationStatus = "fallback";
                _logService?.Info($"Starting diarization on system audio: {systemAudio.LastCapturePath}");
                try
                {
                    var diarizationResult = await _workerClient.DiarizeFileAsync(systemAudio.LastCapturePath);
                    diarizationSegments = diarizationResult.Segments ?? new List<DiarizedSegment>();
                    _logService?.Info($"Diarization returned {diarizationSegments.Count} segments");
                    if (diarizationResult.Warnings is not null)
                    {
                        foreach (var warning in diarizationResult.Warnings)
                        {
                            _logService?.Info($"Diarization warning: {warning}");
                            if (!string.IsNullOrWhiteSpace(warning))
                                healthWarnings.Add(warning);
                        }
                    }
                    if (diarizationSegments.Count == 0)
                    {
                        _logService?.Info("Diarization returned 0 segments; using chronological [System audio] fallback.");
                        healthWarnings.Add(DiarizationNoSegmentsWarning);
                    }
                    else
                    {
                        diarizationStatus = "identified";
                    }
                }
                catch (Exception exception)
                {
                    _logService?.Error($"Diarization failed on {systemAudio.LastCapturePath}", exception);
                    diarizationSegments = new List<DiarizedSegment>();
                    healthWarnings.Add(DescribeDiarizationFailure(exception));
                }
            }
            else
            {
                _logService?.Info("Diarization skipped: no system audio available.");
            }

            _logService?.Info(diarizationSegments.Count > 0
                ? "Using diarized chronological transcript merge."
                : "Using chronological fallback transcript merge.");
            var formatted = TranscriptFormatter.MergeWithSegments(
                micTranscript.Segments ?? new List<TranscriptSegment>(),
                systemTranscript.Segments ?? new List<TranscriptSegment>(),
                diarizationSegments,
                _startedAt);
            var merged = formatted.Transcript;

            var summary = MeetingSummaryService.CreateSummary(merged);
            if (string.IsNullOrWhiteSpace(merged))
            {
                healthWarnings.Add(TranscriptMissingWarning);
            }
            else if (trackFailures > 0)
            {
                healthWarnings.Add(PartialTranscriptWarning);
            }

            var distinctWarnings = CleanupHealthWarnings(
                healthWarnings,
                transcript: merged,
                diarizationSucceededWithSegments: diarizationSegments.Count > 0);

            return new RecordedMeetingResult(
                title,
                _startedAt,
                durationMs,
                merged,
                summary,
                micAudio?.LastCapturePath ?? "",
                systemAudio?.LastCapturePath,
                distinctWarnings,
                (micAudio?.OwnedPaths ?? [])
                    .Concat(systemAudio?.OwnedPaths ?? [])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                formatted.Segments,
                diarizationStatus);
        }
        finally
        {
            IsBusy = false;
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _micCapture.Dispose();
        _systemCapture.Dispose();
        _workerClient.Dispose();
    }

    public static List<string> CleanupHealthWarnings(
        IEnumerable<string>? warnings,
        string? transcript = null,
        bool diarizationSucceededWithSegments = false)
    {
        var cleaned = new List<string>();
        if (warnings is null)
        {
            return cleaned;
        }

        var warningList = warnings
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Select(warning => warning.Trim())
            .ToList();
        var effectiveDiarizationSuccess = diarizationSucceededWithSegments
            || warningList.Any(HasSuccessfulDiarizationSegmentCount)
            || TranscriptHasDiarizedSpeakerLabels(transcript);

        foreach (var warning in warningList)
        {
            var normalized = NormalizeHealthWarning(warning, effectiveDiarizationSuccess);
            if (!string.IsNullOrWhiteSpace(normalized)
                && !cleaned.Any(existing => string.Equals(existing, normalized, StringComparison.Ordinal)))
            {
                cleaned.Add(normalized);
            }
        }

        return cleaned;
    }

    private static string? NormalizeHealthWarning(string? warning, bool diarizationSucceededWithSegments)
    {
        if (string.IsNullOrWhiteSpace(warning))
        {
            return null;
        }

        var text = warning.Trim();

        if (text.Equals(SystemAudioMissingWarning, StringComparison.OrdinalIgnoreCase))
            return SystemAudioMissingWarning;
        if (text.Equals(SystemTranscriptEmptyWarning, StringComparison.OrdinalIgnoreCase)
            || text.Contains("System transcript was empty", StringComparison.OrdinalIgnoreCase))
            return null;
        if (text.Equals(MicTranscriptEmptyWarning, StringComparison.OrdinalIgnoreCase)
            || text.Contains("Microphone transcript was empty", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Mic transcript was empty", StringComparison.OrdinalIgnoreCase))
            return MicTranscriptEmptyWarning;
        if (text.Equals("System audio was nearly silent; remote speakers may be missing.", StringComparison.OrdinalIgnoreCase))
            return "System audio was nearly silent; remote speakers may be missing.";
        if (text.Equals("Microphone audio was nearly silent; your voice may not have been recorded.", StringComparison.OrdinalIgnoreCase))
            return "Microphone audio was nearly silent; your voice may not have been recorded.";
        if (text.StartsWith("Microphone audio was not captured", StringComparison.OrdinalIgnoreCase))
            return "Microphone audio was not captured; your voice may be missing.";
        if (text.StartsWith("Microphone device changed during the meeting", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Microphone capture resumed", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Microphone capture failed after a device change", StringComparison.OrdinalIgnoreCase))
            return text;
        if (text.StartsWith("Meeting-process audio remained silent", StringComparison.OrdinalIgnoreCase))
            return "Meeting-process audio was silent; Muesli switched to all-system audio capture.";

        if (text.Equals(DiarizationNoSegmentsWarning, StringComparison.OrdinalIgnoreCase)
            || text.Contains("returned no speaker segments", StringComparison.OrdinalIgnoreCase))
            return DiarizationNoSegmentsWarning;
        if (text.Equals(DiarizationFailedWarning, StringComparison.OrdinalIgnoreCase)
            || text.Contains("Speaker diarization failed", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Diarization failed.", StringComparison.OrdinalIgnoreCase))
            return DiarizationFailedWarning;
        if (text.Equals(TranscriptMissingWarning, StringComparison.OrdinalIgnoreCase)
            || text.Contains("no transcript", StringComparison.OrdinalIgnoreCase))
            return TranscriptMissingWarning;

        if (text.Equals(PartialTranscriptWarning, StringComparison.OrdinalIgnoreCase))
            return PartialTranscriptWarning;
        if (text.Contains("all system audio is being captured as a fallback", StringComparison.OrdinalIgnoreCase)
            || text.Equals(AllSystemFallbackWarning, StringComparison.OrdinalIgnoreCase))
            return AllSystemFallbackWarning;
        if (text.StartsWith("Microphone transcription failed:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("System transcription failed:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Microphone capture failed:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Meeting processing failed:", StringComparison.OrdinalIgnoreCase))
            return text;

        if (text.Equals(DiarizationDependenciesMissingWarning, StringComparison.OrdinalIgnoreCase)
            || text.Contains("dependencies missing", StringComparison.OrdinalIgnoreCase)
            || text.Contains("dependencies are missing", StringComparison.OrdinalIgnoreCase)
            || text.Contains("No module named", StringComparison.OrdinalIgnoreCase))
            return DiarizationDependenciesMissingWarning;

        var mentionsHfToken = text.Contains("HF_TOKEN", StringComparison.OrdinalIgnoreCase);
        var mentionsUnauthenticatedRequests = text.Contains("unauthenticated requests", StringComparison.OrdinalIgnoreCase);
        var mentionsModelAccess = text.Contains("access denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("model access failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("gated repo", StringComparison.OrdinalIgnoreCase)
            || text.Contains("gated repository", StringComparison.OrdinalIgnoreCase)
            || text.Contains("403", StringComparison.OrdinalIgnoreCase)
            || text.Contains("401", StringComparison.OrdinalIgnoreCase)
            || text.Contains("permission", StringComparison.OrdinalIgnoreCase);
        if (mentionsModelAccess)
            return DiarizationModelAccessWarning;
        if (mentionsHfToken || mentionsUnauthenticatedRequests)
            return diarizationSucceededWithSegments ? null : HfTokenMissingWarning;

        if (text.StartsWith("ASR engine:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Diarization segments:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Timing diarization ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Worker stderr:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Input path:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Input bytes:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Model cache:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Backend:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (text.Contains("torchvision", StringComparison.OrdinalIgnoreCase)
            || text.Contains("UserWarning", StringComparison.OrdinalIgnoreCase)
            || text.Contains("site-packages", StringComparison.OrdinalIgnoreCase)
            || text.Contains(".py:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("\\", StringComparison.OrdinalIgnoreCase)
            || text.Contains("/", StringComparison.OrdinalIgnoreCase)
            || text.Contains("waveform", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return null;
    }

    private static bool HasSuccessfulDiarizationSegmentCount(string warning)
    {
        if (!warning.StartsWith("Diarization segments:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = warning.Split(':', 2);
        return parts.Length == 2
            && int.TryParse(parts[1].Trim(), out var count)
            && count > 0;
    }

    private static bool HasChunkFailure(TranscriptionResult result) =>
        result.Diagnostic?.Contains(" failed:", StringComparison.OrdinalIgnoreCase) == true;

    internal static string DescribeDiarizationFailure(Exception exception)
    {
        var message = exception.Message;
        if (message.Contains("No module named", StringComparison.OrdinalIgnoreCase)
            || message.Contains("soundfile", StringComparison.OrdinalIgnoreCase)
            || message.Contains("pyannote", StringComparison.OrdinalIgnoreCase))
        {
            return DiarizationDependenciesMissingWarning;
        }

        if (message.Contains("HF_TOKEN", StringComparison.OrdinalIgnoreCase)
            || message.Contains("access denied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("gated", StringComparison.OrdinalIgnoreCase)
            || message.Contains("401", StringComparison.OrdinalIgnoreCase)
            || message.Contains("403", StringComparison.OrdinalIgnoreCase))
        {
            return DiarizationModelAccessWarning;
        }

        return DiarizationFailedWarning;
    }

    private static bool TranscriptHasDiarizedSpeakerLabels(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return false;
        }

        return System.Text.RegularExpressions.Regex.IsMatch(
            transcript,
            @"\[\d{2}:\d{2}:\d{2}\]\s+[^:\r\n]+:\s");
    }
}

public sealed record RecordedMeetingResult(
    string Title,
    DateTime StartedAt,
    int DurationMs,
    string Transcript,
    string Summary,
    string MicAudioPath,
    string? SystemAudioPath,
    List<string>? HealthWarnings = null,
    List<string>? TemporaryAudioPaths = null,
    List<MeetingTranscriptSegment>? TranscriptSegments = null,
    string DiarizationStatus = "unknown");

public sealed record MeetingRecordingStartResult(
    string RemoteAudioMode,
    bool UsedAllSystemFallback,
    string? Warning);

public sealed record RecordedMeetingCapture(
    string Title,
    DateTime StartedAt,
    int DurationMs,
    string MicAudioPath,
    string? SystemAudioPath,
    List<string> HealthWarnings);
