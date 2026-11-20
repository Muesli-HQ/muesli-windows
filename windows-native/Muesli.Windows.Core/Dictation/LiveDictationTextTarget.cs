using System.Globalization;
using System.Text;

namespace Muesli.Windows.Services;

// Port of macOS LiveDictationTextTarget.swift and LiveDictationKeyboardInput.swift (Muesli-HQ/muesli
// 2f10b23d), keyboard transport only. Windows has no AXSelectedText-style write, so live drafts and final
// cleanup are Unicode keystroke bursts, each acknowledged by UI Automation readback. Offsets are UTF-16.

/// <summary>What one hands-free dictation needs from its cursor target.</summary>
public interface ILiveDictationTarget
{
    Action? OnSuspended { get; set; }
    bool IsSuspended { get; }
    bool HasInsertedText { get; }
    bool HasAttemptedWrite { get; }
    Task<bool> UpdateAsync(string committedText, string tail);
    Task<bool> FinishAsync(string text);
    void Cancel();
}

/// <summary>
/// Windows-only fallback (no macOS equivalent) for fields UI Automation cannot read back, such as
/// terminals and some Chromium/Electron editors. Types only committed text, appends only, never deletes,
/// never sends control keys (so never Enter), and stops for good the moment the transcript revises
/// text it already typed or the target window loses the foreground. Cleanup that rewrites typed text
/// goes to history only.
/// </summary>
public sealed class AppendOnlyLiveDictationTarget(Func<bool> isForeground, Func<string, bool> send,
    Action<string>? onDiagnostic = null) : ILiveDictationTarget
{
    private readonly SemaphoreSlim _operation = new(1, 1);
    private string _typed = "";
    private volatile bool _closed;
    private volatile bool _suspended;

    public Action? OnSuspended { get; set; }
    public bool IsSuspended => _suspended;
    public bool HasInsertedText { get; private set; }
    public bool HasAttemptedWrite { get; private set; }

    public Task<bool> UpdateAsync(string committedText, string tail) => AppendAsync(committedText, finishing: false);

    public async Task<bool> FinishAsync(string text)
    {
        var delivered = await AppendAsync(text, finishing: true).ConfigureAwait(false);
        Cancel();
        return delivered;
    }

    private async Task<bool> AppendAsync(string text, bool finishing)
    {
        if (_closed || _suspended) return false;
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_closed || _suspended) return false;
            // Typed text cannot be revised blind. Mid-stream that ends live updates; at finish the
            // final transcript is kept in history.
            if (!text.StartsWith(_typed, StringComparison.Ordinal))
                return finishing ? Report("final_differs") : Suspend("committed_prefix_changed");
            var delta = text[_typed.Length..];
            if (delta.Length == 0) return true;
            if (!LiveDictationKeyboardInput.IsSupported(delta)) return Suspend("keyboard_text_unsupported");
            if (!isForeground()) return Suspend("focus_changed");
            HasAttemptedWrite = true;
            if (!send(delta)) return Suspend("keyboard_dispatch_failed");
            _typed = text;
            HasInsertedText = true;
            return true;
        }
        finally
        {
            _operation.Release();
        }
    }

    private bool Report(string reason)
    {
        onDiagnostic?.Invoke(reason);
        return false;
    }

    private bool Suspend(string reason)
    {
        var first = !_suspended;
        _suspended = true;
        Report(reason);
        if (first) OnSuspended?.Invoke();
        return false;
    }

    public void Cancel() => _closed = true;
}

public readonly record struct LiveTextRange(int Location, int Length)
{
    public int End => Location + Length;
}

public sealed record LiveDictationSnapshot(string Text, LiveTextRange Selection);

public readonly record struct LiveDictationFocusStatus(LiveDictationFocusStatus.Kinds Kind, string Reason = "")
{
    public enum Kinds { Focused, Unavailable, Departed }

    public static LiveDictationFocusStatus Focused => new(Kinds.Focused);
    public static LiveDictationFocusStatus Unavailable(string reason) => new(Kinds.Unavailable, reason);
    public static LiveDictationFocusStatus Departed(string reason) => new(Kinds.Departed, reason);
}

