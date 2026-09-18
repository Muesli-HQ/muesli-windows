namespace Muesli.Windows.Services;

public enum MeetingSessionState
{
    Idle,
    Preparing,
    Recording,
    DegradedRecording,
    Stopping,
    Finalizing,
    Completed,
    Failed,
    Cancelled,
    RecoverableInterruption
}

public enum MeetingSessionTrigger
{
    Prepare,
    Prepared,
    PreparedDegraded,
    Degrade,
    Recover,
    Stop,
    TracksFinalized,
    Complete,
    Fail,
    Cancel,
    Interrupt,
    Resume,
    RestoreInterrupted,
    FinalizeInterrupted,
    Reset
}

public sealed record MeetingSessionTransition(
    MeetingSessionState From,
    MeetingSessionState To,
    MeetingSessionTrigger Trigger,
    DateTimeOffset Timestamp,
    string Reason);

public sealed class MeetingSessionStateMachine
{
    private readonly object _gate = new();
    private readonly List<MeetingSessionTransition> _history = [];

    public MeetingSessionState State { get; private set; } = MeetingSessionState.Idle;

    public IReadOnlyList<MeetingSessionTransition> History
    {
        get
        {
            lock (_gate)
            {
                return _history.ToList();
            }
        }
    }

    public MeetingSessionTransition Apply(
        MeetingSessionTrigger trigger,
        string reason = "",
        DateTimeOffset? timestamp = null)
    {
        lock (_gate)
        {
            return ApplyUnderGate(trigger, reason, timestamp);
        }
    }

    public bool TryApply(
        MeetingSessionTrigger trigger,
        out MeetingSessionTransition? transition,
        string reason = "",
        DateTimeOffset? timestamp = null)
    {
        lock (_gate)
        {
            if (!TryResolve(State, trigger, out _))
            {
                transition = null;
                return false;
            }
            transition = ApplyUnderGate(trigger, reason, timestamp);
            return true;
        }
    }

    public bool CanApply(MeetingSessionTrigger trigger)
    {
        lock (_gate)
        {
            return TryResolve(State, trigger, out _);
        }
    }

    private static MeetingSessionState Resolve(MeetingSessionState state, MeetingSessionTrigger trigger)
    {
        if (TryResolve(state, trigger, out var next))
        {
            return next;
        }

        throw new InvalidOperationException($"Meeting session cannot apply {trigger} while {state}.");
    }

    private MeetingSessionTransition ApplyUnderGate(
        MeetingSessionTrigger trigger,
        string reason,
        DateTimeOffset? timestamp)
    {
        var from = State;
        var to = Resolve(from, trigger);
        var transition = new MeetingSessionTransition(
            from,
            to,
            trigger,
            timestamp ?? DateTimeOffset.UtcNow,
            NormalizeReason(reason));
        State = to;
        _history.Add(transition);
        if (_history.Count > 64)
        {
            _history.RemoveRange(0, _history.Count - 64);
        }
        return transition;
    }

    private static bool TryResolve(
        MeetingSessionState state,
        MeetingSessionTrigger trigger,
        out MeetingSessionState next)
    {
        next = (state, trigger) switch
        {
            (MeetingSessionState.Idle, MeetingSessionTrigger.Prepare) => MeetingSessionState.Preparing,
            (MeetingSessionState.Idle, MeetingSessionTrigger.RestoreInterrupted) => MeetingSessionState.RecoverableInterruption,

            (MeetingSessionState.Preparing, MeetingSessionTrigger.Prepared) => MeetingSessionState.Recording,
            (MeetingSessionState.Preparing, MeetingSessionTrigger.PreparedDegraded) => MeetingSessionState.DegradedRecording,
            (MeetingSessionState.Preparing, MeetingSessionTrigger.Fail) => MeetingSessionState.Failed,
            (MeetingSessionState.Preparing, MeetingSessionTrigger.Cancel) => MeetingSessionState.Cancelled,
            (MeetingSessionState.Preparing, MeetingSessionTrigger.Interrupt) => MeetingSessionState.RecoverableInterruption,

            (MeetingSessionState.Recording, MeetingSessionTrigger.Degrade) => MeetingSessionState.DegradedRecording,
            (MeetingSessionState.Recording, MeetingSessionTrigger.Stop) => MeetingSessionState.Stopping,
            (MeetingSessionState.Recording, MeetingSessionTrigger.Interrupt) => MeetingSessionState.RecoverableInterruption,
            (MeetingSessionState.Recording, MeetingSessionTrigger.Cancel) => MeetingSessionState.Cancelled,
            (MeetingSessionState.Recording, MeetingSessionTrigger.Fail) => MeetingSessionState.Failed,

            (MeetingSessionState.DegradedRecording, MeetingSessionTrigger.Recover) => MeetingSessionState.Recording,
            (MeetingSessionState.DegradedRecording, MeetingSessionTrigger.Stop) => MeetingSessionState.Stopping,
            (MeetingSessionState.DegradedRecording, MeetingSessionTrigger.Interrupt) => MeetingSessionState.RecoverableInterruption,
            (MeetingSessionState.DegradedRecording, MeetingSessionTrigger.Cancel) => MeetingSessionState.Cancelled,
            (MeetingSessionState.DegradedRecording, MeetingSessionTrigger.Fail) => MeetingSessionState.Failed,

            (MeetingSessionState.Stopping, MeetingSessionTrigger.TracksFinalized) => MeetingSessionState.Finalizing,
            (MeetingSessionState.Stopping, MeetingSessionTrigger.Interrupt) => MeetingSessionState.RecoverableInterruption,
            (MeetingSessionState.Stopping, MeetingSessionTrigger.Cancel) => MeetingSessionState.Cancelled,
            (MeetingSessionState.Stopping, MeetingSessionTrigger.Fail) => MeetingSessionState.Failed,

            (MeetingSessionState.Finalizing, MeetingSessionTrigger.Complete) => MeetingSessionState.Completed,
            (MeetingSessionState.Finalizing, MeetingSessionTrigger.Interrupt) => MeetingSessionState.RecoverableInterruption,
            (MeetingSessionState.Finalizing, MeetingSessionTrigger.Cancel) => MeetingSessionState.Cancelled,
            (MeetingSessionState.Finalizing, MeetingSessionTrigger.Fail) => MeetingSessionState.Failed,

            (MeetingSessionState.RecoverableInterruption, MeetingSessionTrigger.Resume) => MeetingSessionState.Preparing,
            (MeetingSessionState.RecoverableInterruption, MeetingSessionTrigger.FinalizeInterrupted) => MeetingSessionState.Stopping,
            (MeetingSessionState.RecoverableInterruption, MeetingSessionTrigger.Cancel) => MeetingSessionState.Cancelled,
            (MeetingSessionState.RecoverableInterruption, MeetingSessionTrigger.Fail) => MeetingSessionState.Failed,

            (MeetingSessionState.Completed, MeetingSessionTrigger.Reset) => MeetingSessionState.Idle,
            (MeetingSessionState.Failed, MeetingSessionTrigger.Reset) => MeetingSessionState.Idle,
            (MeetingSessionState.Failed, MeetingSessionTrigger.Prepare) => MeetingSessionState.Preparing,
            (MeetingSessionState.Cancelled, MeetingSessionTrigger.Reset) => MeetingSessionState.Idle,
            _ => state
        };

        return next != state;
    }

    private static string NormalizeReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "unspecified";
        }

        var normalized = reason.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240];
    }
}
