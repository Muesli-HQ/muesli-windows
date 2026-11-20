using NAudio.Wave;

namespace Muesli.Windows.Services;

// Ports of macOS BufferedDictationPreviewDelivery.swift, BufferedDictationFinalizer.swift and
// BufferedDictationRecovery.swift (Muesli-HQ/muesli 2f10b23d).

/// <summary>
/// A slow editor must not replay an unbounded queue of obsolete hypotheses. Close stops admission and
/// drops pending previews; an already-started write may finish its readback before the target's final
/// replacement.
/// </summary>
public sealed class BufferedDictationPreviewDelivery(
    Func<BufferedTranscriptionUpdate, Task<bool>> deliver,
    Action<BufferedTranscriptionUpdate, bool>? onResult = null)
{
    private readonly object _lock = new();
    private BufferedTranscriptionUpdate? _pending;
    private ulong _latestRevision;
    private Task? _worker;
    private bool _closed;

    public void Offer(BufferedTranscriptionUpdate update)
    {
        lock (_lock)
        {
            if (_closed || update.Revision <= _latestRevision) return;
            _latestRevision = update.Revision;
            // Recognition keeps its hypothesis for agreement/alignment, but the destination receives only
            // committed groups. Never revise a visible draft while the user is speaking; final cleanup is a
            // separate write.
            _pending = new BufferedTranscriptionUpdate(
                TranscriptionEngineArtifactsFilter.ApplyToCommittedText(update.CommittedText), "", update.Revision);
            _worker ??= Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            BufferedTranscriptionUpdate update;
            lock (_lock)
            {
                if (_closed || _pending is null) { _worker = null; return; }
                update = _pending;
                _pending = null;
            }
            var accepted = await deliver(update).ConfigureAwait(false);
            onResult?.Invoke(update, accepted);
            if (!accepted) Close();
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
            _pending = null;
        }
    }

    public Task WaitUntilIdleAsync()
    {
        lock (_lock) return _worker ?? Task.CompletedTask;
    }
}

/// <summary>
/// Recognition reuse is independent of whether the editor accepted live text. All output still goes
/// through the original target lease and the cleanup pipeline.
/// </summary>
public static class BufferedDictationFinalizer
{
    // ponytail: macOS requires exact equality. Windows streams through StreamingPcmNormalizer while the WAV
    // is resampled separately at merge, so this matches DictationRollingTranscriber's 50 ms tolerance.
    // Use exact equality if both ever share one resampler.
    public const int RecordingCoverageToleranceSamples = 800;

    /// <summary>
    /// An incomplete stream is recoverable from its saved recording, never an invitation to silently run a
    /// second, whole-recording recognition pass.
    /// </summary>
    public sealed class IncompleteStream(string reason, string partialText)
        : Exception($"Live transcription could not finish ({reason}).")
    {
        public string Reason { get; } = reason;
        public string PartialText { get; } = partialText;
    }

    public static int? RecordedSampleCount(string path)
    {
        try
        {
            using var reader = new WaveFileReader(path);
            var samples = (long)Math.Round(reader.TotalTime.TotalSeconds * 16_000);
            return samples is > 0 and <= int.MaxValue ? (int)samples : null;
        }
        catch (Exception exception) when (exception is IOException or FormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Covers(int streamed, int recorded) => Math.Abs(streamed - recorded) <= RecordingCoverageToleranceSamples;

    public static async Task<T> TranscribeAsync<T>(
        BufferedTranscriptionSession? session,
        int? recordedSampleCount,
        Func<string, Task<T>> reuse,
        Func<Task<T>> nonStreaming,
        Action<string>? onDecision = null,
        TimeSpan? drainTimeout = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session is null) return await nonStreaming().ConfigureAwait(false);
            var streamed = session.Diagnostics.ReceivedSamples;
            if (recordedSampleCount is not > 0 || !Covers(streamed, recordedSampleCount.Value))
            {
                session.Cancel();
                onDecision?.Invoke($"incomplete:recording_coverage_mismatch:recorded={recordedSampleCount ?? -1}:streamed={streamed}");
                cancellationToken.ThrowIfCancellationRequested();
                throw new IncompleteStream("recording_coverage_mismatch", session.CurrentTranscript);
            }
            if (session.KnownReuseFallbackReason is { } known)
            {
                session.Cancel();
                var reason = BufferedTranscriptionSession.RawValue(known);
                onDecision?.Invoke($"incomplete:{reason}:committed_evidence {session.CurrentReuseDiagnostics}");
                cancellationToken.ThrowIfCancellationRequested();
                throw new IncompleteStream(reason, session.CurrentTranscript);
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(drainTimeout ?? TimeSpan.FromSeconds(10));
            var result = await session.FinishForReuseAsync(deadline.Token).ConfigureAwait(false);
            var timedOut = deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.CanReuse || !Covers(result.ReceivedSamples, recordedSampleCount.Value) ||
                result.DecodedThroughSample != result.ReceivedSamples)
            {
                var reason = timedOut ? "tail_drain_timeout"
                    : result.FallbackReason is { } fallback ? BufferedTranscriptionSession.RawValue(fallback) : "incomplete_coverage";
                onDecision?.Invoke($"incomplete:{reason} {result.Diagnostics}");
                cancellationToken.ThrowIfCancellationRequested();
                throw new IncompleteStream(reason, result.Text);
            }
            onDecision?.Invoke($"reuse {result.Diagnostics}");
            // If cleanup fails or is cancelled, propagate normally. Do not run ASR and cleanup again as
            // though the accepted recognition had failed.
            cancellationToken.ThrowIfCancellationRequested();
            return await reuse(result.Text).ConfigureAwait(false);
        }
        finally
        {
            session?.Cancel();
        }
    }
}