/// <summary>The transport is selected before any write and never changed on failure.</summary>
public sealed class LiveDictationTextAccess
{
    public required Func<Task<LiveDictationSnapshot?>> Snapshot { get; init; }
    public required Func<Task<LiveDictationFocusStatus>> FocusStatus { get; init; }
    /// <summary>Selects the range only while the field still shows the given snapshot.</summary>
    public required Func<LiveTextRange, LiveDictationSnapshot, Task<bool>> Select { get; init; }
    /// <summary>Cheap foreground-process identity, checked around every lease decision.</summary>
    public required Func<LiveDictationFocusStatus> CurrentProcessStatus { get; init; }
    /// <summary>
    /// Posts one acknowledgment group of at most eight graphemes. Dispatch must call validate immediately
    /// before posting and send nothing when it fails. True is not proof of insertion.
    /// </summary>
    public required Func<string, Func<bool>, Action<string>, bool> SendBurst { get; init; }
    /// <summary>A synchronous read used by validate, immediately before dispatch.</summary>
    public required Func<LiveDictationSnapshot?> SnapshotNow { get; init; }
    public Action? StopObserving { get; init; }
}

public static class LiveDictationKeyboardInput
{
    public const int MaximumBurstGraphemes = 8;

    /// <summary>
    /// Restrict this transport to text, never commands or field navigation. No characters are normalized,
    /// stripped, or silently replaced.
    /// </summary>
    public static bool IsSupported(string text) => text.EnumerateRunes().All(rune =>
        Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
        && rune.Value is not (>= 0xF700 and <= 0xF8FF));

    /// <summary>
    /// Partition the text into separately acknowledged groups. Word separators can trigger editor-side
    /// processing; never post the next word until the separator's exact text and caret have been read back.
    /// The first grapheme stays separate. Long words and unspaced scripts stay bounded without splitting a
    /// grapheme.
    /// </summary>
    public static List<string>? AcknowledgmentGroups(string text)
    {
        if (!IsSupported(text)) return null;
        var graphemes = LiveDictationTextTarget.Graphemes(text);
        if (graphemes.Count == 0) return [];
        var groups = new List<string> { graphemes[0] };
        var word = new StringBuilder();
        var wordCount = 0;
        void FlushWord()
        {
            if (wordCount == 0) return;
            groups.Add(word.ToString());
            word.Clear();
            wordCount = 0;
        }
        foreach (var grapheme in graphemes.Skip(1))
        {
            if (grapheme.EnumerateRunes().Any(rune => Rune.IsWhiteSpace(rune) || BufferedTranscriptionSession.IsPunctuation(rune)))
            {
                FlushWord();
                groups.Add(grapheme);
            }
            else
            {
                word.Append(grapheme);
                if (++wordCount == MaximumBurstGraphemes) FlushWord();
            }
        }
        FlushWord();
        return groups;
    }
}

/// <summary>
/// Owns one revisable range for one hands-free session. Never writes the field's value or types deletion
/// keys. Unsupported editors use the caller's history path instead of an unverified replacement.
/// </summary>
public sealed class LiveDictationTextTarget : ILiveDictationTarget
{
    public sealed record ReadbackTiming
    {
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(0.5);
        public TimeSpan AvailabilityTimeout { get; init; } = TimeSpan.FromSeconds(2);
        public Func<TimeSpan> Now { get; init; } = () => TimeSpan.FromTicks(Environment.TickCount64 * TimeSpan.TicksPerMillisecond);
        public Func<Task> Wait { get; init; } = () => Task.Delay(10);
    }

