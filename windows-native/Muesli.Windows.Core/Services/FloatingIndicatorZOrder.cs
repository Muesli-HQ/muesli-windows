namespace Muesli.Windows.Core.Services;

/// <summary>One top-level window that sits above the indicator in z-order.</summary>
/// <param name="Visible">The window is shown (<c>IsWindowVisible</c>).</param>
/// <param name="Topmost">The window carries <c>WS_EX_TOPMOST</c>.</param>
/// <param name="Cloaked">DWM hides the window (another virtual desktop, a suspended app).</param>
public readonly record struct IndicatorWindowAbove(bool Visible, bool Topmost, bool Cloaked);

/// <summary>What the indicator has to do to be in front of ordinary windows again.</summary>
public enum IndicatorZOrderRepair
{
    /// <summary>Hidden, or already in front: leave the window alone.</summary>
    None,

    /// <summary>The topmost flag is gone: setting it puts the window back in the topmost band.</summary>
    SetTopmost,

    /// <summary>
    /// The topmost flag is still set but an ordinary window is in front. Setting the flag again is
    /// a no-op for a window that already has it, so it is cleared first and then set.
    /// </summary>
    ResetTopmost
}

/// <summary>
/// Decides when the floating indicator has lost its place in front of ordinary windows. Windows can
/// leave a window flagged <c>WS_EX_TOPMOST</c> while ordering it below non-topmost windows; the flag
/// alone is therefore not evidence that the pill can be seen. The window walks the windows above
/// itself and asks this type what to do.
/// </summary>
public static class FloatingIndicatorZOrder
{
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;
    public const uint SwpHideWindow = 0x0080;
    public const uint SwpNoOwnerZOrder = 0x0200;
    public const uint SwpNoSendChanging = 0x0400;

    /// <summary>
    /// <c>SetWindowPos</c> flags for every z-order repair: order only. Never activates (a dictation
    /// must not be pasted into the pill), never moves or resizes it, and never shows a hidden pill.
    /// </summary>
    public const uint RepairFlags = SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder;

    /// <summary>
    /// Safety bound on the upward walk. A healthy pill has only the other topmost windows above it;
    /// a demoted one meets an ordinary window within a few steps.
    /// </summary>
    public const int MaxWindowsInspected = 512;

    /// <summary>
    /// True when a window that is on screen and not topmost sits above the indicator. Hidden and
    /// cloaked windows cover nothing and are ignored.
    /// </summary>
    public static bool IsBehindOrdinaryWindow(IEnumerable<IndicatorWindowAbove> windowsAbove)
    {
        ArgumentNullException.ThrowIfNull(windowsAbove);
        var inspected = 0;
        foreach (var window in windowsAbove)
        {
            if (++inspected > MaxWindowsInspected) break;
            if (window is { Visible: true, Cloaked: false, Topmost: false }) return true;
        }

        return false;
    }

    /// <summary>The repair for the pill's current state; <see cref="IndicatorZOrderRepair.None"/> while hidden.</summary>
    public static IndicatorZOrderRepair RepairFor(bool shown, bool flaggedTopmost, bool behindOrdinaryWindow)
    {
        if (!shown) return IndicatorZOrderRepair.None;
        if (!flaggedTopmost) return IndicatorZOrderRepair.SetTopmost;
        return behindOrdinaryWindow ? IndicatorZOrderRepair.ResetTopmost : IndicatorZOrderRepair.None;
    }
}
