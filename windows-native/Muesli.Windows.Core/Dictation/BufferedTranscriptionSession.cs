using System.Globalization;
using System.Text;

namespace Muesli.Windows.Services;

// Port of macOS BufferedTranscriptionSession.swift (Muesli-HQ/muesli 2f10b23d). Rules, thresholds and
// diagnostics match line for line; comments marked "macOS:" quote the reason given there. Times are in
// seconds and audio is 16 kHz mono float PCM, as on macOS.

/// <summary>
/// A decoder produced a hypothesis that cannot yet be tied to the supplied audio. Preserve the last
/// valid preview and retry only after more audio.
/// </summary>
public sealed class BufferedTranscriptionPreviewDeferral(string reason) : Exception(reason)
{
    public const string MissingWordTimings = "missing_word_timings";
    public const string TextAlignmentMismatch = "text_alignment_mismatch";
    public const string InvalidWordTimings = "invalid_word_timings";
    public const string CollapsedWordTimings = "collapsed_word_timings";

    public string Reason { get; } = reason;
}

/// <summary>Word times are relative to the start of the PCM supplied to the decoder.</summary>
public sealed record BufferedTranscriptionWord(string Text, double Start, double End, float? Confidence = null);

public sealed record BufferedTranscriptionWindow(string Text, IReadOnlyList<BufferedTranscriptionWord> Words)
{
    /// <summary>
    /// Preserve the decoder's punctuation, whitespace and scripts when splitting the result into timed
    /// words. Missing/unmatched timing is a preview failure, never permission to drop text at a rollover.
    /// </summary>
    public static BufferedTranscriptionWindow Aligned(string text, IReadOnlyList<BufferedTranscriptionWord> words)
    {
        var cursor = 0;
        var aligned = new List<BufferedTranscriptionWord>();
        foreach (var word in words)
        {
            var spelling = word.Text.Trim();
            if (spelling.Length == 0) continue;
            var index = text.IndexOf(spelling, cursor, StringComparison.Ordinal);
            if (index < 0 || !IsWhitespace(text, cursor, index))
                throw new BufferedTranscriptionSession.SessionFailure(BufferedTranscriptionSession.FailureKind.InvalidTimings);
            aligned.Add(word with { Text = text[cursor..(index + spelling.Length)] });
            cursor = index + spelling.Length;
        }
        if (!IsWhitespace(text, cursor, text.Length))
            throw new BufferedTranscriptionSession.SessionFailure(BufferedTranscriptionSession.FailureKind.InvalidTimings);
        return new BufferedTranscriptionWindow(text, aligned);
    }

    private static bool IsWhitespace(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
            if (!char.IsWhiteSpace(text[i])) return false;
        return true;
    }
}

public sealed record BufferedTranscriptionUpdate(string CommittedText, string Tail, ulong Revision = 0)
{
    public string Text => CommittedText + Tail;
}

/// <summary>
/// macOS: Buffered previews supply final text after validated ownership and complete audio coverage.
/// Confidence is diagnostic; structural failures retain partial text and the recording for explicit
/// recovery. Audio capture never waits for inference. There is one decoder in flight and new arrivals
/// coalesce into the next snapshot. Audio-time ownership, rather than matching strings, preserves
/// deliberately repeated words at rollovers.
/// </summary>
public sealed class BufferedTranscriptionSession
{
    private const double SampleRate = 16_000;

    public sealed record SessionDiagnostics(int ReceivedSamples, int DecodeCount, ulong Revision, bool Closed);

    public sealed record Configuration
    {
        public int MinimumSamples { get; init; } = 32_000;
        public int UpdateSamples { get; init; } = 16_000;
        public int WindowSamples { get; init; } = 240_000;
        public int RightContextSamples { get; init; } = 48_000;
        public int LeftContextSamples { get; init; } = 16_000;
        public int MaximumBufferedSamples { get; init; } = 720_000;
        public int MaximumRecoveryWindowSamples { get; init; } = 480_000;
        /// <summary>Consecutive window timings may move slightly as context grows.</summary>
        public double MaximumTimestampDrift { get; init; } = 0.25;
        /// <summary>Only suppress an entirely silent window. Quiet speech is not trimmed.</summary>
        public float SilenceRms { get; init; } = 0.0001f;
        /// <summary>
        /// Whisper's local agreement ignores cosmetic revisions and can use a following timed word as
        /// context instead of the fixed age cutoff.
        /// </summary>
        public bool LexicalAgreement { get; init; }

        public static Configuration Whisper => new() { LexicalAgreement = true, WindowSamples = 128_000 };
    }

    /// <summary>Provisional telemetry thresholds, not finalization gates or calibrated correctness.</summary>
    public static class ReusePolicy
    {
        public const float AgreedConfidenceFloor = 0.15f;
        public const float UnagreedConfidenceFloor = 0.8f;
    }

    public enum ReuseFallbackReason
    {
        Cancelled, TranscriptionFailed, NoAudio, IncompleteCoverage,
        MissingConfidence, InvalidConfidence, LowConfidence,
        UnagreedContextTooLong, UnsupportedWordTiming,
        CommittedTranscriptChanged, UnverifiedEmptyAudio
    }

    /// <summary>
    /// macOS: Quality uncertainty never requests a new full-session transcription. Coverage, alignment,
    /// and lifecycle failures still prevent completion.
    /// </summary>
    public static bool PreventsReuse(ReuseFallbackReason reason) => reason is ReuseFallbackReason.Cancelled
        or ReuseFallbackReason.TranscriptionFailed or ReuseFallbackReason.NoAudio or ReuseFallbackReason.IncompleteCoverage;