    private static LiveDictationTextTarget? s_active;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly LiveDictationTextAccess _access;
    private readonly ReadbackTiming _timing;
    private readonly Action<string> _onDiagnostic;
    private LiveDictationSnapshot _expected;
    private LiveTextRange _ownedRange;
    private volatile bool _closed;
    private volatile bool _finishing;
    private volatile bool _suspended;
    private volatile bool _paused;
    private bool _mutating;
    private bool _pendingTextValidation;
    private Task? _notificationValidation;
    private string _frozenCommittedText = "";
    private TimeSpan _availabilityWaitUsed;
    private int _availabilityRevision;

    /// <summary>Permanent ownership loss only; transient UIA pauses must not announce a stop.</summary>
    public Action? OnSuspended { get; set; }
    public bool IsSuspended => _suspended;
    public bool IsPaused => _paused;
    public bool HasInsertedText { get; private set; }
    /// <summary>
    /// Even an unsuccessful dispatch may have reached the other process. Callers must never retry by
    /// normal paste after this becomes true.
    /// </summary>
    public bool HasAttemptedWrite { get; private set; }
    public string? ApplicationName { get; init; }

    private LiveDictationTextTarget(LiveDictationTextAccess access, LiveDictationSnapshot snapshot,
        Action<string>? onDiagnostic, ReadbackTiming? timing)
    {
        _access = access;
        _expected = snapshot;
        _ownedRange = snapshot.Selection;
        _onDiagnostic = onDiagnostic ?? (_ => { });
        _timing = timing ?? new ReadbackTiming();
    }

    public static LiveDictationTextTarget? Create(LiveDictationTextAccess access, LiveDictationSnapshot snapshot,
        Action<string>? onDiagnostic = null, ReadbackTiming? timing = null, string? applicationName = null)
    {
        Interlocked.Exchange(ref s_active, null)?.Cancel();
        if (!IsValid(snapshot.Selection, snapshot.Text)) return null;
        var target = new LiveDictationTextTarget(access, snapshot, onDiagnostic, timing) { ApplicationName = applicationName };
        Volatile.Write(ref s_active, target);
        return target;
    }

    /// <summary>
    /// A healthy preview may be deferred when it would rewrite too much text. Commitments advance only when
    /// their complete text has been acknowledged.
    /// </summary>
    public async Task<bool> UpdateAsync(string committedText, string tail)
    {
        if (_finishing || !await AcquireOperationAsync().ConfigureAwait(false)) return false;
        try
        {
            if (!committedText.StartsWith(_frozenCommittedText, StringComparison.Ordinal))
                return Suspend("committed_prefix_changed");
            var text = committedText + tail;
            var boundary = committedText.Length;
            var offset = 0;
            var boundaries = new HashSet<int> { 0 };
            foreach (var grapheme in Graphemes(text)) boundaries.Add(offset += grapheme.Length);
            if (!boundaries.Contains(boundary)) return Suspend("committed_boundary_invalid");
            var accepted = await PerformUpdateAsync(text, _frozenCommittedText.Length, maximumRewrittenGraphemes: 80).ConfigureAwait(false);
            var acknowledged = _expected.Text.Substring(_ownedRange.Location, _ownedRange.Length);
            if (accepted && acknowledged == text) _frozenCommittedText = committedText;
            return accepted;
        }
        finally
        {
            ReleaseOperation();
        }
    }

    public async Task<bool> FinishAsync(string text)
    {
        if (_closed || _finishing) return false;
        _finishing = true;
        if (!await AcquireOperationAsync().ConfigureAwait(false)) return false;
        bool delivered;
        try
        {
            // ponytail: macOS keyboard/paste transports finish with a guarded clipboard paste. Windows'
            // existing delivery prefers Unicode input over the clipboard, so cleanup retypes from the first
            // differing grapheme. A clipboard ownership port is needed only if long retypes prove too slow.
            delivered = await PerformUpdateAsync(text).ConfigureAwait(false);
        }
        finally
        {
            ReleaseOperation();
        }
        Cancel();
        return delivered;
    }

    private async Task<bool> AcquireOperationAsync()
    {
        if (_closed || _suspended) return false;
        await _operation.WaitAsync().ConfigureAwait(false);
        if (_closed || _suspended)
        {
            _operation.Release();
            return false;
        }
        _availabilityWaitUsed = TimeSpan.Zero;
        return true;
    }

