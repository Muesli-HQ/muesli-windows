using Muesli.Windows.Core.Services;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// Cover for the hotkey behaviour the WinUI host lost relative to the retired WPF implementation:
/// which states Escape can cancel from, how a failed microphone start must leave the machine, and
/// how a microphone denial is reported.
/// </summary>
public sealed class DictationHotkeyOrchestrationTests
{
    // ─── Escape must reach every live state ──────────────────────────────────────────────────

    [Fact]
    public void Escape_cancels_a_gesture_that_is_only_armed()
    {
        // The regression: capture has not started, so "is the coordinator recording" is false and
        // the old predicate let Escape through to the focused app instead of cancelling.
        var machine = new DictationHotkeyStateMachine();
        machine.KeyDown(doubleTapEnabled: false);

        Assert.Equal(DictationHotkeyState.Armed, machine.State);
        Assert.True(machine.IsLive);
        Assert.True(Policy(machine, recording: false, transcribing: false, busy: false, operation: false));
    }

    [Fact]
    public void Escape_cancels_while_preparing()
    {
        var machine = new DictationHotkeyStateMachine();
        machine.KeyDown(doubleTapEnabled: false);
        machine.PrepareDelayElapsed();

        Assert.Equal(DictationHotkeyState.Preparing, machine.State);
        Assert.True(Policy(machine, recording: false, transcribing: false, busy: false, operation: false));
    }

    [Fact]
    public void Escape_cancels_while_recording()
    {
        var machine = new DictationHotkeyStateMachine();
        machine.KeyDown(doubleTapEnabled: false);
        machine.StartDelayElapsed();

        Assert.Equal(DictationHotkeyState.Holding, machine.State);
        Assert.True(Policy(machine, recording: true, transcribing: false, busy: false, operation: false));
    }

    [Fact]
    public void Escape_cancels_while_transcribing_after_the_gesture_has_ended()
    {
        // Key-up returned the machine to Idle, so only the coordinator knows work is still running.
        var machine = new DictationHotkeyStateMachine();
        machine.KeyDown(doubleTapEnabled: false);
        machine.StartDelayElapsed();
        machine.KeyUp(doubleTapEnabled: false);

        Assert.Equal(DictationHotkeyState.Idle, machine.State);
        Assert.False(machine.IsLive);
        Assert.True(Policy(machine, recording: false, transcribing: true, busy: false, operation: false));
    }

    [Fact]
    public void Escape_cancels_while_an_operation_token_is_still_alive()
    {
        var machine = new DictationHotkeyStateMachine();
        Assert.True(Policy(machine, recording: false, transcribing: false, busy: false, operation: true));
    }

    [Fact]
    public void Escape_is_passed_through_when_no_dictation_is_in_flight()
    {
        // The pill must never swallow Escape from the app the user is actually typing in.
        var machine = new DictationHotkeyStateMachine();
        Assert.False(Policy(machine, recording: false, transcribing: false, busy: false, operation: false));
    }

    [Fact]
    public void Hands_free_remains_cancellable_after_the_shortcut_is_released()
    {
        var machine = new DictationHotkeyStateMachine();
        machine.KeyDown(doubleTapEnabled: true);
        machine.KeyUp(doubleTapEnabled: true);
        machine.KeyDown(doubleTapEnabled: true);

        Assert.Equal(DictationHotkeyState.HandsFree, machine.State);
        Assert.True(machine.IsLive);
        Assert.True(Policy(machine, recording: true, transcribing: false, busy: false, operation: false));
    }

    // ─── A failed microphone start must not strand the machine ───────────────────────────────

    [Fact]
    public void A_failed_microphone_start_resets_the_machine_so_the_next_press_is_accepted()
    {
        // Observed live: the pill stayed in its recording state and no further shortcut press did
        // anything until Muesli was restarted.
        var machine = new DictationHotkeyStateMachine();
        machine.KeyDown(doubleTapEnabled: false);
        machine.StartDelayElapsed();
        Assert.Equal(DictationHotkeyState.Holding, machine.State);

        // What the host does when the coordinator reports it never started recording.
        machine.Reset();

        Assert.Equal(DictationHotkeyState.Idle, machine.State);
        Assert.False(machine.IsLive);

        // The retry must behave exactly like a first press.
        Assert.Equal(DictationHotkeyAction.Arm, machine.KeyDown(doubleTapEnabled: false));
        Assert.Equal(DictationHotkeyState.Armed, machine.State);
    }

    [Fact]
    public void A_reset_machine_does_not_act_on_a_timer_that_was_already_in_flight()
    {
        // The prepare/start timers are cancelled on reset, but one may already have been dispatched.
        var machine = new DictationHotkeyStateMachine();
        machine.KeyDown(doubleTapEnabled: false);
        machine.Reset();

        Assert.Equal(DictationHotkeyAction.None, machine.PrepareDelayElapsed());
        Assert.Equal(DictationHotkeyAction.None, machine.StartDelayElapsed());
        Assert.Equal(DictationHotkeyState.Idle, machine.State);
    }

    // ─── Microphone denial is reported as a permission problem ───────────────────────────────

    [Fact]
    public void A_denied_microphone_is_reported_as_a_permission_problem_with_a_route_to_settings()
    {
        var denied = new UnauthorizedAccessException("Access is denied. (0x80070005 (E_ACCESSDENIED))");

        Assert.True(MicrophoneAccessDiagnostics.IsAccessDenied(denied));
        var message = MicrophoneAccessDiagnostics.DescribeFailure(denied);

        Assert.Contains("Microphone", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hotkey", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ms-settings:privacy-microphone", MicrophoneAccessDiagnostics.PrivacySettingsUri);
    }

    [Fact]
    public void An_access_denied_hresult_is_recognised_even_when_it_is_wrapped()
    {
        var inner = new InvalidOperationException("audio client")
        {
            HResult = MicrophoneAccessDiagnostics.AccessDeniedHResult
        };
        Assert.True(MicrophoneAccessDiagnostics.IsAccessDenied(new InvalidOperationException("start", inner)));
    }

    [Fact]
    public void An_ordinary_device_fault_keeps_its_own_message_and_is_not_called_a_permission_problem()
    {
        var fault = new InvalidOperationException("No capture device is present.");

        Assert.False(MicrophoneAccessDiagnostics.IsAccessDenied(fault));
        Assert.Contains("No capture device is present.", MicrophoneAccessDiagnostics.DescribeFailure(fault));
    }

    private static bool Policy(
        DictationHotkeyStateMachine machine,
        bool recording,
        bool transcribing,
        bool busy,
        bool operation) =>
        DictationCancellationPolicy.CanCancel(machine.IsLive, recording, transcribing, busy, operation);
}