/// <summary>macOS BackendOption.supportsBufferedLiveTranscription: local Parakeet, Whisper and Bodhan.</summary>
public static class BufferedLiveDictationSupport
{
    public static bool Supports(NativeAsrModelKind kind) =>
        kind is NativeAsrModelKind.Parakeet or NativeAsrModelKind.ParakeetTransducer or NativeAsrModelKind.Whisper or NativeAsrModelKind.Bodhan;

    /// <summary>Timed agreement needs word timings; sherpa Whisper and Bodhan stream untimed chunks.</summary>
    public static bool HasWordTimings(NativeAsrModelKind kind) =>
        kind is NativeAsrModelKind.Parakeet or NativeAsrModelKind.ParakeetTransducer;

    /// <summary>macOS MuesliSettingsDefinitions buffered_live_dictation unavailable reasons.</summary>
    public static string? UnavailableReason(TranscriptionModelDefinition model, bool ready) =>
        !Supports(model.Kind) ? "Select a local Parakeet, Whisper, or Bodhan model first."
        : ready ? null : "Download the selected dictation model first.";
}

/// <summary>
/// macOS BufferedDictationCaptureAudio: target capture runs concurrently with recorder startup. Keep up to
/// 3 s of initial PCM while it is pending, then hand it off in sample order. Overflow refuses the handoff.
/// </summary>
public sealed class BufferedDictationCaptureAudio
{
    private const int MaximumPendingSamples = 16_000 * 3;
    private readonly object _lock = new();
    private readonly List<float> _samples = [];
    private Action<float[]>? _consume;
    private bool _overflowed;

    public void Append(float[] values)
    {
        lock (_lock)
        {
            if (_consume is not null) { _consume(values); return; }
            if (_overflowed || _samples.Count + values.Length > MaximumPendingSamples)
            {
                _overflowed = true;
                _samples.Clear();
                return;
            }
            _samples.AddRange(values);
        }
    }

    public bool Attach(Action<float[]> consume)
    {
        lock (_lock)
        {
            if (_overflowed) return false;
            consume([.. _samples]);
            _samples.Clear();
            _consume = consume;
            return true;
        }
    }

    /// <summary>Drops pending and future audio (no live target).</summary>
    public void Detach()
    {
        lock (_lock)
        {
            _overflowed = true;
            _consume = null;
            _samples.Clear();
        }
    }
}

/// <summary>macOS FluidAudioBufferedWordTimings and FluidAudioBackend.previewWindow over sherpa results.</summary>
public static class BufferedPreviewWindows
{
    public static BufferedTranscriptionWindow FromSherpa(SherpaOnnx.OfflineRecognizerResult result, bool requiresWordTimings) =>
        requiresWordTimings
            ? Window(result.Text ?? "", BuildWords(result.Tokens, result.Timestamps, result.Durations))
            : new BufferedTranscriptionWindow(result.Text ?? "", []);

    /// <summary>
    /// Text/token disagreement can recover when the next window supplies more context. Decoder errors stay
    /// fatal and never reach here.
    /// </summary>
    public static BufferedTranscriptionWindow Window(string text, IReadOnlyList<BufferedTranscriptionWord> words)
    {
        try
        {
            return BufferedTranscriptionWindow.Aligned(text, words);
        }
        catch (BufferedTranscriptionSession.SessionFailure failure) when (failure.Kind == BufferedTranscriptionSession.FailureKind.InvalidTimings)
        {
            throw new BufferedTranscriptionPreviewDeferral(BufferedTranscriptionPreviewDeferral.TextAlignmentMismatch);
        }
    }

