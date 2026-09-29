namespace Muesli.Windows.Services;

public sealed class DictationHotkeyStateMachine
{
    public DictationHotkeyState State { get; private set; } = DictationHotkeyState.Idle;

    public bool IsHandsFree => State == DictationHotkeyState.HandsFree;
    public bool IsArmed => State is DictationHotkeyState.Armed or DictationHotkeyState.Preparing;
    public bool IsLive => State is DictationHotkeyState.Holding or DictationHotkeyState.HandsFree or DictationHotkeyState.Preparing or DictationHotkeyState.Armed;

    public DictationHotkeyAction KeyDown(bool doubleTapEnabled)
    {
        if (State == DictationHotkeyState.HandsFree)
        {
            return Transition(DictationHotkeyState.Idle, DictationHotkeyAction.StopRecording);
        }

        if (State == DictationHotkeyState.AwaitingSecondTap && doubleTapEnabled)
        {
            return Transition(DictationHotkeyState.HandsFree, DictationHotkeyAction.EnterHandsFree);
        }

        if (State != DictationHotkeyState.Idle)
        {
            return DictationHotkeyAction.None;
        }

        return Transition(DictationHotkeyState.Armed, DictationHotkeyAction.Arm);
    }

    public DictationHotkeyAction KeyUp(bool doubleTapEnabled)
    {
        if (State == DictationHotkeyState.Holding)
        {
            return Transition(DictationHotkeyState.Idle, DictationHotkeyAction.StopRecording);
        }

        if (State is DictationHotkeyState.Armed or DictationHotkeyState.Preparing)
        {
            return doubleTapEnabled
                ? Transition(DictationHotkeyState.AwaitingSecondTap, DictationHotkeyAction.StartDoubleTapTimer)
                : Transition(DictationHotkeyState.Idle, DictationHotkeyAction.Cancel);
        }

        return DictationHotkeyAction.None;
    }

    public DictationHotkeyAction PrepareDelayElapsed()
    {
        return State == DictationHotkeyState.Armed
            ? Transition(DictationHotkeyState.Preparing, DictationHotkeyAction.ShowPreparing)
            : DictationHotkeyAction.None;
    }

    public DictationHotkeyAction StartDelayElapsed()
    {
        return State is DictationHotkeyState.Armed or DictationHotkeyState.Preparing
            ? Transition(DictationHotkeyState.Holding, DictationHotkeyAction.StartRecording)
            : DictationHotkeyAction.None;
    }

    public DictationHotkeyAction DoubleTapWindowElapsed()
    {
        return State == DictationHotkeyState.AwaitingSecondTap
            ? Transition(DictationHotkeyState.Idle, DictationHotkeyAction.Cancel)
            : DictationHotkeyAction.None;
    }

    public DictationHotkeyAction OtherKeyWhileArmed()
    {
        return State switch
        {
            DictationHotkeyState.Holding => Transition(DictationHotkeyState.Idle, DictationHotkeyAction.StopRecording),
            DictationHotkeyState.Armed or DictationHotkeyState.Preparing => Transition(DictationHotkeyState.Idle, DictationHotkeyAction.Cancel),
            DictationHotkeyState.AwaitingSecondTap => Transition(DictationHotkeyState.Idle, DictationHotkeyAction.Cancel),
            _ => DictationHotkeyAction.None
        };
    }

    public void Reset() => State = DictationHotkeyState.Idle;

    private DictationHotkeyAction Transition(DictationHotkeyState state, DictationHotkeyAction action)
    {
        State = state;
        return action;
    }
}

public enum DictationHotkeyState
{
    Idle,
    Armed,
    Preparing,
    Holding,
    AwaitingSecondTap,
    HandsFree
}

public enum DictationHotkeyAction
{
    None,
    Arm,
    ShowPreparing,
    StartRecording,
    StopRecording,
    Cancel,
    StartDoubleTapTimer,
    EnterHandsFree
}