    private void ReleaseOperation() => _operation.Release();

    private async Task<bool> PerformUpdateAsync(string text, int frozenPrefixLength = 0, int? maximumRewrittenGraphemes = null)
    {
        if (!await WaitForCurrentAsync().ConfigureAwait(false)) return _paused && !_closed && !_finishing;
        // Empty provisional hypotheses never erase existing or previously emitted text.
        if (text.Length == 0) return true;
        if (!LiveDictationKeyboardInput.IsSupported(text)) return Suspend("keyboard_text_unsupported");
        var nextText = Replace(_expected.Text, _ownedRange, text);
        if (nextText == _expected.Text) return true;
        var previous = Graphemes(_expected.Text.Substring(_ownedRange.Location, _ownedRange.Length));
        var proposed = Graphemes(text);
        var prefixCount = 0;
        while (prefixCount < Math.Min(previous.Count, proposed.Count) && previous[prefixCount] == proposed[prefixCount]) prefixCount++;
        // Event delivery replaces one retained grapheme instead of sending Delete. Never replace a frozen
        // committed grapheme.
        if (prefixCount == proposed.Count) prefixCount--;
        var prefixLength = proposed.Take(prefixCount).Sum(grapheme => grapheme.Length);
        if (prefixLength < frozenPrefixLength)
        {
            if (maximumRewrittenGraphemes is not null && text.Length == frozenPrefixLength && text == _frozenCommittedText)
            {
                _onDiagnostic("preview_deferred_committed_boundary");
                return true;
            }
            return Suspend("committed_prefix_changed");
        }
        if (maximumRewrittenGraphemes is { } maximum && previous.Count - prefixCount > maximum)
        {
            _onDiagnostic("preview_deferred_rewrite_limit");
            return true;
        }
        var replacement = string.Concat(proposed.Skip(prefixCount));
        // Plan only the changed suffix, so an agreed prefix never splits a burst. Preflight the complete
        // replacement before selecting text.
        var plan = LiveDictationKeyboardInput.AcknowledgmentGroups(replacement);
        if (plan is not { Count: > 0 } || plan.Any(step => step.Length == 0 || Graphemes(step).Count > LiveDictationKeyboardInput.MaximumBurstGraphemes) ||
            string.Concat(plan) != replacement)
            return Suspend("keyboard_text_unsupported");
        HasAttemptedWrite = true;
        lock (_gate) _mutating = true;
        try
        {
            return await PerformKeyboardUpdateAsync(plan, prefixLength).ConfigureAwait(false);
        }
        finally
        {
            bool pending;
            lock (_gate)
            {
                _mutating = false;
                pending = _pendingTextValidation;
                _pendingTextValidation = false;
            }
            if (pending) ScheduleNotificationValidation();
        }
    }