    /// <summary>macOS rawValue spelling, used in diagnostics and recovery reasons.</summary>
    public static string RawValue(ReuseFallbackReason reason)
    {
        var name = reason.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public sealed record ReuseDiagnostics(
        ReuseFallbackReason? QualityConcern, int CommittedWordCount, int AgreedWordCount, int UnagreedWordCount,
        int MissingConfidenceCount, int InvalidConfidenceCount, int LowConfidenceCount, float? MinimumConfidence,
        double MaximumUnagreedSpan, int DiscardedSamples, string? FirstLowConfidenceEvidence, string? LastLowConfidenceEvidence)
    {
        public override string ToString() =>
            $"qualityConcern={(QualityConcern is { } concern ? RawValue(concern) : "none")}:words={CommittedWordCount}:agreed={AgreedWordCount}:" +
            $"unagreed={UnagreedWordCount}:missing={MissingConfidenceCount}:invalid={InvalidConfidenceCount}:low={LowConfidenceCount}:" +
            $"minConfidence={(MinimumConfidence?.ToString(CultureInfo.InvariantCulture) ?? "none")}:maxUnagreedSpan={MaximumUnagreedSpan.ToString(CultureInfo.InvariantCulture)}:" +
            $"discardedSamples={DiscardedSamples}:firstLow=[{FirstLowConfidenceEvidence ?? "none"}]:lastLow=[{LastLowConfidenceEvidence ?? "none"}]";
    }

    /// <summary>
    /// Diagnostic text may be partial when Completed is false. Callers must check CanReuse and
    /// recording sample-count equality before using it.
    /// </summary>
    public sealed record ReuseResult(
        string Text, bool CanReuse, bool Completed, ReuseFallbackReason? FallbackReason,
        int ReceivedSamples, int DecodedThroughSample, ReuseDiagnostics Diagnostics);

    private sealed class ReuseQuality
    {
        public ReuseFallbackReason? QualityConcern;
        public ReuseFallbackReason? StructuralReason;
        public int CommittedWords, AgreedWords, UnagreedWords, MissingConfidence, InvalidConfidence, LowConfidence;
        public float? MinimumConfidence;
        public double MaximumUnagreedSpan;
        public double? CurrentUnagreedStart, CurrentUnagreedEnd;
        public string? FirstLowConfidenceEvidence, LastLowConfidenceEvidence;

        public void RecordConcern(ReuseFallbackReason reason)
        {
            if (PreventsReuse(reason)) StructuralReason ??= reason;
            else QualityConcern ??= reason;
        }

        public ReuseQuality Clone() => (ReuseQuality)MemberwiseClone();
    }

    public enum FailureKind { AudioOverrun, InvalidTimings, CannotAdvance, OwnershipUnresolved }

    public sealed class SessionFailure(FailureKind kind) : Exception(Describe(kind))
    {
        public FailureKind Kind { get; } = kind;

        private static string Describe(FailureKind kind) => kind switch
        {
            FailureKind.AudioOverrun => "Live transcription could not keep up. The recording is still available for final transcription.",
            FailureKind.InvalidTimings => "Live transcription returned unusable word timing.",
            FailureKind.CannotAdvance => "Live transcription could not safely advance its audio window.",
            _ => "Live transcription could not resolve its audio boundary with additional context. The recording remains available for final transcription."
        };

        public string Category => Kind switch
        {
            FailureKind.AudioOverrun => "audioOverrun",
            FailureKind.InvalidTimings => "invalidTimings",
            FailureKind.CannotAdvance => "cannotAdvance",
            _ => "ownershipUnresolved"
        };
    }

    private enum RecoveryReason { MissingAnchor, AmbiguousAnchor, ChangedAnchor, ChangedBoundary, EmptyHypothesis, NoRollProgress, InseparableTimings, UnusablePreview }

    private static string RawValue(RecoveryReason reason) => reason switch
    {
        RecoveryReason.MissingAnchor => "missing_anchor",
        RecoveryReason.AmbiguousAnchor => "ambiguous_anchor",
        RecoveryReason.ChangedAnchor => "changed_anchor_waiting",
        RecoveryReason.ChangedBoundary => "changed_boundary",
        RecoveryReason.EmptyHypothesis => "empty_hypothesis",
        RecoveryReason.NoRollProgress => "no_roll_progress",
        RecoveryReason.InseparableTimings => "inseparable_timings",
        _ => "unusable_preview"
    };

    private sealed class AlignmentRecovery(RecoveryReason reason) : Exception
    {
        public RecoveryReason Reason { get; } = reason;
        public List<BufferedTranscriptionWord>? Candidate { get; init; }
        public List<BufferedTranscriptionWord>? AgreementTail { get; init; }
    }

    private sealed class State
    {
        public List<float> Audio = [];
        public int Offset;
        public int LastDecodedEnd;
        // Frozen at commitment, never revised by a later lexical hypothesis.
        public int CommittedAudioThrough;
        public bool Continuity;
        public int AlignmentDeferrals;
        public int LastCommitSnapshotEnd;
        // No audio is owned at session start, including a word timed at (0, 0).
        public double? OwnedUntil;
        public string CommittedText = "";
        public List<BufferedTranscriptionWord> CommittedAnchors = [];
        // Early Whisper commitments may outlast the four retained anchors. Keep their furthest decoded
        // end until the silence cutoff passes it.
        public double? MaximumOwnedWordEnd;
        public List<BufferedTranscriptionWord> PreviousTail = [];
        public List<bool> PreviousTailReuseAgreement = [];
        public (string Text, int ThroughSample)? ContinuityRecovery;
        public int LastPublishedThroughSample;
        public string LastPublishedTail = "";
        public string LastPresentedTail = "";
        public bool Recovering;
        public List<BufferedTranscriptionWord>? RecoveryCandidate;
        public BufferedTranscriptionWord? FollowingAnchor;
        public ulong Revision;
        public int ReceivedSamples;
        public int DecodeCount;
        public ReuseQuality ReuseQuality = new();
        public ReuseFallbackReason? ReuseTerminalReason;
        public string? FinalText;
        public int DecodedThroughSample;
        public bool Running;
        public bool Finishing;
        public bool Closed;

        public State Clone()
        {
            var copy = (State)MemberwiseClone();
            copy.Audio = [.. Audio];
            copy.CommittedAnchors = [.. CommittedAnchors];
            copy.PreviousTail = [.. PreviousTail];
            copy.PreviousTailReuseAgreement = [.. PreviousTailReuseAgreement];
            copy.RecoveryCandidate = RecoveryCandidate is null ? null : [.. RecoveryCandidate];
            copy.ReuseQuality = ReuseQuality.Clone();
            return copy;
        }
    }

    private sealed record Snapshot(float[] Audio, int Offset, bool Final);

    // Callback delivery shares this lock with cancellation, so no callback can start after Cancel()
    // returns. Monitor is reentrant, matching macOS's NSRecursiveLock, so a callback may cancel.
    private readonly object _lock = new();
    private State _state = new();
    // Lifecycle state must not be restored by alignment's transactional rollback.
    private readonly List<TaskCompletionSource> _waiters = [];
    private CancellationTokenSource? _drainCancellation;
    private readonly Configuration _configuration;
    private readonly Func<float[], CancellationToken, Task<BufferedTranscriptionWindow>> _decode;
    private readonly Func<float[], CancellationToken, Task<string>>? _decodeWithoutTimings;
    private readonly Action<BufferedTranscriptionUpdate> _onUpdate;
    private readonly Action<Exception> _onFailure;
    private readonly Action<string> _onDiagnostic;
    private readonly Action<int> _onSilentAudio;

    public BufferedTranscriptionSession(
        Configuration? configuration = null,
        Func<float[], CancellationToken, Task<BufferedTranscriptionWindow>>? decode = null,
        Func<float[], CancellationToken, Task<string>>? decodeWithoutTimings = null,
        Action<int>? onSilentAudio = null,
        Action<BufferedTranscriptionUpdate>? onUpdate = null,
        Action<Exception>? onFailure = null,
        Action<string>? onDiagnostic = null)
    {
        var c = configuration ?? new Configuration();
        if (c.MinimumSamples <= 0 || c.UpdateSamples <= 0 || c.WindowSamples <= c.MinimumSamples ||
            c.RightContextSamples <= 0 || c.LeftContextSamples < 0 ||
            c.RightContextSamples + c.LeftContextSamples >= c.WindowSamples ||
            c.MaximumBufferedSamples < c.WindowSamples || c.MaximumRecoveryWindowSamples < c.WindowSamples ||
            !(c.MaximumTimestampDrift > 0) || !double.IsFinite(c.MaximumTimestampDrift))
            throw new ArgumentException("Invalid buffered transcription configuration.", nameof(configuration));
        if (decode is null && decodeWithoutTimings is null)
            throw new ArgumentException("A decoder is required.");
        _configuration = c;
        _decode = decode ?? ((_, _) => throw new SessionFailure(FailureKind.InvalidTimings));
        _decodeWithoutTimings = decodeWithoutTimings;
        _onSilentAudio = onSilentAudio ?? (_ => { });
        _onUpdate = onUpdate ?? (_ => { });
        _onFailure = onFailure ?? (_ => { });
        _onDiagnostic = onDiagnostic ?? (_ => { });
        // Untimed backends own disjoint PCM chunks from the first update.
        _state.Continuity = decode is null;
    }

    public ReuseFallbackReason? KnownReuseFallbackReason
    {
        get { lock (_lock) return _state.ReuseTerminalReason ?? _state.ReuseQuality.StructuralReason; }
    }

    /// <summary>
    /// Best raw hypothesis, including the undecided tail, for saved recovery. This makes no claim that
    /// the stream completed successfully.
    /// </summary>
    public string CurrentTranscript
    {
        get
        {
            lock (_lock)
                return _state.FinalText ?? _state.ContinuityRecovery?.Text ?? _state.CommittedText + _state.LastPublishedTail;
        }
    }

    public ReuseDiagnostics CurrentReuseDiagnostics
    {
        get { lock (_lock) return BuildReuseResult(cancelled: false).Diagnostics; }
    }

    public SessionDiagnostics Diagnostics
    {
        get
        {
            lock (_lock)
                return new SessionDiagnostics(_state.ReceivedSamples, _state.DecodeCount, _state.Revision, _state.Closed);
        }
    }

    public void Enqueue(float[] samples)
    {
        lock (_lock)
        {
            if (_state.Closed || _state.Finishing || samples.Length == 0) return;
            if (samples.Length > _configuration.MaximumBufferedSamples - _state.Audio.Count || !samples.All(float.IsFinite))
            {
                _onDiagnostic($"audio_buffer_overrun:retained={_state.Audio.Count}:incoming={samples.Length}:limit={_configuration.MaximumBufferedSamples}");
                if (_state.Closed) return;
                Fail(new SessionFailure(FailureKind.AudioOverrun));
                return;
            }
            _state.Audio.AddRange(samples);
            _state.ReceivedSamples += samples.Length;
            StartIfNeeded();
        }
    }

    public void Cancel()
    {
        lock (_lock) Close(ReuseFallbackReason.Cancelled);
    }

    /// <summary>
    /// Closes capture and drains all accepted samples. Failures use onFailure; cancellation returns
    /// promptly even when the model cannot cancel inference.
    /// </summary>
    public Task FinishAsync()
    {
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (_state.Closed) return Task.CompletedTask;
            _state.Finishing = true;
            _waiters.Add(waiter);
            StartIfNeeded();
        }
        return waiter.Task;
    }

