using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class HotkeyRecordingTests
{
    private const int Ctrl = 0x11, Menu = 0x12, Shift = 0x10, RightWin = 0x5C, KeyA = 0x41, F8 = 0x77;
    private static readonly RecorderKey RightCtrlDown = new(Ctrl, IsExtended: true);
    private static readonly RecorderKey RightAltDown = new(Menu, IsExtended: true);
    private static readonly RecorderKey AltGrCtrl = new(Ctrl, IsExtended: false, ScanCode: 0x1D);

    [Fact]
    public void A_right_modifier_held_past_auto_repeat_still_records()
    {
        var recorder = new HotkeyRecorderCore();
        Assert.Null(recorder.KeyDown(RightCtrlDown, new HeldModifiers(Ctrl: true)));
        for (var repeat = 0; repeat < 5; repeat++)
            Assert.Null(recorder.KeyDown(RightCtrlDown with { IsRepeat = true }, new HeldModifiers(Ctrl: true)));
        Assert.Equal("Right Ctrl", recorder.KeyUp(RightCtrlDown));
    }

    [Fact]
    public void Right_Alt_records_on_AltGr_layouts_whatever_order_the_fake_Ctrl_arrives_in()
    {
        var recorder = new HotkeyRecorderCore();
        var altGrHeld = new HeldModifiers(Ctrl: true, Alt: true);
        Assert.Null(recorder.KeyDown(AltGrCtrl, new HeldModifiers(Ctrl: true)));
        Assert.Null(recorder.KeyDown(RightAltDown, altGrHeld));
        Assert.Null(recorder.KeyDown(AltGrCtrl with { IsRepeat = true }, altGrHeld));
        Assert.Null(recorder.KeyDown(RightAltDown with { IsRepeat = true }, altGrHeld));
        Assert.Null(recorder.KeyUp(AltGrCtrl));
        Assert.Equal("Right Alt", recorder.KeyUp(RightAltDown));
    }

    [Fact]
    public void A_left_modifier_or_a_second_modifier_is_not_a_lone_hold_key()
    {
        var recorder = new HotkeyRecorderCore();
        Assert.Null(recorder.KeyDown(new RecorderKey(Ctrl), new HeldModifiers(Ctrl: true)));
        Assert.Null(recorder.KeyUp(new RecorderKey(Ctrl)));

        Assert.Null(recorder.KeyDown(RightCtrlDown, new HeldModifiers(Ctrl: true)));
        Assert.Null(recorder.KeyDown(new RecorderKey(Shift, ScanCode: 0x2A), new HeldModifiers(Ctrl: true, Shift: true)));
        Assert.Null(recorder.KeyUp(RightCtrlDown));
    }

    [Fact]
    public void Keys_record_with_their_modifiers_on_key_down()
    {
        var recorder = new HotkeyRecorderCore();
        Assert.Equal("F8", recorder.KeyDown(new RecorderKey(F8), default));
        Assert.Equal("Ctrl+Shift+A", recorder.KeyDown(new RecorderKey(KeyA), new HeldModifiers(Ctrl: true, Shift: true)));
        Assert.Equal("Right Shift", RecordLone(new RecorderKey(Shift, ScanCode: 0x36), new HeldModifiers(Shift: true)));
        Assert.Equal("Right Win", RecordLone(new RecorderKey(RightWin), new HeldModifiers(Win: true)));
    }

    private static string? RecordLone(RecorderKey key, HeldModifiers held)
    {
        var recorder = new HotkeyRecorderCore();
        recorder.KeyDown(key, held);
        return recorder.KeyUp(key);
    }

    private const int VkRightAlt = 0xA5, VkRightCtrl = 0xA3, VkLeftCtrl = 0xA2, VkC = 0x43;

    [Fact]
    public void AltGr_auto_repeat_does_not_cancel_a_Right_Alt_hold()
    {
        var hold = new ModifierHoldTracker(VkRightAlt);
        Assert.Equal(ModifierHoldAction.None, Down(hold, VkLeftCtrl, ModifierHoldTracker.AltGrPhantomCtrlScanCode));
        Assert.Equal(ModifierHoldAction.Start | ModifierHoldAction.SendMenuMask, Down(hold, VkRightAlt, 0x38));
        for (var repeat = 0; repeat < 5; repeat++)
        {
            Assert.Equal(ModifierHoldAction.None, Down(hold, VkLeftCtrl, ModifierHoldTracker.AltGrPhantomCtrlScanCode));
            Assert.Equal(ModifierHoldAction.None, Down(hold, VkRightAlt, 0x38));
        }
        Assert.Equal(ModifierHoldAction.Stop, Up(hold, VkRightAlt));
    }

    [Fact]
    public void A_real_combination_cancels_and_a_clean_release_stops()
    {
        var hold = new ModifierHoldTracker(VkRightCtrl);
        Assert.Equal(ModifierHoldAction.Start, Down(hold, VkRightCtrl, 0x1D));
        Assert.Equal(ModifierHoldAction.Cancel, Down(hold, VkC, 0x2E));
        Assert.Equal(ModifierHoldAction.None, Up(hold, VkRightCtrl));

        Assert.Equal(ModifierHoldAction.Start, Down(hold, VkRightCtrl, 0x1D));
        Assert.Equal(ModifierHoldAction.Stop, Up(hold, VkRightCtrl));
    }

    [Fact]
    public void A_real_Left_Ctrl_still_counts_as_another_key_and_injected_input_is_ignored()
    {
        var hold = new ModifierHoldTracker(VkRightAlt);
        Assert.Equal(ModifierHoldAction.Start | ModifierHoldAction.SendMenuMask, Down(hold, VkRightAlt, 0x38));
        Assert.Equal(ModifierHoldAction.Cancel, Down(hold, VkLeftCtrl, 0x1D));

        var injected = new ModifierHoldTracker(VkRightCtrl);
        Assert.Equal(ModifierHoldAction.None, injected.Process(VkRightCtrl, 0x1D, true, false, injected: true, () => false));
    }

    [Fact]
    public void The_hold_does_not_start_while_another_modifier_is_down()
    {
        var hold = new ModifierHoldTracker(VkRightCtrl);
        Assert.Equal(ModifierHoldAction.None, hold.Process(VkRightCtrl, 0x1D, true, false, false, () => true));
        Assert.Equal(ModifierHoldAction.None, Up(hold, VkRightCtrl));
    }

    private static ModifierHoldAction Down(ModifierHoldTracker hold, int key, uint scan) =>
        hold.Process(key, scan, isKeyDown: true, isKeyUp: false, injected: false, () => false);

    private static ModifierHoldAction Up(ModifierHoldTracker hold, int key) =>
        hold.Process(key, 0, isKeyDown: false, isKeyUp: true, injected: false, () => false);
}