    private async Task<bool> PerformKeyboardUpdateAsync(List<string> plan, int prefixLength)
    {
        var originalBase = _ownedRange.Location;
        var editRange = new LiveTextRange(originalBase + prefixLength, _ownedRange.Length - prefixLength);
        var current = new LiveDictationSnapshot(_expected.Text, editRange);
        // Rewriting an already-correct selection creates an unacknowledgeable mutation: old and desired
        // readback are identical (macOS preparePasteSelection). Dispatch still verifies this snapshot.
        _onDiagnostic($"keyboard_selection:{(current == _expected ? "reused" : "written")}:range={editRange.Location}:{editRange.Length}");
        if (current != _expected)
        {
            if (!await SelectAsync(editRange, _expected).ConfigureAwait(false)) return Suspend("selection_write_failed");
            if (await AwaitReadbackAsync(current, _expected, "selection").ConfigureAwait(false) is null) return false;
        }
        foreach (var step in plan)
        {
            if (await WaitForSnapshotAsync(current).ConfigureAwait(false) is null)
                return _closed ? false : Suspend("keyboard_target_unavailable");
            var next = new LiveDictationSnapshot(Replace(current.Text, current.Selection, step),
                new LiveTextRange(current.Selection.Location + step.Length, 0));
            // The editor can expose intermediate states of an already posted burst. Only these exact prefixes
            // may settle toward the complete acknowledgment.
            var intermediates = new List<LiveDictationSnapshot>();
            var intermediate = "";
            foreach (var grapheme in Graphemes(step).SkipLast(1))
            {
                intermediate += grapheme;
                intermediates.Add(new LiveDictationSnapshot(Replace(current.Text, current.Selection, intermediate),
                    new LiveTextRange(current.Selection.Location + intermediate.Length, 0)));
            }
            var expectedBeforeSend = current;
            var sent = _access.SendBurst(step, () =>
            {
                if (!MutationLeaseIsCurrent()) return false;
                if (_access.SnapshotNow() is not { } snapshot)
                {
                    _onDiagnostic("keyboard_validation_snapshot_unavailable");
                    return false;
                }
                if (snapshot != expectedBeforeSend)
                {
                    _onDiagnostic($"keyboard_validation_snapshot_changed:textMatches={Lower(snapshot.Text == expectedBeforeSend.Text)}," +
                        $"caretMatches={Lower(snapshot.Selection == expectedBeforeSend.Selection)}");
                    return false;
                }
                return true;
            }, failure => _onDiagnostic($"keyboard_dispatch_{failure}"));
            if (!sent) return _closed || _suspended ? false : Suspend("keyboard_dispatch_failed");
            if (await AwaitReadbackAsync(next, current, "keyboard_text", intermediates: intermediates).ConfigureAwait(false) is not { } acknowledged)
                return false;
            HasInsertedText = true;
            // Track acknowledged prefixes so cancellation leaves exactly the text already delivered; later
            // events and queued updates cannot resume it.
            _expected = acknowledged;
            _ownedRange = new LiveTextRange(originalBase, acknowledged.Selection.Location - originalBase);
            current = acknowledged;
        }
        return true;
    }

    /// <summary>
    /// An accepted input can precede the editor's automation tree update. Exact old/new text and caret
    /// components may settle independently. Only a complete desired snapshot succeeds. Polling never
    /// retries a mutation, and no queued operation can run meanwhile.
    /// </summary>
    private async Task<LiveDictationSnapshot?> AwaitReadbackAsync(LiveDictationSnapshot desired, LiveDictationSnapshot stale,
        string stage, IReadOnlyList<LiveDictationSnapshot>? intermediates = null)
    {
        intermediates ??= [];
        var deadline = _timing.Now() + _timing.Timeout;
        var reportedStates = new HashSet<string>();
        while (true)
        {
            var before = _timing.Now();
            var revision = Volatile.Read(ref _availabilityRevision);
            if (await WaitForSnapshotAsync().ConfigureAwait(false) is not { } observed)
            {
                if (!_closed) Suspend($"{stage}_availability_timeout");
                return null;
            }
            if (revision != Volatile.Read(ref _availabilityRevision)) deadline += _timing.Now() - before;
            if (observed == desired) { ResumeAvailability(); return observed; }
            var oldText = observed.Text == stale.Text;
            var newText = observed.Text == desired.Text;
            var oldCaret = observed.Selection == stale.Selection;
            var newCaret = observed.Selection == desired.Selection;
            var intermediateText = intermediates.Any(snapshot => observed.Text == snapshot.Text);
            var intermediateCaret = intermediates.Any(snapshot => observed.Selection == snapshot.Selection);
            var state = $"{(newText ? "new" : oldText ? "old" : "other")}:{(newCaret ? "new" : oldCaret ? "old" : "other")}";
            var timedOut = _timing.Now() >= deadline;
            if ((observed != stale || timedOut) && reportedStates.Add(state))
            {
                var caseOnly = !newText && string.Equals(observed.Text, desired.Text, StringComparison.OrdinalIgnoreCase);
                var trailingNewlineOnly = !newText && observed.Text.TrimEnd('\r', '\n') == desired.Text.TrimEnd('\r', '\n');
                _onDiagnostic($"{stage}_readback_state:text={state.Split(':')[0]},lengths={observed.Text.Length}/{stale.Text.Length}/{desired.Text.Length}," +
                    $"caret={state.Split(':')[1]},ranges={Format(observed.Selection)}/{Format(stale.Selection)}/{Format(desired.Selection)}," +
                    $"caseOnly={Lower(caseOnly)},trailingNewlineOnly={Lower(trailingNewlineOnly)}");
            }
            if (!((oldText || newText || intermediateText) && (oldCaret || newCaret || intermediateCaret)))
            {
                Suspend($"{stage}_readback_changed");
                return null;
            }
            if (timedOut)
            {
                Suspend($"{stage}_readback_timeout");
                return null;
            }
            await _timing.Wait().ConfigureAwait(false);
        }
    }