    /// <summary>
    /// Drains the retained tail using the established streaming policy. Quality concerns remain
    /// telemetry; successful ownership and complete sample coverage permit reuse. Cancellation returns
    /// partial text promptly without waiting for an uncooperative decoder.
    /// </summary>
    public async Task<ReuseResult> FinishForReuseAsync(CancellationToken cancellationToken = default)
    {
        using (cancellationToken.Register(Cancel))
            await FinishAsync().ConfigureAwait(false);
        lock (_lock) return BuildReuseResult(cancellationToken.IsCancellationRequested);
    }

    private ReuseResult BuildReuseResult(bool cancelled)
    {
        var quality = _state.ReuseQuality;
        var completed = _state.FinalText is not null && _state.Closed && _state.ReuseTerminalReason is null
            && _state.DecodedThroughSample == _state.ReceivedSamples;
        ReuseFallbackReason? reason = cancelled ? ReuseFallbackReason.Cancelled
            : _state.ReuseTerminalReason ?? quality.StructuralReason ?? (completed ? null : ReuseFallbackReason.IncompleteCoverage);
        return new ReuseResult(
            _state.FinalText ?? _state.ContinuityRecovery?.Text ?? _state.CommittedText + _state.LastPublishedTail,
            completed && reason is null, completed && !cancelled, reason, _state.ReceivedSamples, _state.DecodedThroughSample,
            new ReuseDiagnostics(quality.QualityConcern, quality.CommittedWords, quality.AgreedWords, quality.UnagreedWords,
                quality.MissingConfidence, quality.InvalidConfidence, quality.LowConfidence, quality.MinimumConfidence,
                quality.MaximumUnagreedSpan, _state.Offset, quality.FirstLowConfidenceEvidence, quality.LastLowConfidenceEvidence));
    }

    private void StartIfNeeded()
    {
        if (_state.Running || _state.Closed) return;
        var minimum = _state.Continuity ? ContinuityChunkSamples : _configuration.MinimumSamples;
        if (!_state.Finishing && _state.Audio.Count < minimum) return;
        if (!_state.Finishing && !_state.Continuity &&
            _state.Offset + _state.Audio.Count - _state.LastDecodedEnd < _configuration.UpdateSamples) return;
        _state.Running = true;
        var cancellation = new CancellationTokenSource();
        // This runs under the lock, so a new task cannot reach TakeSnapshot()/Close() before its handle
        // has been published here.
        _drainCancellation = cancellation;
        _ = Task.Run(async () =>
        {
            await DrainAsync(cancellation.Token).ConfigureAwait(false);
            lock (_lock)
            {
                // A new arrival can start another drain after TakeSnapshot marks this one idle; its
                // cancellation handle must remain intact.
                if (ReferenceEquals(_drainCancellation, cancellation)) _drainCancellation = null;
            }
            cancellation.Dispose();
        });
    }