    /// <summary>
    /// A boundary token (leading space or SentencePiece "▁") starts a word. Sherpa exposes no token
    /// confidence, so confidence stays unknown (reuse telemetry only). A token without a duration is an
    /// emission point, as on macOS.
    /// </summary>
    public static List<BufferedTranscriptionWord> BuildWords(
        IReadOnlyList<string>? tokens, IReadOnlyList<float>? timestamps, IReadOnlyList<float>? durations)
    {
        var words = new List<BufferedTranscriptionWord>();
        if (tokens is null || timestamps is null) return words;
        var current = "";
        double start = 0, end = 0;
        void Flush()
        {
            var trimmed = current.Trim(' ');
            if (trimmed.Length > 0) words.Add(new BufferedTranscriptionWord(trimmed, start, end));
        }
        for (var index = 0; index < Math.Min(tokens.Count, timestamps.Count); index++)
        {
            var token = tokens[index] ?? "";
            if (token.Length == 0 || token is "<blank>" or "<pad>") continue;
            var boundary = token[0] is ' ' or '▁';
            var startsNewWord = boundary || current.Length == 0;
            if (startsNewWord && current.Length > 0)
            {
                Flush();
                current = "";
            }
            var tokenStart = (double)timestamps[index];
            var duration = durations is not null && index < durations.Count && durations[index] > 0 ? durations[index] : 0;
            if (startsNewWord)
            {
                current = boundary ? token[1..] : token;
                start = tokenStart;
            }
            else
            {
                current += token;
            }
            end = tokenStart + duration;
        }
        Flush();
        return words;
    }
}

/// <summary>
/// Preserves a failed streaming session for explicit recovery without rerunning transcription or
/// moving/deleting the recorder's original audio.
/// </summary>
public static class BufferedDictationRecovery
{
    public const string DirectoryName = "Dictation Recovery";

    /// <summary>
    /// A small fallback when copying the WAV into the recovery directory fails. Reserve a sidecar
    /// atomically; never overwrite an earlier attempt.
    /// </summary>
    public static string SaveTranscript(string transcript, string wavPath)
    {
        var sidecar = wavPath + ".transcript.txt";
        if (WriteNew(transcript, sidecar)) return sidecar;
        var unique = $"{wavPath}.{Guid.NewGuid().ToString().ToUpperInvariant()}.transcript.txt";
        if (!WriteNew(transcript, unique)) throw new IOException($"File exists: {unique}");
        return unique;
    }

    private static bool WriteNew(string text, string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
        try
        {
            using (stream)
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                writer.Write(text);
            return true;
        }
        catch
        {
            try { File.Delete(path); } catch (IOException) { }
            throw;
        }
    }

    public sealed record Outcome(string ShowPath, string Message);

    /// <summary>
    /// macOS IncompleteStream recovery and its alert text. The recorder deletes its WAV after stop, so where
    /// macOS leaves the original in place when the copy fails, Windows moves it into the recovery folder.
    /// </summary>
    public static Outcome Preserve(string wavPath, string transcript, string directory)
    {
        try
        {
            return new Outcome(Save(wavPath, transcript, directory),
                "The recording and available transcript were saved for recovery. Text already inserted has been left in place.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A full disk or permissions error must not delete the only remaining recording.
            var kept = wavPath;
            try
            {
                Directory.CreateDirectory(directory);
                var moved = Path.Combine(directory, $"{Guid.NewGuid().ToString().ToUpperInvariant()}.wav");
                File.Move(wavPath, moved);
                kept = moved;
            }
            catch (Exception moveFailure) when (moveFailure is IOException or UnauthorizedAccessException) { }
            try
            {
                var text = SaveTranscript(transcript, kept);
                return new Outcome(kept, $"The recovery copy could not be saved. The original recording remains at {kept}, with the available transcript at {text}.");
            }
            catch (Exception textFailure) when (textFailure is IOException or UnauthorizedAccessException)
            {
                return new Outcome(kept, $"The recording remains at {kept}. The available transcript could not be saved; copy it here before closing:\n\n{transcript}");
            }
        }
    }

    public static string Save(string wavPath, string transcript, string directory)
    {
        var attributes = File.GetAttributes(wavPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("The recording is not a regular file.");
        Directory.CreateDirectory(directory);
        var session = Path.Combine(directory, Guid.NewGuid().ToString().ToUpperInvariant());
        Directory.CreateDirectory(session);
        try
        {
            File.Copy(wavPath, Path.Combine(session, "audio.wav"));
            var text = Path.Combine(session, "transcript.txt");
            var temporary = text + ".tmp";
            File.WriteAllText(temporary, transcript, new System.Text.UTF8Encoding(false));
            File.Move(temporary, text);
            return session;
        }
        catch
        {
            // Only this invocation's incomplete copy is disposable. The source and every previously saved
            // recovery remain available on failure.
            try { Directory.Delete(session, recursive: true); } catch (IOException) { }
            throw;
        }
    }
}