    private bool MutationLeaseIsCurrent()
    {
        if (_closed || _suspended || !ReferenceEquals(Volatile.Read(ref s_active), this)) return false;
        return HandleFocusStatus(_access.CurrentProcessStatus());
    }

    private async Task<LiveDictationSnapshot?> SnapshotAsync()
    {
        var result = await _access.Snapshot().ConfigureAwait(false);
        return MutationLeaseIsCurrent() ? result : null;
    }

    private async Task<bool> SelectAsync(LiveTextRange range, LiveDictationSnapshot matching)
    {
        if (!MutationLeaseIsCurrent()) return false;
        var success = await _access.Select(range, matching).ConfigureAwait(false);
        return success && MutationLeaseIsCurrent();
    }

    private async Task<bool> MutationIsCurrentAsync()
    {
        if (_closed || _suspended || !ReferenceEquals(Volatile.Read(ref s_active), this)) return false;
        var status = await _access.FocusStatus().ConfigureAwait(false);
        if (!MutationLeaseIsCurrent()) return false;
        return HandleFocusStatus(status);
    }

    private bool HandleFocusStatus(LiveDictationFocusStatus status)
    {
        switch (status.Kind)
        {
            case LiveDictationFocusStatus.Kinds.Focused: return true;
            case LiveDictationFocusStatus.Kinds.Unavailable: PauseAvailability(status.Reason); return false;
            default: return Suspend(status.Reason);
        }
    }

    private void PauseAvailability(string reason)
    {
        Interlocked.Increment(ref _availabilityRevision);
        if (!_paused) _onDiagnostic($"accessibility_paused:{reason}");
        _paused = true;
    }

    private void ResumeAvailability()
    {
        if (_paused) _onDiagnostic("accessibility_resumed");
        _paused = false;
    }

    /// <summary>
    /// No mutation is retried here. An unknown focus/snapshot is read again within one cumulative operation
    /// budget. Every successful verification still requires the captured field and, when supplied, the
    /// exact snapshot.
    /// </summary>
    private async Task<LiveDictationSnapshot?> WaitForSnapshotAsync(LiveDictationSnapshot? required = null)
    {
        while (!_closed)
        {
            var started = _timing.Now();
            if (await MutationIsCurrentAsync().ConfigureAwait(false))
            {
                if (await SnapshotAsync().ConfigureAwait(false) is { } observed && MutationLeaseIsCurrent())
                {
                    if (required is not null && observed != required)
                    {
                        ReportSnapshotMismatch(observed, required, "verification");
                        Suspend("text_or_caret_changed");
                        return null;
                    }
                    if (required is not null) ResumeAvailability();
                    return observed;
                }
                PauseAvailability("snapshot_unavailable");
            }
            if (_closed) return null;
            _availabilityWaitUsed += Max(TimeSpan.Zero, _timing.Now() - started);
            if (_availabilityWaitUsed >= _timing.AvailabilityTimeout)
            {
                _onDiagnostic("accessibility_wait_exhausted");
                return null;
            }
            var waitStarted = _timing.Now();
            await _timing.Wait().ConfigureAwait(false);
            _availabilityWaitUsed += Max(TimeSpan.Zero, _timing.Now() - waitStarted);
        }
        return null;
    }