    private Snapshot? TakeSnapshot()
    {
        lock (_lock)
        {
            if (_state.Closed) { _state.Running = false; return null; }
            if (_state.Finishing && _state.Audio.Count == 0)
            {
                if (_state.Continuity && _state.ReceivedSamples > 0 && _state.DecodedThroughSample == _state.ReceivedSamples)
                {
                    _state.FinalText = _state.CommittedText;
                    Close();
                }
                else
                {
                    Close(_state.ReceivedSamples == 0 ? ReuseFallbackReason.NoAudio : ReuseFallbackReason.IncompleteCoverage);
                }
                return null;
            }
            if (_state.Continuity)
            {
                if (!_state.Finishing && _state.Audio.Count < ContinuityChunkSamples)
                {
                    _state.Running = false;
                    return null;
                }
                var chunk = Math.Min(_state.Audio.Count, ContinuityChunkSamples);
                return new Snapshot(_state.Audio.GetRange(0, chunk).ToArray(), _state.Offset,
                    _state.Finishing && chunk == _state.Audio.Count);
            }
            var end = _state.Offset + _state.Audio.Count;
            if (!_state.Finishing && (_state.Audio.Count < _configuration.MinimumSamples || end - _state.LastDecodedEnd < _configuration.UpdateSamples))
            {
                _state.Running = false;
                return null;
            }
            var limit = _state.Recovering ? RecoveryWindowLimit : _configuration.WindowSamples;
            var count = Math.Min(_state.Audio.Count, limit);
            if (_state.Recovering && _state.Offset + count <= _state.LastDecodedEnd)
            {
                if (_state.Finishing)
                {
                    _onDiagnostic("ownership_recovery_exhausted:finish_without_new_audio");
                    if (!_state.Closed && BeginContinuity(_state.LastDecodedEnd)) return TakeSnapshot();
                    if (!_state.Closed) Fail(new SessionFailure(FailureKind.OwnershipUnresolved));
                }
                else
                {
                    _state.Running = false;
                }
                return null;
            }
            return new Snapshot(_state.Audio.GetRange(0, count).ToArray(), _state.Offset,
                _state.Finishing && count == _state.Audio.Count);
        }
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        while (TakeSnapshot() is { } snapshot)
        {
            try
            {
                bool continuity;
                lock (_lock) continuity = _state.Continuity;
                var decodeStarted = System.Diagnostics.Stopwatch.StartNew();
                var silent = Rms(snapshot.Audio) <= _configuration.SilenceRms;
                if (continuity && _decodeWithoutTimings is not null)
                {
                    var text = "";
                    if (!silent)
                    {
                        lock (_lock)
                        {
                            if (_state.Closed) return;
                            _state.DecodeCount++;
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        text = await _decodeWithoutTimings(snapshot.Audio, cancellationToken).ConfigureAwait(false);
                    }
                    lock (_lock)
                    {
                        if (_state.Closed) return;
                        _onDiagnostic($"continuity_decode:audioMs={snapshot.Audio.Length / 16}:decodeMs={decodeStarted.ElapsedMilliseconds}:final={Lower(snapshot.Final)}");
                        if (_state.Closed) return;
                        if (silent) _onSilentAudio(snapshot.Audio.Length);
                        AcceptContinuity(text, snapshot);
                    }
                    continue;
                }

                BufferedTranscriptionWindow result;
                if (silent)
                {
                    result = new BufferedTranscriptionWindow("", []);
                }
                else
                {
                    lock (_lock)
                    {
                        if (_state.Closed) return;
                        _state.DecodeCount++;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    result = await _decode(snapshot.Audio, cancellationToken).ConfigureAwait(false);
                }
                lock (_lock)
                {
                    if (_state.Closed) continue;
                    _onDiagnostic($"decode:audioMs={snapshot.Audio.Length / 16}:decodeMs={decodeStarted.ElapsedMilliseconds}:final={Lower(snapshot.Final)}");
                    if (_state.Closed) continue;
                    // Accept can update anchors before discovering that a roll cannot advance. Recovery must
                    // restore the entire proposal, including PCM, before deferring this observation.
                    var checkpoint = _state.Clone();
                    try
                    {
                        Accept(result, snapshot, silent);
                    }
                    catch (AlignmentRecovery recovery)
                    {
                        _state = checkpoint;
                        DeferAlignment(recovery, snapshot);
                    }
                    catch (SessionFailure failure) when (failure.Kind == FailureKind.InvalidTimings && _decodeWithoutTimings is not null)
                    {
                        _state = checkpoint;
                        DeferAlignment(new AlignmentRecovery(RecoveryReason.UnusablePreview), snapshot);
                    }
                }
            }
            catch (BufferedTranscriptionPreviewDeferral deferral)
            {
                lock (_lock)
                {
                    if (_state.Closed) return;
                    // Untimed decoding has no recoverable timing contract. A decoder violating that
                    // contract must not spin on one chunk.
                    if (_state.Continuity) { Fail(deferral); return; }
                    _onDiagnostic($"preview_deferred:{deferral.Reason}");
                    if (_state.Closed) return;
                    DeferAlignment(new AlignmentRecovery(RecoveryReason.UnusablePreview), snapshot);
                }
            }
            catch (Exception exception)
            {
                lock (_lock) if (!_state.Closed) Fail(exception);
            }
        }
    }

    private void Accept(BufferedTranscriptionWindow window, Snapshot snapshot, bool isSilent)
    {
        var result = BufferedTranscriptionWindow.Aligned(window.Text, window.Words);
        var duration = snapshot.Audio.Length / SampleRate;
        var previousStart = double.NegativeInfinity;
        foreach (var word in result.Words)
        {
            if (!double.IsFinite(word.Start) || !double.IsFinite(word.End) || word.Start < 0 ||
                word.Start < previousStart || word.End < word.Start || word.End > duration + 0.25)
                throw new SessionFailure(FailureKind.InvalidTimings);
            previousStart = word.Start;
        }
        if (result.Text.Trim().Length != 0 && result.Words.Count == 0)
            throw new SessionFailure(FailureKind.InvalidTimings);
        var rolls = snapshot.Audio.Length >= _configuration.WindowSamples && !snapshot.Final;
        // An empty decoder result does not establish ownership of audible PCM. Keep the window for
        // bounded context recovery rather than discarding a lead that a later hypothesis could recognize.
        // The existing acoustic silence bypass may still advance without inventing sentence boundaries.
        if (result.Words.Count == 0 && !isSilent && (rolls || _state.Recovering))
            throw new AlignmentRecovery(RecoveryReason.EmptyHypothesis);
        if (result.Words.Count == 0 && (_state.CommittedAnchors.Count > 0 || _state.LastPublishedTail.Length > 0))
            throw new AlignmentRecovery(RecoveryReason.EmptyHypothesis);
        var @base = snapshot.Offset / SampleRate;
        var absoluteWords = result.Words.Select(word => word with { Start = @base + word.Start, End = @base + word.End }).ToList();
        var unowned = UnownedStart(absoluteWords, @base);
        if (unowned > 0)
        {
            var ownedEnd = absoluteWords.Take(unowned).Max(word => word.End);
            _state.MaximumOwnedWordEnd = Math.Max(_state.MaximumOwnedWordEnd ?? ownedEnd, ownedEnd);
        }
        var words = absoluteWords.Skip(unowned).ToList();
        var matureUntil = @base + duration - _configuration.RightContextSamples / SampleRate;
        var matureCount = words.TakeWhile(word => word.End <= matureUntil).Count();
        var agreementCount = 0;
        foreach (var (previous, current) in _state.PreviousTail.Zip(words))
        {
            // Position and both timestamps still identify the same occurrence. Whisper may change edge
            // punctuation/casing indefinitely despite agreeing on the word. Freeze its latest rendering
            // without holding the entire following prefix hostage to those cosmetic revisions.
            if (!WordsAgree(previous, current)) break;
            agreementCount++;
        }
        var reuseAgreement = ReuseAgreement(words, freshCoverage: snapshot.Offset + snapshot.Audio.Length > _state.LastDecodedEnd);
        // A full encoder window must advance even after a coalesced first decode. Its existing
        // right-context policy remains the bounded fallback; shorter windows freeze agreed prefixes
        // instead of rewriting whole sentences. Local agreement plus a following timed word already
        // establishes a complete prefix, so Whisper does not wait for the right-context budget.
        var confirmedCount = _configuration.LexicalAgreement ? Math.Min(agreementCount, Math.Max(0, words.Count - 1)) : 0;
        while (confirmedCount > 0 && words[confirmedCount].End == words[confirmedCount].Start) confirmedCount--;
        // Maturity is an evidence budget, not a reason to wait for the next physical crop. A stable
        // ordered seam can close a mature prefix even when an earlier unowned word is still revising.
        var stableSeamCount = !rolls && !snapshot.Final && snapshot.Offset + snapshot.Audio.Length > _state.LastDecodedEnd
            ? MaturePrefixWithStableSeam(words, matureCount) : 0;
        var matureCommitCount = rolls ? matureCount : Math.Max(Math.Min(matureCount, agreementCount), stableSeamCount);
        var commitCount = snapshot.Final ? words.Count : Math.Max(confirmedCount, matureCommitCount);
        // Preserve quantized zero-duration words, but never make one the live boundary anchor. Keep it
        // pending until a positive-duration neighbor can anchor the group; finalization flushes all.
        var proposedCount = commitCount;
        var zeroAnchorHeld = 0;
        if (!snapshot.Final && _configuration.LexicalAgreement)
        {
            while (commitCount > 0 && words[commitCount - 1].End == words[commitCount - 1].Start) commitCount--;
            zeroAnchorHeld = proposedCount - commitCount;
            // After startup or a long silent roll there is no preceding anchor to corroborate an onset
            // revision. Establish a small timed group, rather than freezing one speculative word.
            if (_state.CommittedAnchors.Count == 0)
            {
                var lexicalCount = words.Take(commitCount).Count(word => word.End > word.Start && HasAlphanumeric(word.Text));
                // A real one-word utterance may be followed by any length pause. Independent agreement
                // plus mature context can confirm it.
                if (lexicalCount == 0 || (lexicalCount < 2 && commitCount > Math.Min(matureCount, agreementCount)))
                    commitCount = 0;
            }
        }
        if (_decodeWithoutTimings is not null && !snapshot.Final && commitCount == 0 && words.Count > 0 &&
            snapshot.Offset + snapshot.Audio.Length - _state.LastCommitSnapshotEnd >= _configuration.RightContextSamples + 2 * _configuration.UpdateSamples)
            throw new AlignmentRecovery(RecoveryReason.NoRollProgress) { AgreementTail = words };
        _onDiagnostic($"commit_policy:mature={matureCount}:agreed={agreementCount}:confirmed={confirmedCount}:stableSeam={stableSeamCount}:" +
            $"committed={commitCount}:pending={words.Count - commitCount}:zeroAnchorHeld={zeroAnchorHeld}:" +
            $"initialGroupHeld={proposedCount - zeroAnchorHeld - commitCount}:roll={Lower(rolls)}");
        if (_state.Closed) return;
        var retainFrom = @base + duration;
        var retainsLexicalAnchor = false;
        if (commitCount > 0)
        {
            var committed = words.Take(commitCount).ToList();
            var committedEnd = committed.Max(word => word.End);
            _state.MaximumOwnedWordEnd = Math.Max(_state.MaximumOwnedWordEnd ?? committedEnd, committedEnd);
            RecordReuseQuality(committed, reuseAgreement.Take(commitCount).ToList());
            var last = committed[^1];
            var next = words.Count > commitCount ? words[commitCount] : null;
            var ownedUntil = OwnershipBoundary(last, next);
            _state.OwnedUntil = ownedUntil;
            _state.CommittedAudioThrough = Math.Max(_state.CommittedAudioThrough,
                Math.Min(snapshot.Offset + snapshot.Audio.Length, (int)(ownedUntil * SampleRate)));
            _state.LastCommitSnapshotEnd = snapshot.Offset + snapshot.Audio.Length;
            // Keep the last owned word available as a timing anchor at rollover, including when a long
            // word starts before the usual left context.
            retainFrom = Math.Min(Math.Min(ownedUntil, next?.Start ?? ownedUntil), last.Start);
            retainsLexicalAnchor = true;
            _state.CommittedText += Join(committed, _state.CommittedText);
            _state.CommittedAnchors = [.. _state.CommittedAnchors.Concat(committed).TakeLast(4)];
            var ownedEnd = _configuration.LexicalAgreement ? _state.MaximumOwnedWordEnd ?? last.End : last.End;
            if (rolls && (next?.Start ?? matureUntil) >= matureUntil &&
                ownedEnd + _configuration.LeftContextSamples / SampleRate + _configuration.MaximumTimestampDrift < matureUntil)
            {
                // A long decoded silence after the last word needs no lexical anchor. Retain its right
                // context and own only the silent lead.
                _state.OwnedUntil = matureUntil;
                retainFrom = matureUntil;
                retainsLexicalAnchor = false;
            }
        }
        else if (rolls)
        {
            var lastOwnedEnd = _configuration.LexicalAgreement ? _state.MaximumOwnedWordEnd : _state.CommittedAnchors.LastOrDefault()?.End;
            var hasSilentLead = lastOwnedEnd is not { } lastEnd ||
                lastEnd + _configuration.LeftContextSamples / SampleRate + _configuration.MaximumTimestampDrift < matureUntil;
            // A next word at the maturity cutoff is not itself evidence of a long pause. Preserve nearby
            // owned onsets and their timing margin. Empty pending text does not mean every early-owned
            // word ended. Keep a lexical seam while any earlier owned interval crosses it.
            if ((words.Count == 0 && (!_configuration.LexicalAgreement || hasSilentLead)) ||
                ((words.FirstOrDefault()?.Start ?? @base) >= matureUntil && hasSilentLead))
            {
                // There is no unowned speech in this leading span. Its old anchors may leave the retained
                // PCM; the absolute time boundary then owns it.
                _state.OwnedUntil = Math.Max(_state.OwnedUntil ?? double.NegativeInfinity, matureUntil);
                retainFrom = matureUntil;
            }
            else if (_state.CommittedAnchors.LastOrDefault() is { } lastOwned && _state.OwnedUntil is { } owned)
            {
                // A fractional rolling window can end while the next word straddles the maturity cutoff.
                // Previously owned audio still provides safe progress; no additional commitment is needed.
                retainFrom = Math.Min(Math.Min(owned, words.FirstOrDefault()?.Start ?? owned), lastOwned.Start);
                retainsLexicalAnchor = true;
            }
            else
            {
                throw new AlignmentRecovery(RecoveryReason.NoRollProgress) { AgreementTail = words };
            }
        }
        var tailWords = words.Skip(commitCount).ToList();
        var tail = Join(tailWords, _state.CommittedText);
        // Preserve agreement across rolls in absolute audio time.
        _state.PreviousTail = tailWords;
        _state.PreviousTailReuseAgreement = [.. reuseAgreement.Skip(commitCount)];
        _state.LastPublishedTail = tail;
        _state.LastPublishedThroughSample = snapshot.Offset + snapshot.Audio.Length;
        _state.FollowingAnchor = tailWords.FirstOrDefault();
        _state.LastDecodedEnd = snapshot.Offset + snapshot.Audio.Length;
        _state.DecodedThroughSample = _state.LastDecodedEnd;
        // Empty recognition over nonzero microphone noise is acoustic uncertainty, not missing sample
        // coverage. Preserve it as quality telemetry only once that decoded span is discarded or final.
        if ((rolls || snapshot.Final) && result.Words.Count == 0 && snapshot.Audio.Any(sample => sample != 0))
            _state.ReuseQuality.RecordConcern(ReuseFallbackReason.UnverifiedEmptyAudio);
        if (rolls)
        {
            if (retainsLexicalAnchor && _state.CommittedAnchors.Count >= 2)
            {
                // An owned onset can move into a preceding pause after cropping. Keep its predecessor so the
                // two-sided boundary check still has evidence. Long words/gaps must not pin the window:
                // retain this extra context only when a full update can advance.
                var preceding = _state.CommittedAnchors[^2];
                var contextualStart = Math.Min(retainFrom, preceding.Start);
                var contextualDiscard = (int)(contextualStart * SampleRate) - _configuration.LeftContextSamples - _state.Offset;
                if (contextualDiscard >= _configuration.UpdateSamples) retainFrom = contextualStart;
            }
            // Once a decoded silent lead has retired every lexical anchor and there is no pending word, old
            // context is already owned. Feeding it back to Whisper can hallucinate a new phrase over the
            // pause. Keep left context whenever an anchor or an unfinished word needs it.
            var ownedWordsEnded = _configuration.LexicalAgreement
                ? _state.MaximumOwnedWordEnd is not { } maximumEnd || maximumEnd <= retainFrom
                : _state.CommittedAnchors.All(anchor => anchor.End <= retainFrom);
            var leftContext = _configuration.LexicalAgreement && !retainsLexicalAnchor && tailWords.Count == 0 && ownedWordsEnded
                ? 0 : _configuration.LeftContextSamples;
            var discard = (int)(retainFrom * SampleRate) - leftContext - _state.Offset;
            if (discard <= 0 || discard > _state.Audio.Count)
                throw new AlignmentRecovery(RecoveryReason.NoRollProgress) { AgreementTail = words };
            _state.Audio.RemoveRange(0, discard);
            _state.Offset += discard;
            var newBase = _state.Offset / SampleRate;
            // The matcher needs an anchor's complete onset in retained PCM. An owned word's end can extend
            // through a pause and straddle this crop even though its onset is gone.
            _state.CommittedAnchors.RemoveAll(anchor => anchor.Start < newBase);
        }
        _state.AlignmentDeferrals = 0;
        if (_state.Recovering)
        {
            _state.Recovering = false;
            _state.RecoveryCandidate = null;
            _onDiagnostic($"ownership_recovered:samples={snapshot.Audio.Length}");
            if (_state.Closed) return;
        }
        _state.Revision++;
        // ponytail: macOS smooths Whisper's visible tail (WhisperPreviewPresentation). Dictation delivery
        // drops the tail, and Windows Whisper has no word timings, so the raw tail is published.
        _state.LastPresentedTail = tail;
        _onUpdate(new BufferedTranscriptionUpdate(_state.CommittedText, tail, _state.Revision));
        if (snapshot.Final && !_state.Closed)
        {
            if (_state.DecodedThroughSample != _state.ReceivedSamples)
            {
                Close(ReuseFallbackReason.IncompleteCoverage);
                return;
            }
            _state.FinalText = _state.CommittedText + tail;
            Close();
        }
    }

    /// <summary>
    /// Locate already-owned words using ordered, timed anchors, not textual overlap removal. Refresh
    /// their positions on every decode so small timing jitter cannot push an owned repeated word back
    /// into the draft tail.
    /// </summary>
    private int UnownedStart(List<BufferedTranscriptionWord> words, double @base)
    {
        var drift = _configuration.MaximumTimestampDrift;
        if (_state.CommittedAnchors.Count == 0)
        {
            if (_state.OwnedUntil is not { } ownedUntil) return 0;
            // Silent ownership is half-open: a zero-duration word exactly at its right edge was not
            // committed and must survive the next roll.
            return words.TakeWhile(word => (word.Start + word.End) / 2 < ownedUntil).Count();
        }
        var anchors = _state.CommittedAnchors.Where(anchor => anchor.Start >= @base).ToList();
        if (anchors.Count == 0) throw new AlignmentRecovery(RecoveryReason.MissingAnchor);
        int? matchedIndex = null;
        // Earlier committed words may be revised by the model. Try the longest retained suffix first,
        // never resolving multiple candidates by proximity. Match onsets: a decoder can extend an owned
        // word's end across silence when more context arrives.
        for (var length = anchors.Count; length >= 1; length--)
        {
            var suffix = anchors.TakeLast(length).ToList();
            var candidates = new List<int>();
            for (var index = 0; index < words.Count; index++)
            {
                if (index + 1 < length) continue;
                var valid = true;
                for (var k = 0; k < length; k++)
                {
                    var candidate = words[index + 1 - length + k];
                    if (AnchorIdentity(suffix[k].Text) != AnchorIdentity(candidate.Text) ||
                        Math.Abs(suffix[k].Start - candidate.Start) > drift) { valid = false; break; }
                }
                if (valid) candidates.Add(index);
            }
            if (candidates.Count > 1) throw new AlignmentRecovery(RecoveryReason.AmbiguousAnchor);
            if (candidates.Count == 1) { matchedIndex = candidates[0]; break; }
        }
        if (matchedIndex is null && anchors.Count >= 2 && _state.FollowingAnchor is { } following)
        {
            // More context can expand or shorten an owned word's onset span without moving its end or the
            // next word. Require the preceding lexical/timed context and the following seam; an end
            // timestamp alone cannot own a repeat.
            for (var length = anchors.Count; length >= 2; length--)
            {
                var suffix = anchors.TakeLast(length).ToList();
                var last = suffix[^1];
                var candidates = new List<int>();
                for (var index = 0; index < words.Count; index++)
                {
                    if (index + 1 < length || index + 1 >= words.Count) continue;
                    var current = words[index];
                    var next = words[index + 1];
                    if (AnchorIdentity(current.Text) != AnchorIdentity(last.Text) ||
                        Math.Abs(current.End - last.End) > drift ||
                        // A pause may move from the preceding word's end to this onset.
                        current.Start < words[index - 1].End - drift ||
                        AnchorIdentity(next.Text) != AnchorIdentity(following.Text) ||
                        Math.Abs(next.Start - following.Start) > drift) continue;
                    var valid = true;
                    for (var k = 0; k < length - 1; k++)
                    {
                        var word = words[index + 1 - length + k];
                        if (AnchorIdentity(suffix[k].Text) != AnchorIdentity(word.Text) ||
                            Math.Abs(suffix[k].Start - word.Start) > drift) { valid = false; break; }
                    }
                    if (valid) candidates.Add(index);
                }
                if (candidates.Count > 1) throw new AlignmentRecovery(RecoveryReason.AmbiguousAnchor);
                if (candidates.Count == 1) { matchedIndex = candidates[0]; break; }
            }
        }
        if (matchedIndex is null && _configuration.LexicalAgreement)
        {
            matchedIndex = RevisedBoundary(words, anchors, _state.FollowingAnchor);
            if (matchedIndex is not null)
            {
                _state.ReuseQuality.RecordConcern(ReuseFallbackReason.CommittedTranscriptChanged);
                _onDiagnostic("ownership_realigned:edited_context");
            }
        }
        if (matchedIndex is null)
        {
            // A permanently revised lexical anchor can still identify the same audio seam. Require a unique
            // ordered onset alignment and agreement on its NEW spelling in two different, growing hypotheses.
            for (var length = anchors.Count; length >= 1; length--)
            {
                var suffix = anchors.TakeLast(length).ToList();
                var candidates = new List<int>();
                for (var index = 0; index < words.Count; index++)
                {
                    if (index + 1 < length) continue;
                    var valid = true;
                    for (var k = 0; k < length; k++)
                        if (Math.Abs(suffix[k].Start - words[index + 1 - length + k].Start) > drift) { valid = false; break; }
                    if (valid) candidates.Add(index);
                }
                if (candidates.Count > 1) throw new AlignmentRecovery(RecoveryReason.AmbiguousAnchor);
                if (candidates.Count == 0) continue;
                var matched = candidates[0];
                var candidateWords = words.GetRange(matched + 1 - length, length);
                var next = matched + 1 < words.Count ? words[matched + 1] : null;
                if (_state.FollowingAnchor is { } followingAnchor)
                {
                    // A revised owned word must not split into extra tokens that are then mistaken for new
                    // speech (cannot -> can + not).
                    if (next is null || AnchorIdentity(next.Text) != AnchorIdentity(followingAnchor.Text) ||
                        Math.Abs(next.Start - followingAnchor.Start) > drift)
                        throw new AlignmentRecovery(RecoveryReason.ChangedBoundary);
                }
                else if (next is not null && anchors.Count > 0 && next.Start <= anchors[^1].End + drift)
                {
                    throw new AlignmentRecovery(RecoveryReason.ChangedBoundary);
                }
                if (_state.RecoveryCandidate is { } previous && previous.Count == candidateWords.Count &&
                    previous.Zip(candidateWords).All(pair =>
                        string.Equals(pair.First.Text.Trim(), pair.Second.Text.Trim(), StringComparison.Ordinal) &&
                        Math.Abs(pair.First.Start - pair.Second.Start) <= drift))
                {
                    if (suffix.Zip(candidateWords).Any(pair => AnchorIdentity(pair.First.Text) != AnchorIdentity(pair.Second.Text)))
                        _state.ReuseQuality.RecordConcern(ReuseFallbackReason.CommittedTranscriptChanged);
                    matchedIndex = matched;
                    break;
                }
                throw new AlignmentRecovery(RecoveryReason.ChangedAnchor) { Candidate = candidateWords };
            }
        }
        if (matchedIndex is not { } matchedAt) throw new AlignmentRecovery(RecoveryReason.MissingAnchor);
        var lastCurrent = words[matchedAt];
        // A shorter suffix can legitimately recover live ownership while older committed wording changed.
        // Preserve frozen text under the streaming policy and retain the revision as quality telemetry.
        if (matchedAt + 1 < anchors.Count)
        {
            _state.ReuseQuality.RecordConcern(ReuseFallbackReason.CommittedTranscriptChanged);
        }
        else
        {
            var currentOwned = words.GetRange(matchedAt + 1 - anchors.Count, anchors.Count);
            if (!anchors.Zip(currentOwned).All(pair => AnchorIdentity(pair.First.Text) == AnchorIdentity(pair.Second.Text)))
                _state.ReuseQuality.RecordConcern(ReuseFallbackReason.CommittedTranscriptChanged);
        }
        _state.CommittedAnchors = [.. words.Take(matchedAt + 1).TakeLast(4)];
        _state.OwnedUntil = OwnershipBoundary(lastCurrent, matchedAt + 1 < words.Count ? words[matchedAt + 1] : null);
        return matchedAt + 1;
    }

    /// <summary>
    /// Match the occurrence around a revised endpoint without requiring equal token counts. A timed
    /// longest common subsequence tolerates insertions and deletions inside the already-owned context.
    /// Two unchanged context words and a unique overlapping endpoint are needed.
    /// </summary>
    private int? RevisedBoundary(List<BufferedTranscriptionWord> words, List<BufferedTranscriptionWord> anchors,
        BufferedTranscriptionWord? following)
    {
        var drift = _configuration.MaximumTimestampDrift;
        if (anchors.Count < 3) return null;
        var last = anchors[^1];
        if (!(last.End > last.Start)) return null;
        var context = anchors.Take(anchors.Count - 1).ToList();
        // scores[j] is the longest ordered, lexical/timed match between the context and words before j.
        var scores = new int[words.Count + 1];
        foreach (var anchor in context)
        {
            var next = new int[words.Count + 1];
            for (var j = 0; j < words.Count; j++)
            {
                next[j + 1] = AnchorIdentity(anchor.Text) == AnchorIdentity(words[j].Text) && Math.Abs(anchor.Start - words[j].Start) <= drift
                    ? Math.Max(scores[j] + 1, Math.Max(scores[j + 1], next[j]))
                    : Math.Max(scores[j + 1], next[j]);
            }
            scores = next;
        }
        var oldCenter = (last.Start + last.End) / 2;
        var candidates = Enumerable.Range(0, words.Count).Where(index =>
        {
            var current = words[index];
            var center = (current.Start + current.End) / 2;
            // Mutual interior overlap excludes a neighboring short token and partial split tokens.
            return scores[index] >= 2 && current.End > current.Start &&
                current.Start < oldCenter && oldCenter < current.End &&
                last.Start < center && center < last.End;
        }).ToList();
        // Do not choose the closest timestamp or the highest-confidence word when repeated/overlapping
        // occurrences support multiple boundaries.
        if (candidates.Count != 1) return null;
        var found = candidates[0];
        var after = found + 1 < words.Count ? words[found + 1] : null;
        if (following is not null)
        {
            if (after is null || AnchorIdentity(after.Text) != AnchorIdentity(following.Text) ||
                Math.Abs(after.Start - following.Start) > drift) return null;
        }
        // Without an independently known following word, a token still inside the old endpoint's span may
        // be the rest of a split owned word. Do not emit it again as new speech.
        if (after is not null && (after.Start < words[found].End || (following is null && after.Start < last.End))) return null;
        return found;
    }

    private int RecoveryWindowLimit => Math.Min(_configuration.MaximumRecoveryWindowSamples, _configuration.MaximumBufferedSamples);

    private void DeferAlignment(AlignmentRecovery recovery, Snapshot snapshot)
    {
        _state.LastDecodedEnd = snapshot.Offset + snapshot.Audio.Length;
        // A valid hypothesis that cannot yet advance the crop is still evidence for the next growing
        // window. Invalid/ambiguous alignments are not.
        _state.PreviousTail = _configuration.LexicalAgreement ? recovery.AgreementTail ?? [] : [];
        _state.PreviousTailReuseAgreement = [.. Enumerable.Repeat(false, _state.PreviousTail.Count)];
        _state.Recovering = true;
        _state.RecoveryCandidate = recovery.Candidate;
        var eligibleAnchors = _state.CommittedAnchors.Count(anchor => anchor.Start >= snapshot.Offset / SampleRate);
        _onDiagnostic($"ownership_recovery:{RawValue(recovery.Reason)}:samples={snapshot.Audio.Length}:originSamples={snapshot.Offset}:" +
            $"anchorCount={_state.CommittedAnchors.Count}:eligibleAnchorCount={eligibleAnchors}");
        if (_state.Closed) return;
        _state.AlignmentDeferrals++;
        if ((_state.AlignmentDeferrals >= 2 || snapshot.Final || snapshot.Audio.Length >= RecoveryWindowLimit) &&
            BeginContinuity(snapshot.Offset + snapshot.Audio.Length))
            return;
        if (snapshot.Final || snapshot.Audio.Length >= RecoveryWindowLimit)
        {
            _onDiagnostic($"ownership_recovery_exhausted:{(snapshot.Final ? "final" : "window_limit")}");
            if (_state.Closed) return;
            Fail(new SessionFailure(FailureKind.OwnershipUnresolved));
            return;
        }
        // Every accepted attempt has a revision, including deferred observations. Consumers keep their
        // visible prefix/draft and continue accepting audio.
        _state.Revision++;
        _onUpdate(new BufferedTranscriptionUpdate(_state.CommittedText, _state.LastPresentedTail, _state.Revision));
    }

    // A small non-overlapping segment is the continuity unit, not an utterance. It adds no punctuation or
    // endpoint decisions and runs through pauses.
    private int ContinuityChunkSamples => Math.Min(_configuration.WindowSamples, 2 * _configuration.UpdateSamples);

    private bool BeginContinuity(int snapshotEnd)
    {
        if (_decodeWithoutTimings is null || _state.Closed) return false;
        // Use the fence captured when text was committed, not a later guess at where revised words ended.
        // Never skip unowned audio to catch up.
        var frontier = Math.Max(_state.Offset, Math.Min(_state.CommittedAudioThrough, snapshotEnd));
        var discard = frontier - _state.Offset;
        if (discard > _state.Audio.Count) return false;
        _state.Audio.RemoveRange(0, discard);
        _state.Offset = frontier;
        _state.DecodedThroughSample = frontier;
        _state.LastDecodedEnd = frontier;
        _state.Continuity = true;
        _state.Recovering = false;
        _state.CommittedAnchors = [];
        _state.PreviousTail = [];
        _state.PreviousTailReuseAgreement = [];
        _state.FollowingAnchor = null;
        _state.RecoveryCandidate = null;
        // Retain the best recognized hypothesis for failure/timeout recovery until disjoint decoding has
        // replaced all audio represented by that hypothesis.
        if (_state.LastPublishedTail.Length > 0)
            _state.ContinuityRecovery = (_state.CommittedText + _state.LastPublishedTail, _state.LastPublishedThroughSample);
        _state.LastPublishedTail = "";
        _state.LastPresentedTail = "";
        _state.ReuseQuality.RecordConcern(ReuseFallbackReason.CommittedTranscriptChanged);
        _onDiagnostic($"continuity_started:frontierSamples={frontier}:retainedSamples={_state.Audio.Count}");
        return true;
    }

    private void AcceptContinuity(string text, Snapshot snapshot)
    {
        // The decoder saw exactly this disjoint PCM segment. Word timings and earlier lexical revisions
        // cannot gate its delivery or sample coverage.
        var trimmed = text.Trim();
        if (trimmed.Length > 0)
            _state.CommittedText += Join([new BufferedTranscriptionWord(trimmed, 0, 0)], _state.CommittedText);
        else
            _state.ReuseQuality.RecordConcern(ReuseFallbackReason.UnverifiedEmptyAudio);
        _state.Audio.RemoveRange(0, snapshot.Audio.Length);
        _state.Offset = snapshot.Offset + snapshot.Audio.Length;
        _state.CommittedAudioThrough = _state.Offset;
        if (_state.ContinuityRecovery is { } recovery && _state.Offset >= recovery.ThroughSample)
            _state.ContinuityRecovery = null;
        _state.LastDecodedEnd = _state.Offset;
        _state.DecodedThroughSample = _state.Offset;
        _state.LastCommitSnapshotEnd = _state.Offset;
        _state.Revision++;
        _onDiagnostic($"continuity_chunk:samples={snapshot.Audio.Length}:throughSamples={_state.Offset}:characters={trimmed.Length}");
        if (_state.Closed) return;
        _onUpdate(new BufferedTranscriptionUpdate(_state.CommittedText, "", _state.Revision));
        if (snapshot.Final && !_state.Closed)
        {
            if (_state.DecodedThroughSample != _state.ReceivedSamples) { Close(ReuseFallbackReason.IncompleteCoverage); return; }
            _state.FinalText = _state.CommittedText;
            Close();
        }
    }

    /// <summary>
    /// Quality evidence is independent of the live prefix-commit policy. An earlier revised word must not
    /// erase agreement of later timed occurrences. Ambiguous repeats never gain evidence by choosing the
    /// nearest candidate.
    /// </summary>
    private List<bool> ReuseAgreement(List<BufferedTranscriptionWord> words, bool freshCoverage)
    {
        var previousIndex = -1;
        return words.Select(word =>
        {
            var matches = Enumerable.Range(0, _state.PreviousTail.Count).Where(index =>
            {
                var previous = _state.PreviousTail[index];
                // The same word can absorb trailing silence as context grows. Its unique onset identifies
                // the occurrence; positive speech intervals must still overlap.
                var overlaps = previous.Start == previous.End || word.Start == word.End ||
                    Math.Max(previous.Start, word.Start) < Math.Min(previous.End, word.End);
                return AnchorIdentity(previous.Text) == AnchorIdentity(word.Text) &&
                    Math.Abs(previous.Start - word.Start) <= _configuration.MaximumTimestampDrift && overlaps;
            }).ToList();
            if (matches.Count != 1 || matches[0] <= previousIndex) return false;
            var index = matches[0];
            previousIndex = index;
            // An identical stop snapshot may retain prior independent evidence, but two decodes of the
            // same PCM cannot create that evidence.
            return freshCoverage || (index < _state.PreviousTailReuseAgreement.Count && _state.PreviousTailReuseAgreement[index]);
        }).ToList();
    }

    private void RecordReuseQuality(List<BufferedTranscriptionWord> words, List<bool> agreement)
    {
        var quality = _state.ReuseQuality;
        var supportedSpan = (_configuration.RightContextSamples + _configuration.UpdateSamples) / SampleRate;
        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            var agreed = agreement[index];
            quality.CommittedWords++;
            if (agreed)
            {
                quality.AgreedWords++;
                quality.CurrentUnagreedStart = null;
                quality.CurrentUnagreedEnd = null;
            }
            else
            {
                quality.UnagreedWords++;
                // Carry unsupported runs across commitment batches; only an independently agreed word resets
                // this provisional bound.
                quality.CurrentUnagreedStart = Math.Min(quality.CurrentUnagreedStart ?? word.Start, word.Start);
                quality.CurrentUnagreedEnd = Math.Max(quality.CurrentUnagreedEnd ?? word.End, word.End);
                var span = quality.CurrentUnagreedEnd.Value - quality.CurrentUnagreedStart.Value;
                quality.MaximumUnagreedSpan = Math.Max(quality.MaximumUnagreedSpan, span);
                if (span > supportedSpan) quality.RecordConcern(ReuseFallbackReason.UnagreedContextTooLong);
                if (word.Start == word.End && HasAlphanumeric(word.Text))
                    quality.RecordConcern(ReuseFallbackReason.UnsupportedWordTiming);
            }
            if (word.Confidence is not { } confidence)
            {
                quality.MissingConfidence++;
                quality.RecordConcern(ReuseFallbackReason.MissingConfidence);
                continue;
            }
            if (!float.IsFinite(confidence) || confidence is < 0 or > 1)
            {
                quality.InvalidConfidence++;
                quality.RecordConcern(ReuseFallbackReason.InvalidConfidence);
                continue;
            }
            quality.MinimumConfidence = Math.Min(quality.MinimumConfidence ?? confidence, confidence);
            var floor = agreed ? ReusePolicy.AgreedConfidenceFloor : ReusePolicy.UnagreedConfidenceFloor;
            if (confidence < floor)
            {
                quality.LowConfidence++;
                var evidence = FormattableString.Invariant($"start={word.Start},end={word.End},confidence={confidence},agreed={Lower(agreed)}");
                quality.FirstLowConfidenceEvidence ??= evidence;
                quality.LastLowConfidenceEvidence = evidence;
                quality.RecordConcern(ReuseFallbackReason.LowConfidence);
            }
        }
    }

    private bool WordsAgree(BufferedTranscriptionWord previous, BufferedTranscriptionWord current)
    {
        var sameWord = _configuration.LexicalAgreement
            ? AgreementIdentity(previous.Text) == AgreementIdentity(current.Text)
            : string.Equals(previous.Text.Trim(), current.Text.Trim(), StringComparison.Ordinal);
        return sameWord &&
            Math.Abs(previous.Start - current.Start) <= _configuration.MaximumTimestampDrift &&
            Math.Abs(previous.End - current.End) <= _configuration.MaximumTimestampDrift;
    }

    private int MaturePrefixWithStableSeam(List<BufferedTranscriptionWord> words, int matureCount)
    {
        if (!_configuration.LexicalAgreement || matureCount <= 0 || matureCount >= words.Count ||
            matureCount >= _state.PreviousTail.Count) return 0;
        // Compare exactly the endpoint and next position, never search for a nearby repeated word. Both
        // observations must contain real timed words; standalone punctuation cannot establish the seam.
        for (var index = matureCount - 1; index <= matureCount; index++)
        {
            var previous = _state.PreviousTail[index];
            var current = words[index];
            if (!(previous.End > previous.Start) || !(current.End > current.Start) ||
                !HasAlphanumeric(previous.Text) || !HasAlphanumeric(current.Text) || !WordsAgree(previous, current)) return 0;
        }
        return matureCount;
    }

    internal static string AgreementIdentity(string text)
    {
        var trimmed = text.Trim();
        // A hyphen can be a numeric sign. Do not treat '-1' and '1' as cosmetic variants when freezing new
        // speech. Internal punctuation (can't/cant, decimal points) and standalone punctuation survive.
        static bool IsEdge(Rune rune) =>
            Rune.IsWhiteSpace(rune) || (IsPunctuation(rune) && "-#%٪‰‱'’′″".IndexOf(rune.ToString(), StringComparison.Ordinal) < 0);
        var runes = trimmed.EnumerateRunes().ToList();
        var start = 0;
        var end = runes.Count;
        while (start < end && IsEdge(runes[start]))
        {
            if ((runes[start].Value == '.' || runes[start].Value == ',') && start + 1 < end && Rune.IsNumber(runes[start + 1])) break;
            start++;
        }
        while (end > start && IsEdge(runes[end - 1])) end--;
        if (start >= end) return trimmed;
        return string.Concat(runes.Skip(start).Take(end - start).Select(rune => rune.ToString())).ToLowerInvariant();
    }

    internal static string AnchorIdentity(string text)
    {
        // Already committed punctuation/case cannot be rewritten. Ignore those differences only when
        // finding its timed occurrence in a fresh decode. Trim the union at the edges only.
        var trimmed = text.Trim();
        var runes = trimmed.EnumerateRunes().ToList();
        var start = 0;
        var end = runes.Count;
        while (start < end && (IsPunctuation(runes[start]) || Rune.IsWhiteSpace(runes[start]))) start++;
        while (end > start && (IsPunctuation(runes[end - 1]) || Rune.IsWhiteSpace(runes[end - 1]))) end--;
        if (start >= end) return trimmed;
        return string.Concat(runes.Skip(start).Take(end - start).Select(rune => rune.ToString())).ToLowerInvariant();
    }

    private static double OwnershipBoundary(BufferedTranscriptionWord last, BufferedTranscriptionWord? next)
    {
        if (next is null) return last.End;
        if (next.Start >= last.End) return (last.End + next.Start) / 2;
        var lastCenter = (last.Start + last.End) / 2;
        var nextCenter = (next.Start + next.End) / 2;
        if (!(nextCenter > lastCenter)) throw new AlignmentRecovery(RecoveryReason.InseparableTimings);
        return (lastCenter + nextCenter) / 2;
    }

    internal static string Join(IEnumerable<BufferedTranscriptionWord> words, string prefix)
    {
        var text = string.Concat(words.Select(word => word.Text));
        // A fresh decoder window can omit the leading word space. Preserve punctuation and scripts
        // normally written without word separators.
        if (text.Length > 0 && prefix.Length > 0)
        {
            var first = Rune.GetRuneAt(text, 0);
            var last = LastRune(prefix);
            if (UsesWordSpaces(first) && (UsesWordSpaces(last) || EndsSpacedSentence(prefix))) text = " " + text;
        }
        return text;
    }

    private static bool EndsSpacedSentence(string text)
    {
        if (text.Length == 0) return false;
        var last = LastRune(text);
        if (".,!?;:…)]}\"'»”’،؛؟".IndexOf(last.ToString(), StringComparison.Ordinal) < 0) return false;
        var runes = text.EnumerateRunes().Reverse();
        foreach (var rune in runes)
            if (!IsPunctuation(rune)) return UsesWordSpaces(rune);
        return false;
    }

    private static bool UsesWordSpaces(Rune rune)
    {
        if (!IsAlphanumeric(rune)) return false;
        // Han, kana, Thai, Lao, Khmer and Myanmar retain decoder spacing. Korean uses word spaces,
        // including between independently decoded clips.
        var value = rune.Value;
        return !(value is >= 0x2E80 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF
            or >= 0x0E00 and <= 0x0EFF or >= 0x1000 and <= 0x109F or >= 0x1780 and <= 0x17FF);
    }

    private static Rune LastRune(string text) =>
        Rune.DecodeLastFromUtf16(text, out var rune, out _) == System.Buffers.OperationStatus.Done ? rune : Rune.ReplacementChar;

    /// <summary>Foundation CharacterSet.alphanumerics: letters, marks and numbers.</summary>
    internal static bool IsAlphanumeric(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark
        or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.DecimalDigitNumber
        or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;

    internal static bool IsPunctuation(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
        or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
        or UnicodeCategory.OtherPunctuation;

    private static bool HasAlphanumeric(string text) => text.EnumerateRunes().Any(IsAlphanumeric);

    private static double Rms(float[] audio)
    {
        double sum = 0;
        foreach (var sample in audio) sum += (double)sample * sample;
        return Math.Sqrt(sum / Math.Max(1, audio.Length));
    }

    private static string Lower(bool value) => value ? "true" : "false";

    private void Fail(Exception error)
    {
        // Publish failure before releasing finish waiters so a caller cannot mistake a failed drain for a
        // successfully completed preview.
        _state.Closed = true;
        _state.ReuseTerminalReason = ReuseFallbackReason.TranscriptionFailed;
        _state.FinalText = null;
        // A failure handler may reentrantly cancel. Keep these waiters out of Close() until that handler
        // has completely delivered failure.
        var waiters = _waiters.ToList();
        _waiters.Clear();
        _onFailure(error);
        Close();
        foreach (var waiter in waiters) waiter.TrySetResult();
    }

    private void Close(ReuseFallbackReason? reason = null)
    {
        if (reason is { } terminal)
        {
            _state.ReuseTerminalReason = terminal;
            _state.FinalText = null;
        }
        _state.Closed = true;
        _state.Running = false;
        var drain = _drainCancellation;
        _drainCancellation = null;
        // Cancellation propagates to a backend waiting for its decode permit. Do not await it: an
        // uncooperative decoder must not delay finish.
        try { drain?.Cancel(); } catch (ObjectDisposedException) { }
        _state.Audio.Clear();
        var waiters = _waiters.ToList();
        _waiters.Clear();
        foreach (var waiter in waiters) waiter.TrySetResult();
    }
}
