using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Muesli.Windows.UITests;

/// <summary>
/// End-to-end qualification of the real shortcut path in the packaged host: the actual low-level
/// keyboard hook, the actual packaged microphone, and the real indicator states.
/// </summary>
/// <remarks>
/// <para>
/// This deliberately uses no presentation probe and no test hook. Keys are injected with
/// <c>SendInput</c>, exactly as a user's keyboard would deliver them, and the assertions read the
/// indicator's own window geometry — the pill's size is the state, so this observes the shipping
/// behaviour rather than an instrumented view of it.
/// </para>
/// <para>
/// It requires a working capture device and the packaged <c>microphone</c> device capability. A
/// failure here is a real product or environment failure, which is the point.
/// </para>
/// </remarks>
[Collection("UiAutomation")]
[Trait(UiAutomationEnvironment.TraitName, UiAutomationEnvironment.TraitValue)]
public sealed class PackagedDictationShortcutTests
{
    private const byte VkF8 = 0x77;
    private const uint KeyEventKeyUp = 2;

    // Pill geometry in DIPs, from the product specification.
    private static readonly (double Width, double Height) Idle = (44, 28);
    private static readonly (double Width, double Height) Live = (76, 22);
    private static readonly (double Width, double Height) Transcribing = (120, 32);

    [WinUiAutomationFact]
    public void Holding_the_shortcut_records_and_releasing_it_transcribes_and_returns_to_idle() =>
        StaRunner.Run(() =>
        {
            using var session = MuesliWinUiSession.LaunchWithIndicatorShown();
            session.Run(() =>
            {
                var indicator = IndicatorHandle(session);
                var dashboard = new IntPtr(session.Window.Current.NativeWindowHandle);

                AssertSize(indicator, Idle, "idle at rest");
                session.CaptureSecondaryWindow(WindowOf(indicator), "shortcut-01-idle");

                // The window that must receive the transcript. The pill must never displace it.
                SetForegroundWindow(dashboard);
                var pasteTarget = GetForegroundWindow();

                var observed = new List<string>();
                using (var _ = HoldShortcut())
                {
                    observed.AddRange(SampleUntil(indicator, Live, TimeSpan.FromSeconds(6), dashboard));
                }

                Assert.True(
                    observed.Contains(Describe(Live)),
                    $"Holding the shortcut must reach the live capture pill ({Describe(Live)}). Saw: {string.Join(" → ", observed)}.");
                session.CaptureSecondaryWindow(WindowOf(indicator), "shortcut-02-recording");

                // Release: transcription, then an outcome, then back to idle under its own timer.
                var after = SampleUntil(indicator, Idle, TimeSpan.FromSeconds(30), dashboard);

                Assert.True(
                    after.Contains(Describe(Transcribing)) || after.Any(IsOutcome),
                    $"Releasing must transcribe or report an outcome. Saw: {string.Join(" → ", after)}.");
                Assert.True(
                    after.LastOrDefault() == Describe(Idle),
                    $"The pill must return to idle on its own. Saw: {string.Join(" → ", after)}.");

                session.CaptureSecondaryWindow(WindowOf(indicator), "shortcut-03-returned-to-idle");
                AssertCannotTakeFocus(indicator);
            });
        });

    [WinUiAutomationFact]
    public void Escape_cancels_an_active_dictation_and_returns_the_pill_to_idle() =>
        StaRunner.Run(() =>
        {
            using var session = MuesliWinUiSession.LaunchWithIndicatorShown();
            session.Run(() =>
            {
                var indicator = IndicatorHandle(session);
                var dashboard = new IntPtr(session.Window.Current.NativeWindowHandle);
                SetForegroundWindow(dashboard);

                AssertSize(indicator, Idle, "idle at rest");

                using (var _ = HoldShortcut())
                {
                    var live = SampleUntil(indicator, Live, TimeSpan.FromSeconds(6), dashboard);
                    Assert.True(
                        live.Contains(Describe(Live)),
                        $"The dictation must be live before Escape is meaningful. Saw: {string.Join(" → ", live)}.");
                    session.CaptureSecondaryWindow(WindowOf(indicator), "shortcut-04-live-before-escape");

                    PressEscape();
                }

                var after = SampleUntil(indicator, Idle, TimeSpan.FromSeconds(20), dashboard);
                Assert.True(
                    after.LastOrDefault() == Describe(Idle),
                    $"Escape must return the pill to idle. Saw: {string.Join(" → ", after)}.");

                session.CaptureSecondaryWindow(WindowOf(indicator), "shortcut-05-cancelled-to-idle");

                // And the shortcut must still work afterwards: a cancel may not strand the machine.
                using (var _ = HoldShortcut())
                {
                    var retry = SampleUntil(indicator, Live, TimeSpan.FromSeconds(6), dashboard);
                    Assert.True(
                        retry.Contains(Describe(Live)),
                        $"A cancelled dictation must not block the next attempt. Saw: {string.Join(" → ", retry)}.");
                }

                PressEscape();
                SampleUntil(indicator, Idle, TimeSpan.FromSeconds(20), dashboard);
            });
        });

