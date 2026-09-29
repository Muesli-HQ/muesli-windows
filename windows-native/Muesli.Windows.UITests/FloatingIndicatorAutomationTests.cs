using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Muesli.Windows.UITests;

/// <summary>
/// Qualifies the floating dictation indicator as an always-on-top, secondary window that is not
/// part of the shell's navigation surface. The idle state is deterministically reachable on a
/// clean profile, and the ShowFloatingIndicator setting must hide/show it immediately.
/// </summary>
/// <remarks>
/// <para>
/// The live states (preparing / recording / transcribing / success / error) are produced by real
/// microphone capture and transcription, so they are rendered here through the indicator's
/// presentation probe (<see cref="FloatingIndicatorProbe"/>): the state is driven by a file in the
/// isolated profile, but the pill itself renders through the shipping code path.
/// </para>
/// <para>
/// <b>A probe-driven state is a simulated presentation state.</b> These tests qualify the pill's
/// appearance, geometry, and accessible names for each state. They are not evidence that capture
/// or transcription works — that is covered by the unit tests in <c>Muesli.Windows.Tests</c> and
/// by an actual dictation exercised against the real profile.
/// </para>
/// </remarks>
[Collection("UiAutomation")]
[Trait(UiAutomationEnvironment.TraitName, UiAutomationEnvironment.TraitValue)]
public sealed class FloatingIndicatorAutomationTests
{
    [WinUiAutomationFact]
    public void Idle_indicator_is_reachable_and_publishes_an_idle_name() => StaRunner.Run(() =>
    {
        using var session = MuesliWinUiSession.Launch();
        session.Run(() =>
        {
            var window = session.RequireSecondaryWindow("FloatingDictationIndicator");
            var indicator = window.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "FloatingDictationIndicator"));
            Assert.NotNull(indicator);
            Assert.True(
                indicator.Current.Name.Contains("idle", StringComparison.OrdinalIgnoreCase),
                $"The idle indicator must publish an idle accessible name, but was '{indicator.Current.Name}'.");
            Assert.True(indicator.Current.IsEnabled, "The idle indicator must remain enabled for assistive technology.");
            session.CaptureSecondaryWindow(window, "winui-indicator-idle");
        });
    });

    [WinUiAutomationFact]
    public void Indicator_reacts_immediately_to_the_ShowFloatingIndicator_setting() => StaRunner.Run(() =>
    {
        using var session = MuesliWinUiSession.Launch();
        session.Run(() =>
        {
            // The ignore-with-the-window-open path: the Settings change must re-run the state pass,
            // so toggling ShowFloatingIndicator on a live shell hides/shows the pill without restart.
            // The clean profile seeds showFloatingIndicator = false, so the pill starts hidden.
            // What this test owns is that the setting takes effect immediately, in both
            // directions, without a restart.
            var window = session.RequireSecondaryWindow("FloatingDictationIndicator");
            var handle = new IntPtr(window.Current.NativeWindowHandle);
            Assert.True(handle != IntPtr.Zero, "The indicator window must exist even while hidden.");

            session.NavigateTo("settings", "NavSettings", "SettingsSaveButton");
            session.SelectAutomationId("SettingsAppearanceTab");

            ToggleFloatingIndicator(session, turnOn: true);
            session.SelectAutomationId("SettingsSaveButton");
            WaitUntil(() => IsWindowVisible(handle), "Turning ShowFloatingIndicator on must show the pill.");

            ToggleFloatingIndicator(session, turnOn: false);
            session.SelectAutomationId("SettingsSaveButton");
            WaitUntil(() => !IsWindowVisible(handle), "Turning ShowFloatingIndicator off must hide the pill.");
        });
    });

    /// <summary>
    /// Renders each of the six states through the presentation probe and asserts the pill's
    /// accessible name and its DIP geometry match the shipping layout contract for that state.
    /// </summary>
    /// <summary>
    /// The pill geometry each state must render, in DIPs, taken from the product specification
    /// rather than from the implementation's own constants — so a change to those constants fails
    /// this test instead of silently redefining the expectation.
    /// </summary>
    /// <remarks>
    /// Success is 260 rather than 220 because the probe's success text
    /// ("Probe: dictation inserted", 25 characters) deliberately exceeds the 24-character
    /// threshold, exercising the long-message widening. The error text is 23 characters, so it
    /// stays at the compact 220 and both branches are covered.
    /// </remarks>
    public static TheoryData<string, double, double> StateGeometry() => new()
    {
        { "idle", 44, 28 },
        { "preparing", 76, 22 },
        { "recording", 76, 22 },
        { "transcribing", 120, 32 },
        { "success", 260, 36 },
        { "error", 220, 36 }
    };

    [WinUiAutomationFact]
    public void Every_state_renders_with_its_documented_size_and_accessible_name() => StaRunner.Run(() =>
    {
        using var session = MuesliWinUiSession.LaunchWithIndicatorProbe();
        session.Run(() =>
        {
            foreach (var row in StateGeometry())
            {
                var label = (string)row[0];
                var expectedWidth = (double)row[1];
                var expectedHeight = (double)row[2];

                session.SetIndicatorProbeState(label);

                var indicator = WaitForIndicatorNamed(session, label);
                Assert.True(
                    indicator.Current.IsEnabled,
                    $"The {label} indicator must remain enabled for assistive technology.");

                // The pill's rect lives on the window, not on the Border inside it: a child
                // element reports NativeWindowHandle 0.
                var window = session.RequireSecondaryWindow("FloatingDictationIndicator");
                AssertPillSize(window, expectedWidth, expectedHeight, label);
                session.CaptureSecondaryWindow(window, $"winui-indicator-{label}");
            }
        });
    });

    /// <summary>
    /// A success pill returns itself to idle. This drives the real return timer, so it also proves
    /// the probe does not pin an outcome pill open.
    /// </summary>
    [WinUiAutomationFact]
    public void An_outcome_pill_returns_itself_to_idle() => StaRunner.Run(() =>
    {
        using var session = MuesliWinUiSession.LaunchWithIndicatorProbe();
        session.Run(() =>
        {
            session.SetIndicatorProbeState("success");
            WaitForIndicatorNamed(session, "success");

            // The success pill returns to idle after ~2.2s; allow slack for a loaded host.
            WaitUntil(
                () => CurrentIndicator(session)?.Current.Name.Contains("idle", StringComparison.OrdinalIgnoreCase) == true,
                "A success pill must return itself to idle after about 2.2 seconds.",
                TimeSpan.FromSeconds(20));
        });
    });

    private static AutomationElement WaitForIndicatorNamed(MuesliWinUiSession session, string label)
    {
        AutomationElement? found = null;
        WaitUntil(
            () =>
            {
                found = CurrentIndicator(session);
                return found?.Current.Name.Contains(label, StringComparison.OrdinalIgnoreCase) == true;
            },
            $"The indicator must publish a '{label}' accessible name once that state is rendered. " +
            $"Last seen: '{CurrentIndicator(session)?.Current.Name ?? "<not found>"}'.",
            TimeSpan.FromSeconds(20));
        return found!;
    }

    private static AutomationElement? CurrentIndicator(MuesliWinUiSession session)
    {
        try
        {
            var window = session.RequireSecondaryWindow("FloatingDictationIndicator");
            return window.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "FloatingDictationIndicator"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Compares the pill's on-screen rectangle against the expected DIP size, converting through
    /// the window's own DPI so the assertion holds at any display scale.
    /// </summary>
    private static void AssertPillSize(AutomationElement window, double expectedWidth, double expectedHeight, string label)
    {
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        Assert.True(handle != IntPtr.Zero, $"The {label} indicator window must expose a native handle.");
        var scale = Math.Max(1.0, GetDpiForWindow(handle) / 96.0);
        Assert.True(GetWindowRect(handle, out var rect), $"The {label} indicator must expose a window rect.");

        var widthDip = (rect.Right - rect.Left) / scale;
        var heightDip = (rect.Bottom - rect.Top) / scale;

        // Two physical pixels of rounding are allowed across the whole edge at the window's scale.
        var tolerance = 2.0 / scale;
        Assert.True(
            Math.Abs(expectedWidth - widthDip) <= tolerance,
            $"The {label} pill must be {expectedWidth} DIP wide but measured {widthDip:F2} DIP " +
            $"(at {scale:F2}x scale).");
        Assert.True(
            Math.Abs(expectedHeight - heightDip) <= tolerance,
            $"The {label} pill must be {expectedHeight} DIP tall but measured {heightDip:F2} DIP " +
            $"(at {scale:F2}x scale).");
    }

    private static void ToggleFloatingIndicator(MuesliWinUiSession session, bool turnOn)
    {
        var toggle = session.RequireAutomationId("FloatingIndicatorToggle", mustBeOnscreen: true);
        Assert.True(toggle.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern),
            "FloatingIndicatorToggle must support the Toggle pattern.");
        var current = ((TogglePattern)pattern).Current.ToggleState;
        var target = turnOn ? ToggleState.On : ToggleState.Off;
        if (current != target)
        {
            ((TogglePattern)pattern).Toggle();
            System.Threading.Thread.Sleep(250);
        }
    }

    private static void WaitUntil(Func<bool> condition, string because) =>
        WaitUntil(condition, because, TimeSpan.FromSeconds(15));

    private static void WaitUntil(Func<bool> condition, string because, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (condition()) return;
            }
            catch
            {
            }
            System.Threading.Thread.Sleep(250);
        }
        Assert.Fail(because);
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