    private Task<bool> WaitForCurrentAsync() =>
        WaitForSnapshotAsync(_expected).ContinueWith(task => task.Result is not null, TaskScheduler.Default);

    /// <summary>Cancellation leaves already inserted text in place and rejects late work.</summary>
    public void Cancel()
    {
        _closed = true;
        _access.StopObserving?.Invoke();
        Interlocked.CompareExchange(ref s_active, null, this);
    }

    /// <summary>
    /// The editor may report our own text/selection changes while we type. Validate them after the
    /// complete, verified mutation; a real change still suspends.
    /// </summary>
    public void TextTargetDidChange()
    {
        lock (_gate)
        {
            if (_mutating) { _pendingTextValidation = true; return; }
        }
        ScheduleNotificationValidation();
    }

    /// <summary>
    /// Editors may announce focus again for the same field. Revalidate the identity instead of treating
    /// every notification as a departure. An actual departure still permanently revokes us.
    /// </summary>
    public void FocusTargetDidChange()
    {
        if (!_closed) TextTargetDidChange();
    }

    public void FocusTargetDidDepart()
    {
        if (!_closed) Suspend("focus_changed");
    }

    private void ScheduleNotificationValidation()
    {
        lock (_gate)
        {
            if (_closed || _notificationValidation is not null) return;
            _notificationValidation = Task.Run(async () =>
            {
                try
                {
                    if (!await AcquireOperationAsync().ConfigureAwait(false)) return;
                    try { await ValidateCurrentAsync().ConfigureAwait(false); }
                    finally { ReleaseOperation(); }
                }
                finally
                {
                    lock (_gate) _notificationValidation = null;
                }
            });
        }
    }

    private async Task<bool> ValidateCurrentAsync()
    {
        if (!await MutationIsCurrentAsync().ConfigureAwait(false)) return false;
        if (await SnapshotAsync().ConfigureAwait(false) is not { } snapshot || !MutationLeaseIsCurrent())
        {
            PauseAvailability("snapshot_unavailable");
            return false;
        }
        if (snapshot != _expected)
        {
            ReportSnapshotMismatch(snapshot, _expected, "notification");
            return Suspend("text_or_caret_changed");
        }
        ResumeAvailability();
        return true;
    }

    /// <summary>Structural metadata only; never log the field or dictated text.</summary>
    private void ReportSnapshotMismatch(LiveDictationSnapshot observed, LiveDictationSnapshot expected, string stage) =>
        _onDiagnostic($"snapshot_mismatch:stage={stage},textMatches={Lower(observed.Text == expected.Text)}," +
            $"caretMatches={Lower(observed.Selection == expected.Selection)},lengths={observed.Text.Length}/{expected.Text.Length}," +
            $"ranges={Format(observed.Selection)}/{Format(expected.Selection)}");

    private bool Suspend(string reason)
    {
        var first = !_suspended;
        _onDiagnostic(reason);
        _suspended = true;
        Cancel();
        if (first) OnSuspended?.Invoke();
        return false;
    }

    internal static List<string> Graphemes(string text)
    {
        var result = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) result.Add(enumerator.GetTextElement());
        return result;
    }

    private static string Replace(string text, LiveTextRange range, string replacement) =>
        string.Concat(text.AsSpan(0, range.Location), replacement, text.AsSpan(range.End));

    public static bool IsValid(LiveTextRange range, string text)
    {
        if (range.Location < 0 || range.Location > text.Length || range.Length < 0 || range.Length > text.Length - range.Location)
            return false;
        // Never split a UTF-16 surrogate pair at either boundary.
        bool IsBoundary(int offset) => offset <= 0 || offset >= text.Length ||
            !(char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]));
        return IsBoundary(range.Location) && IsBoundary(range.End);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    private static string Format(LiveTextRange range) => $"{range.Location}:{range.Length}";
    private static string Lower(bool value) => value ? "true" : "false";
}