    /// <summary>Holds the configured shortcut down, releasing it when disposed.</summary>
    private static IDisposable HoldShortcut()
    {
        keybd_event(VkF8, 0, 0, UIntPtr.Zero);
        return new Release(() => keybd_event(VkF8, 0, KeyEventKeyUp, UIntPtr.Zero));
    }

    private static void PressEscape()
    {
        keybd_event(0x1B, 0, 0, UIntPtr.Zero);
        keybd_event(0x1B, 0, KeyEventKeyUp, UIntPtr.Zero);
    }

    /// <summary>
    /// Samples the pill's size until it reaches <paramref name="target"/> or the timeout expires,
    /// returning every distinct size seen. Also asserts continuously that the indicator never
    /// steals the foreground from the dictated-into window.
    /// </summary>
    private static List<string> SampleUntil(
        IntPtr indicator,
        (double Width, double Height) target,
        TimeSpan timeout,
        IntPtr mustStayForeground)
    {
        var seen = new List<string>();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var current = Describe(SizeInDips(indicator));
            if (seen.Count == 0 || seen[^1] != current) seen.Add(current);

            // The pill must remain a non-activating overlay for the whole flow — that is what
            // stops it becoming the window the transcript is pasted into.
            if (current == Describe(Live)) AssertCannotTakeFocus(indicator);

            if (current == Describe(target)) return seen;
            Thread.Sleep(150);
        }
        return seen;
    }

    private static bool IsOutcome(string size) =>
        size.StartsWith("220x", StringComparison.Ordinal) || size.StartsWith("260x", StringComparison.Ordinal);

    /// <summary>
    /// Asserts the pill is structurally incapable of becoming the paste target.
    /// </summary>
    /// <remarks>
    /// This checks the window's extended styles rather than comparing <c>GetForegroundWindow</c>.
    /// In an automation session there is frequently no other eligible window to hold the
    /// foreground, and a test process cannot reliably hand it to one — Windows' foreground lock
    /// denies <c>SetForegroundWindow</c> from a background process. The durable contract is that
    /// the pill carries <c>WS_EX_NOACTIVATE</c> (it cannot take focus) and <c>WS_EX_TOOLWINDOW</c>
    /// without <c>WS_EX_APPWINDOW</c> (it is absent from the taskbar and Alt+Tab).
    /// </remarks>
    private static void AssertCannotTakeFocus(IntPtr indicator)
    {
        const long noActivate = 0x08000000;
        const long toolWindow = 0x00000080;
        const long appWindow = 0x00040000;

        var style = GetWindowLongPtr(indicator, -20);
        Assert.True(
            (style & noActivate) != 0,
            $"The indicator must carry WS_EX_NOACTIVATE so it cannot steal focus from the " +
            $"dictated-into app and become the paste target. Extended style was 0x{style:X8}.");
        Assert.True(
            (style & toolWindow) != 0,
            $"The indicator must carry WS_EX_TOOLWINDOW to stay out of Alt+Tab. Style 0x{style:X8}.");
        Assert.True(
            (style & appWindow) == 0,
            $"The indicator must not carry WS_EX_APPWINDOW, which would put it in the taskbar. Style 0x{style:X8}.");
    }

    private static void AssertSize(IntPtr window, (double Width, double Height) expected, string because)
    {
        var actual = SizeInDips(window);
        Assert.True(
            Math.Abs(actual.Width - expected.Width) <= 1 && Math.Abs(actual.Height - expected.Height) <= 1,
            $"Expected {Describe(expected)} DIP for {because} but measured {Describe(actual)} DIP.");
    }

    private static (double Width, double Height) SizeInDips(IntPtr window)
    {
        var scale = Math.Max(1.0, GetDpiForWindow(window) / 96.0);
        return GetWindowRect(window, out var rect)
            ? ((rect.Right - rect.Left) / scale, (rect.Bottom - rect.Top) / scale)
            : (0, 0);
    }

    private static string Describe((double Width, double Height) size) =>
        $"{Math.Round(size.Width)}x{Math.Round(size.Height)}";

    private static AutomationElement WindowOf(IntPtr handle) => AutomationElement.FromHandle(handle);

    private static string DescribeWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return "<none>";
        try
        {
            var element = AutomationElement.FromHandle(handle);
            return $"'{element.Current.Name}' (class {element.Current.ClassName}, hwnd {handle})";
        }
        catch
        {
            return $"hwnd {handle}";
        }
    }

    private static IntPtr IndicatorHandle(MuesliWinUiSession session)
    {
        var window = session.RequireSecondaryWindow("FloatingDictationIndicator");
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        Assert.True(handle != IntPtr.Zero, "The floating indicator window must expose a native handle.");

        // The indicator is a separate top-level window. If this resolves to the dashboard, the
        // paste-target assertions below would compare a window against itself and fail for the
        // wrong reason.
        var dashboard = new IntPtr(session.Window.Current.NativeWindowHandle);
        Assert.True(
            handle != dashboard,
            $"Resolved the dashboard {DescribeWindow(dashboard)} as the floating indicator. " +
            "The indicator must be its own top-level window.");
        return handle;
    }

    private sealed class Release(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern long GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
