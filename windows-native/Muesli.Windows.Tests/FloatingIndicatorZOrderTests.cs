using Muesli.Windows.Core.Services;

namespace Muesli.Windows.Tests;

public sealed class FloatingIndicatorZOrderTests
{
    private static readonly IndicatorWindowAbove TopmostWindow = new(Visible: true, Topmost: true, Cloaked: false);
    private static readonly IndicatorWindowAbove OrdinaryWindow = new(Visible: true, Topmost: false, Cloaked: false);
    private static readonly IndicatorWindowAbove HiddenOrdinaryWindow = new(Visible: false, Topmost: false, Cloaked: false);
    private static readonly IndicatorWindowAbove CloakedOrdinaryWindow = new(Visible: true, Topmost: false, Cloaked: true);

    // ─── Is the pill really in front? ────────────────────────────────────────────────────────

    [Fact]
    public void Nothing_above_means_in_front()
    {
        Assert.False(FloatingIndicatorZOrder.IsBehindOrdinaryWindow([]));
    }

    [Fact]
    public void Other_topmost_windows_above_do_not_count()
    {
        // The taskbar, another build's pill, Muesli's own meeting prompt: all legitimately above.
        Assert.False(FloatingIndicatorZOrder.IsBehindOrdinaryWindow([TopmostWindow, TopmostWindow, TopmostWindow]));
    }

    [Fact]
    public void A_visible_ordinary_window_above_means_the_pill_lost_its_place()
    {
        // Observed 2026-11-18: WS_EX_TOPMOST still set, yet ordered below the foreground app.
        Assert.True(FloatingIndicatorZOrder.IsBehindOrdinaryWindow([OrdinaryWindow, TopmostWindow]));
        Assert.True(FloatingIndicatorZOrder.IsBehindOrdinaryWindow([TopmostWindow, HiddenOrdinaryWindow, OrdinaryWindow]));
    }

    [Fact]
    public void Hidden_and_cloaked_windows_cover_nothing()
    {
        Assert.False(FloatingIndicatorZOrder.IsBehindOrdinaryWindow(
            [HiddenOrdinaryWindow, CloakedOrdinaryWindow, TopmostWindow]));
    }

    [Fact]
    public void The_walk_is_bounded()
    {
        static IEnumerable<IndicatorWindowAbove> Endless()
        {
            while (true) yield return TopmostWindow;
        }

        Assert.False(FloatingIndicatorZOrder.IsBehindOrdinaryWindow(Endless()));

        var beyondTheBound = Enumerable.Repeat(TopmostWindow, FloatingIndicatorZOrder.MaxWindowsInspected).Append(OrdinaryWindow);
        Assert.False(FloatingIndicatorZOrder.IsBehindOrdinaryWindow(beyondTheBound));
        var atTheBound = Enumerable.Repeat(TopmostWindow, FloatingIndicatorZOrder.MaxWindowsInspected - 1).Append(OrdinaryWindow);
        Assert.True(FloatingIndicatorZOrder.IsBehindOrdinaryWindow(atTheBound));
    }

    // ─── What to do about it ─────────────────────────────────────────────────────────────────

    [Theory]
    // A hidden pill is never touched, whatever its order: the setting or state says it is hidden.
    [InlineData(false, true, true, IndicatorZOrderRepair.None)]
    [InlineData(false, false, true, IndicatorZOrderRepair.None)]
    [InlineData(false, false, false, IndicatorZOrderRepair.None)]
    // In front: nothing to do, so a healthy pill is never reordered.
    [InlineData(true, true, false, IndicatorZOrderRepair.None)]
    // Flag set but behind an ordinary window: setting it again is a no-op, so clear then set.
    [InlineData(true, true, true, IndicatorZOrderRepair.ResetTopmost)]
    // Flag gone: setting it is enough.
    [InlineData(true, false, true, IndicatorZOrderRepair.SetTopmost)]
    [InlineData(true, false, false, IndicatorZOrderRepair.SetTopmost)]
    public void RepairFor_maps_every_case(bool shown, bool flaggedTopmost, bool behind, IndicatorZOrderRepair expected)
    {
        Assert.Equal(expected, FloatingIndicatorZOrder.RepairFor(shown, flaggedTopmost, behind));
    }

    [Fact]
    public void Repair_changes_order_only()
    {
        var flags = FloatingIndicatorZOrder.RepairFlags;
        // Never the foreground window a dictation is pasted into.
        Assert.NotEqual(0u, flags & FloatingIndicatorZOrder.SwpNoActivate);
        // Never moves or resizes the pill.
        Assert.NotEqual(0u, flags & FloatingIndicatorZOrder.SwpNoMove);
        Assert.NotEqual(0u, flags & FloatingIndicatorZOrder.SwpNoSize);
        // Never shows a hidden pill (or hides a shown one).
        Assert.Equal(0u, flags & (FloatingIndicatorZOrder.SwpShowWindow | FloatingIndicatorZOrder.SwpHideWindow));
        // The window must still hear about the change, so WM_WINDOWPOSCHANGED stays reliable.
        Assert.Equal(0u, flags & FloatingIndicatorZOrder.SwpNoSendChanging);
    }

    // ─── Wiring that needs a live window, pinned at the source level ─────────────────────────

    private static string WindowSource() => File.ReadAllText(TestRepositoryLayout.Combine(
        "windows-native", "Muesli.Windows.WinUI", "Indicator", "FloatingIndicatorWindow.cs"));

    [Fact]
    public void The_window_checks_its_order_after_showing_and_on_events_not_on_a_timer()
    {
        var window = WindowSource();
        var show = window.IndexOf("AppWindow.Show(false);", StringComparison.Ordinal);
        var keep = window.IndexOf("KeepInFront();", show, StringComparison.Ordinal);
        Assert.True(show >= 0 && keep > show, "Apply must re-assert the pill's place after showing it.");

        Assert.Contains("case WmWindowPosChanged:", window, StringComparison.Ordinal);
        Assert.Contains("EventSystemForeground, EventSystemForeground", window, StringComparison.Ordinal);
        Assert.Contains("UnhookWinEvent(_foregroundHook)", window, StringComparison.Ordinal);
        // Two timers only: the waveform and the hover collapse. No z-order polling.
        Assert.Equal(2, window.Split("queue.CreateTimer()").Length - 1);
        Assert.DoesNotContain("Tick += (_, _) => Guarded(KeepInFront)", window, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_repair_uses_the_order_only_flags_and_skips_a_hidden_pill()
    {
        var window = WindowSource();
        Assert.Contains("SetWindowPos(_hwnd, HwndNoTopmost, 0, 0, 0, 0, FloatingIndicatorZOrder.RepairFlags);", window, StringComparison.Ordinal);
        Assert.Contains("SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, FloatingIndicatorZOrder.RepairFlags);", window, StringComparison.Ordinal);
        Assert.Contains("if (_closed || !_shown) return;", window, StringComparison.Ordinal);
        // Its own repair must not re-trigger the check.
        Assert.Contains("if (!_repairingZOrder &&", window, StringComparison.Ordinal);
    }
}
