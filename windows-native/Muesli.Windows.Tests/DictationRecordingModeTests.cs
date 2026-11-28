using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>macOS "Dictation recording mode": Hold to talk (default) or Press to toggle.</summary>
public sealed class DictationRecordingModeTests
{
    [Fact]
    public void Press_starts_at_once_and_the_next_press_stops()
    {
        var machine = new DictationHotkeyStateMachine();
        Assert.Equal(DictationHotkeyAction.StartToggleRecording, machine.KeyDown(doubleTapEnabled: false, pressToToggle: true));
        Assert.True(machine.IsLive);
        Assert.Equal(DictationHotkeyAction.None, machine.KeyUp(doubleTapEnabled: false));
        Assert.Equal(DictationHotkeyAction.None, machine.OtherKeyWhileArmed());
        Assert.Equal(DictationHotkeyAction.StopRecording, machine.KeyDown(doubleTapEnabled: false, pressToToggle: true));
        Assert.Equal(DictationHotkeyState.Idle, machine.State);
        Assert.Equal(DictationHotkeyAction.None, machine.KeyUp(doubleTapEnabled: false));
    }

    [Fact]
    public void Double_tap_and_activation_delay_do_not_apply_in_toggle_mode()
    {
        var machine = new DictationHotkeyStateMachine();
        machine.KeyDown(doubleTapEnabled: true, pressToToggle: true);
        Assert.Equal(DictationHotkeyAction.None, machine.StartDelayElapsed());
        Assert.Equal(DictationHotkeyAction.None, machine.KeyUp(doubleTapEnabled: true));
        Assert.Equal(DictationHotkeyAction.StopRecording, machine.KeyDown(doubleTapEnabled: true, pressToToggle: true));
    }

    [Fact]
    public void Hold_to_talk_is_unchanged()
    {
        var machine = new DictationHotkeyStateMachine();
        Assert.Equal(DictationHotkeyAction.Arm, machine.KeyDown(doubleTapEnabled: false));
        Assert.Equal(DictationHotkeyAction.StartRecording, machine.StartDelayElapsed());
        Assert.Equal(DictationHotkeyAction.StopRecording, machine.KeyUp(doubleTapEnabled: false));
    }

    [Fact]
    public void A_hold_begun_before_switching_to_toggle_still_finishes_as_a_hold()
    {
        var machine = new DictationHotkeyStateMachine();
        machine.KeyDown(doubleTapEnabled: false);
        machine.StartDelayElapsed();
        Assert.Equal(DictationHotkeyAction.None, machine.KeyDown(doubleTapEnabled: false, pressToToggle: true));
        Assert.Equal(DictationHotkeyAction.StopRecording, machine.KeyUp(doubleTapEnabled: false));
    }

    [Theory]
    [InlineData("Ctrl+Shift+D", true)]
    [InlineData("Alt+F9", true)]
    [InlineData("F8", false)]
    [InlineData("Right Ctrl", false)]
    [InlineData("nonsense+", false)]
    public void Toggle_needs_a_modifier_and_a_key(string hotkey, bool combination) =>
        Assert.Equal(combination, DictationRecordingModes.IsCombination(hotkey));

    [Fact]
    public void Toggle_is_active_only_with_a_combination_and_defaults_to_hold()
    {
        Assert.Equal(DictationRecordingModes.HoldToTalk, new MuesliSettings().DictationRecordingMode);
        Assert.False(DictationRecordingModes.IsToggleActive(new MuesliSettings { DictationRecordingMode = "toggle", Hotkey = "F8" }));
        Assert.True(DictationRecordingModes.IsToggleActive(new MuesliSettings { DictationRecordingMode = "toggle", Hotkey = "Ctrl+Shift+D" }));
        Assert.False(DictationRecordingModes.IsToggleActive(new MuesliSettings { DictationRecordingMode = "push_to_talk", Hotkey = "Ctrl+Shift+D" }));
        Assert.Equal(DictationRecordingModes.HoldToTalk, DictationRecordingModes.Normalize("sideways"));
        Assert.Equal(DictationRecordingModes.PressToToggle, DictationRecordingModes.Normalize(" Toggle "));
    }
}
