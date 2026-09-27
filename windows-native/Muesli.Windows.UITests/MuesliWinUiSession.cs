using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Muesli.Windows.UITests;

/// <summary>
/// Out-of-process WinUI 3 shell session: launches the shell against an isolated temporary
/// profile, attaches UI Automation by process id, and captures a screenshot when an assertion
/// fails.
/// </summary>
/// <remarks>
/// The default launch is the supported packaged host: <c>winapp run</c> builds, registers the
/// development package, and activates the app by AUMID. Packaged activation cannot inherit
/// <c>MUESLI_PROFILE_ROOT</c> from the launcher environment, so the isolated profile is passed
/// as <c>--profile-root</c> instead. <see cref="LaunchUnpackaged(bool, bool)"/> keeps the
/// previous loose-executable path for the tests that explicitly qualify no-package-identity
/// behavior and executable-level single-instance redirection.
/// </remarks>
internal sealed class MuesliWinUiSession : IDisposable
{
    private const int ShowNormal = 1;
    private const int AllowAnyProcess = -1;
    private const uint SetWindowPosShow = 0x0040;
    private const uint SetWindowPosNoMove = 0x0002;
    private const uint SetWindowPosNoSize = 0x0001;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    private readonly MuesliCleanProfile _profile;
    private readonly Process _process;
    private readonly bool _nonActivatingShell;
    private AutomationElement? _window;
    private bool _disposed;

    private MuesliWinUiSession(MuesliCleanProfile profile, Process process, AutomationElement window, bool nonActivatingShell = false)
    {
        _profile = profile;
        _process = process;
        _window = window;
        _nonActivatingShell = nonActivatingShell;
        Directory.CreateDirectory(UiScreenshot.ArtifactDirectory);
        if (!_nonActivatingShell)
        {
            ActivateMainWindow();
        }
    }

    public int ProcessId => _process.Id;
    public string ExecutablePath { get; private init; } = "";
    public string ProfileRoot => _profile.MuesliRoot;
    public double CurrentDpiScale => GetDpiScale(GetNativeWindowHandle());
    /// <summary>
    /// True when Windows High Contrast is on, or when qualification opts in with
    /// <c>MUESLI_WINUI_HIGH_CONTRAST=1</c>. The harness never changes the OS contrast theme.
    /// </summary>
    public bool HighContrastAutomationAvailable =>
        string.Equals(
            Environment.GetEnvironmentVariable("MUESLI_WINUI_HIGH_CONTRAST"),
            "1",
            StringComparison.OrdinalIgnoreCase) ||
        IsOsHighContrast();
    public IList<string> VisitedPages { get; } = new List<string>();

    public AutomationElement Window =>
        _window ?? throw new InvalidOperationException("The WinUI shell window is no longer available.");

    /// <summary>Resizes the native window to the requested effective DIPs.</summary>
    /// <remarks>
    /// Win32 sizing APIs use physical pixels. The harness converts from DIPs using the current
    /// per-window DPI rather than assuming 96 DPI, which keeps the reference viewport stable on
    /// both standard and scaled displays.
    /// </remarks>
    public void ResizeDips(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Window dimensions must be positive.");

        var handle = GetNativeWindowHandle();
        var scale = GetDpiScale(handle);
        var widthPixels = checked((int)Math.Round(width * scale));
        var heightPixels = checked((int)Math.Round(height * scale));
        if (!MoveWindow(handle, GetWindowLeft(handle), GetWindowTop(handle), widthPixels, heightPixels, true))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The WinUI shell window could not be resized.");
        WaitForIdle();
    }

    public (int Width, int Height) ReadEffectiveDipSize()
    {
        var handle = GetNativeWindowHandle();
        var scale = GetDpiScale(handle);
        if (!GetWindowRect(handle, out var bounds))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The WinUI shell window bounds could not be read.");
        return ((int)Math.Round((bounds.Right - bounds.Left) / scale),
                (int)Math.Round((bounds.Bottom - bounds.Top) / scale));
    }

    public static MuesliWinUiSession Launch(bool seedDeterministicMeeting = false)
        => Launch(seedDeterministicMeeting, seedPopulatedData: false);

    public static MuesliWinUiSession Launch(bool seedDeterministicMeeting, bool seedPopulatedData)
        => LaunchPackaged(new MuesliCleanProfile(seedDeterministicMeeting, seedPopulatedData));

    /// <summary>
    /// Launches the supported packaged host through the WinApp CLI. The isolated profile is
    /// passed as <c>--profile-root</c> because AUMID activation does not inherit the launcher's
    /// environment variables.
    /// </summary>
    /// <summary>
    /// Launches the packaged shell with the floating indicator's presentation probe armed, so the
    /// states that are otherwise reachable only through real microphone capture and transcription
    /// can be rendered and asserted. A probe-driven state is a simulated presentation state, never
    /// evidence that capture or transcription works.
    /// </summary>
    /// <summary>
    /// Launches the packaged shell with the floating indicator visible and <b>no</b> presentation
    /// probe, so the real global hook and the real packaged microphone drive every state. Used by
    /// the end-to-end shortcut qualification.
    /// </summary>
    public static MuesliWinUiSession LaunchWithIndicatorShown()
        => LaunchPackaged(
            new MuesliCleanProfile(false, false, completeFirstRun: true, showFloatingIndicator: true));

    public static MuesliWinUiSession LaunchWithIndicatorProbe()
        => LaunchPackaged(
            new MuesliCleanProfile(false, false, completeFirstRun: true, showFloatingIndicator: true),
            indicatorProbe: true);

    /// <summary>
    /// Launches the packaged shell with the development-only deterministic meeting-notification
    /// preview armed. The preview never persists a fake meeting and never starts capture.
    /// </summary>
    public static MuesliWinUiSession LaunchWithMeetingNotificationPreview(string state = "active", bool background = false)
    {
        var extra = $"--preview-meeting-notification={state}";
        if (background) extra += " --background";
        return LaunchPackaged(
            new MuesliCleanProfile(false, false, completeFirstRun: true),
            extraArgs: extra,
            shellProofId: background ? "MeetingNotificationTitle" : "MainNavigation",
            nonActivatingShell: background,
            attachCompanion: background);
    }

    /// <summary>
    /// The command-line flag that arms the indicator's presentation probe, and the profile file it
    /// watches. These mirror <c>Muesli.Windows.Core.Services.FloatingIndicatorProbe</c>; this
    /// harness deliberately takes no project reference on the product, so the contract is restated
    /// here rather than imported.
    /// </summary>
    private const string IndicatorProbeFlag = "--indicator-probe";
    private const string IndicatorProbeStateFile = "indicator-probe.state";

    /// <summary>
    /// Asks the running shell to render <paramref name="state"/> on the floating indicator, by
    /// name ("idle", "preparing", "recording", "transcribing", "success", "error"). The session
    /// must have been started by <see cref="LaunchWithIndicatorProbe"/>.
    /// </summary>
    public void SetIndicatorProbeState(string state) =>
        File.WriteAllText(Path.Combine(ProfileRoot, IndicatorProbeStateFile), state);

    private static MuesliWinUiSession LaunchPackaged(
        MuesliCleanProfile profile,
        bool indicatorProbe = false,
        string? extraArgs = null,
        string shellProofId = "MainNavigation",
        bool nonActivatingShell = false,
        bool attachCompanion = false)
    {
        try
        {
            ThrowIfAlreadyRunning();
            profile.AssertIsolatedFromDeveloperProfile();
            var process = StartPackagedShell(profile, indicatorProbe, extraArgs);
            try
            {
                var window = attachCompanion
                    ? WaitForCompanionWindow(process, TimeSpan.FromSeconds(90), shellProofId)
                    : WaitForShellWindow(process, TimeSpan.FromSeconds(90), shellProofId);
                return new MuesliWinUiSession(profile, process, window,
                    nonActivatingShell: nonActivatingShell || shellProofId != "MainNavigation") { ExecutablePath = "" };
            }
            catch (Exception exception)
            {
                var detail = ReadStartupLog(profile.MuesliRoot);
                try { UiScreenshot.Capture(null, "winui-attach-failed"); } catch { }
                TryKill(process);
                profile.Dispose();
                throw new InvalidOperationException(
                    $"UI Automation could not attach to the packaged WinUI shell. {exception.GetType().Name}: {exception.Message}{detail}",
                    exception);
            }
        }
        catch
        {
            profile.Dispose();
            throw;
        }
    }

    private static Process StartPackagedShell(MuesliCleanProfile profile, bool indicatorProbe = false, string? extraArgs = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = ResolveWinAppCli(),
            WorkingDirectory = TestPaths.RepositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add(TestPaths.WinUiProjectPath);
        start.ArgumentList.Add("--no-restore");
        start.ArgumentList.Add("--detach");
        start.ArgumentList.Add("--json");
        start.ArgumentList.Add("--property");
        start.ArgumentList.Add("Platform=x64");
        start.ArgumentList.Add("--args");
        var forwarded = $"--profile-root \"{profile.MuesliRoot}\"";
        if (indicatorProbe) forwarded += $" {IndicatorProbeFlag}";
        if (!string.IsNullOrWhiteSpace(extraArgs)) forwarded += $" {extraArgs}";
        start.ArgumentList.Add(forwarded);

        using var launcher = Process.Start(start)
                             ?? throw new InvalidOperationException("The WinApp CLI did not start.");
        // Drain both pipes while the build runs. Waiting first can deadlock when MSBuild
        // fills either redirected pipe, leaving the UI qualification waiting for itself.
        var standardOutput = launcher.StandardOutput.ReadToEndAsync();
        var standardError = launcher.StandardError.ReadToEndAsync();
        if (!launcher.WaitForExit(240_000))
        {
            try { launcher.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("winapp run did not return within 240 seconds.");
        }

        var output = standardOutput.GetAwaiter().GetResult() + standardError.GetAwaiter().GetResult();
        if (launcher.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"winapp run failed with exit code {launcher.ExitCode}.{Environment.NewLine}{Truncate(output)}");
        }

        var match = System.Text.RegularExpressions.Regex.Match(output, "\"ProcessId\"\\s*:\\s*(\\d+)");
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"winapp run did not report a launched process id.{Environment.NewLine}{Truncate(output)}");
        }

        try
        {
            return Process.GetProcessById(int.Parse(match.Groups[1].Value));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"The packaged WinUI shell (pid {match.Groups[1].Value}) exited before the harness could attach.", exception);
        }
    }

    private static string ResolveWinAppCli()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;
            try
            {
                var candidate = Path.Combine(directory.Trim(), "winapp.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
            }
        }

        return "winapp";
    }

    private static string Truncate(string output) =>
        output.Length <= 4000 ? output : output[^4000..];

    /// <summary>
    /// Launches the unpackaged loose-executable build against an isolated profile. Only tests
    /// that qualify behavior without package identity (or executable-level redirection) should
    /// use this path; everything else must use the packaged default.
    /// </summary>
    public static MuesliWinUiSession LaunchUnpackaged(bool seedDeterministicMeeting = false, bool seedPopulatedData = false)
    {
        var exe = TestPaths.TryFindWinUiExecutable()
                  ?? throw new FileNotFoundException(
                      "The unpackaged WinUI shell was not found. Run scripts/run-winui-preview.ps1 first.");
        ThrowIfAlreadyRunning();

        var profile = new MuesliCleanProfile(seedDeterministicMeeting, seedPopulatedData);
        profile.AssertIsolatedFromDeveloperProfile();

        var start = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false
        };
        start.Environment["MUESLI_PROFILE_ROOT"] = profile.MuesliRoot;

        Process process;
        try
        {
            process = Process.Start(start)
                      ?? throw new InvalidOperationException("Process.Start returned null for the WinUI shell.");
        }
        catch
        {
            profile.Dispose();
            throw;
        }

        try
        {
            var window = WaitForShellWindow(process, TimeSpan.FromSeconds(90));
            return new MuesliWinUiSession(profile, process, window) { ExecutablePath = exe };
        }
        catch (Exception exception)
        {
            var detail = ReadStartupLog(profile.MuesliRoot);
            try { UiScreenshot.Capture(null, "winui-attach-failed"); } catch { }
            TryKill(process);
            profile.Dispose();
            throw new InvalidOperationException(
                $"UI Automation could not attach to the WinUI shell. {exception.GetType().Name}: {exception.Message}{detail}",
                exception);
        }
    }

    public static MuesliWinUiSession LaunchPopulated() => Launch(false, seedPopulatedData: true);

    /// <summary>
    /// Launches the packaged shell against a profile that has not completed first-run setup,
    /// so the onboarding window opens over the dashboard.
    /// </summary>
    public static MuesliWinUiSession LaunchOnboarding()
        => LaunchPackaged(new MuesliCleanProfile(completeFirstRun: false));

    /// <summary>
    /// Finds a top-level window of this process — other than the main shell window — that
    /// contains the given automation id. Secondary windows (onboarding, confirmation prompts)
    /// are separate top-level trees, so they cannot be found through <see cref="Window"/>.
    /// </summary>
    /// <remarks>
    /// TEST-04: this used to walk <c>AutomationElement.RootElement.FindAll(TreeScope.Children,
    /// …)</c>, a desktop-wide UIA query that has to marshal into every top-level window on the
    /// desktop before the process filter can be applied. On a busy desktop that threw
    /// <c>COMException 0x80131505 (Operation timed out)</c> and failed the onboarding test even
    /// though the window was open. It now discovers HWNDs with <c>EnumWindows</c> and attaches
    /// per handle with <see cref="AutomationElement.FromHandle"/> — the same technique
    /// <see cref="WaitForShellWindow"/> already uses — so only this process's own windows are
    /// ever queried. The assertion is unchanged: the window must still publish
    /// <paramref name="evidenceAutomationId"/> in its own tree within the timeout.
    /// </remarks>
    public AutomationElement RequireSecondaryWindow(string evidenceAutomationId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        var lastFailure = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            ThrowIfProcessExited();

            var shellHandle = IntPtr.Zero;
            try
            {
                if (_window is not null) shellHandle = new IntPtr(_window.Current.NativeWindowHandle);
            }
            catch (ElementNotAvailableException)
            {
            }

            var handles = new List<IntPtr>();
            EnumWindows((handle, _) =>
            {
                GetWindowThreadProcessId(handle, out var owner);
                if (owner == _process.Id && handle != shellHandle) handles.Add(handle);
                return true;
            }, IntPtr.Zero);

            foreach (var handle in handles)
            {
                try
                {
                    var window = AutomationElement.FromHandle(handle);
                    if (window.FindFirst(
                            TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.AutomationIdProperty, evidenceAutomationId)) is not null)
                        return window;
                }
                catch (ElementNotAvailableException)
                {
                }
                catch (COMException exception)
                {
                    // A window can be torn down between EnumWindows and the query; keep polling.
                    lastFailure = $" Last provider error: {exception.Message}";
                }
            }

            Thread.Sleep(250);
        }

        Fail($"No window of the WinUI shell published automation id '{evidenceAutomationId}'.{lastFailure}");
        throw new InvalidOperationException("Unreachable");
    }

    public AutomationElement RequireCompanionWindow(string evidenceAutomationId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            ThrowIfProcessExited();
            var window = FindCompanionWindow(evidenceAutomationId);
            if (window is not null) return window;
            Thread.Sleep(250);
        }
        Fail($"No WPF companion window published automation id '{evidenceAutomationId}'.");
        throw new InvalidOperationException("Unreachable");
    }

    private static AutomationElement WaitForCompanionWindow(Process process, TimeSpan timeout, string evidenceAutomationId)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) throw new InvalidOperationException("The WinUI shell exited before its WPF notification appeared.");
            var window = FindCompanionWindow(evidenceAutomationId);
            if (window is not null) return window;
            Thread.Sleep(250);
        }
        throw new TimeoutException($"Timed out waiting for WPF notification '{evidenceAutomationId}'.");
    }

    private static AutomationElement? FindCompanionWindow(string evidenceAutomationId)
    {
        var handles = new List<IntPtr>();
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var owner);
            try
            {
                using var process = Process.GetProcessById(unchecked((int)owner));
                if (process.ProcessName == "Muesli.Windows.Indicator.Wpf") handles.Add(handle);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            return true;
        }, IntPtr.Zero);
        foreach (var handle in handles)
        {
            try
            {
                var window = AutomationElement.FromHandle(handle);
                if (window.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, evidenceAutomationId)) is not null)
                    return window;
            }
            catch (ElementNotAvailableException) { }
            catch (COMException) { }
        }
        return null;
    }

    /// <summary>
    /// Foregrounds a secondary window (best effort) and captures it. PrintWindow renders
    /// without foreground, but activation keeps the capture honest on composited surfaces.
    /// </summary>
    public string CaptureSecondaryWindow(AutomationElement window, string slug)
    {
        try
        {
            var handle = new IntPtr(window.Current.NativeWindowHandle);
            if (handle != IntPtr.Zero)
            {
                ShowWindow(handle, ShowNormal);
                BringWindowToTop(handle);
                SetForegroundWindow(handle);
                WaitForIdle();
            }
        }
        catch (ElementNotAvailableException)
        {
            Fail($"The secondary window for '{slug}' disappeared before it could be captured.");
        }

        return UiScreenshot.Capture(window, slug);
    }

    public void Run(Action body)
    {
        try
        {
            if (!_nonActivatingShell)
            {
                ActivateMainWindow();
            }
            body();
        }
        catch
        {
            TryCapture("winui-test-failure");
            throw;
        }
    }

    /// <summary>
    /// Selects a navigation entry and asserts the destination page rendered by requiring an
    /// automation id that only that page publishes.
    /// </summary>
    public void NavigateTo(string pageKey, string navigationAutomationId, string pageEvidenceAutomationId)
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                SelectAutomationId(navigationAutomationId);
                if (TryRequirePageSentinel(pageEvidenceAutomationId, TimeSpan.FromSeconds(6)))
                {
                    if (!VisitedPages.Contains(pageKey, StringComparer.Ordinal))
                        VisitedPages.Add(pageKey);
                    return;
                }

                lastFailure = new TimeoutException(
                    $"The page sentinel '{pageEvidenceAutomationId}' did not appear after selecting '{navigationAutomationId}'.");
            }
            catch (Exception exception)
            {
                lastFailure = exception;
            }

            // Navigation can replace the Frame content asynchronously. Re-selecting the nav item
            // gives the shell another bounded opportunity to settle, while still failing instead
            // of capturing the old page if the destination never publishes its sentinel.
            if (attempt < 3)
                Thread.Sleep(500);
        }

        Fail($"Navigation to '{pageKey}' never published page sentinel '{pageEvidenceAutomationId}' after three attempts. {lastFailure?.Message}");
    }

    /// <summary>
    /// Revalidates a destination immediately before a screenshot. This closes the race where a
    /// navigation item changes selection before its Frame content has replaced the old page.
    /// </summary>
    public string CaptureReference(string slug, string pageKey, string pageEvidenceAutomationId)
    {
        if (!TryRequirePageSentinel(pageEvidenceAutomationId, TimeSpan.FromSeconds(8)))
            Fail($"Refusing to capture '{slug}': page '{pageKey}' did not publish sentinel '{pageEvidenceAutomationId}'.");

        EnsureForegroundForCapture(slug);
        return Capture(slug);
    }

    private void EnsureForegroundForCapture(string slug)
    {
        var handle = GetNativeWindowHandle();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            ActivateMainWindow();
            if (GetForegroundWindow() == handle)
                return;
            Thread.Sleep(100);
        }

        var foreground = GetForegroundWindow();
        Fail($"Refusing to capture '{slug}': WinUI window 0x{handle.ToInt64():X} did not remain foreground (actual 0x{foreground.ToInt64():X}).");
    }

    public void RequirePageSentinel(string pageKey, string pageEvidenceAutomationId)
    {
        if (!TryRequirePageSentinel(pageEvidenceAutomationId, TimeSpan.FromSeconds(8)))
            Fail($"Page '{pageKey}' did not publish sentinel '{pageEvidenceAutomationId}'.");
    }

    private bool TryRequirePageSentinel(string automationId, TimeSpan timeout)
    {
        var element = TryFindAutomationId(automationId, timeout);
        if (element is null)
            return false;

        if (element.Current.IsOffscreen)
        {
            ScrollIntoView(element);
            if (element.Current.IsOffscreen)
                return false;
        }

        return true;
    }

    public AutomationElement RequireAutomationId(string automationId, bool mustBeOnscreen = false)
    {
        var element = FindByAutomationId(automationId, TimeSpan.FromSeconds(20));
        if (mustBeOnscreen && element.Current.IsOffscreen)
        {
            ScrollIntoView(element);
            element = FindByAutomationId(automationId, TimeSpan.FromSeconds(5));
            if (element.Current.IsOffscreen)
                Fail($"Automation id '{automationId}' exists but is off-screen.");
        }

        return element;
    }

    /// <summary>
    /// Brings an element inside a scrolled page into view. Long pages place real controls below
    /// the fold, and an assertion about visibility should measure the control, not the scroll
    /// position it happened to start at.
    /// </summary>
    private void ScrollIntoView(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scrollItem))
            {
                ((ScrollItemPattern)scrollItem).ScrollIntoView();
                WaitForIdle();
                return;
            }

            for (var ancestor = TreeWalker.ControlViewWalker.GetParent(element);
                 ancestor is not null;
                 ancestor = TreeWalker.ControlViewWalker.GetParent(ancestor))
            {
                if (!ancestor.TryGetCurrentPattern(ScrollPattern.Pattern, out var scroll))
                    continue;

                var pattern = (ScrollPattern)scroll;
                if (!pattern.Current.VerticallyScrollable)
                    continue;

                // Step down rather than jumping to the end so an element in the middle of a long
                // page is found at the first scroll offset that actually reveals it.
                for (var percent = 0d; percent <= 100d; percent += 20d)
                {
                    pattern.SetScrollPercent(ScrollPattern.NoScroll, percent);
                    WaitForIdle();
                    if (!element.Current.IsOffscreen)
                        return;
                }

                return;
            }
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (InvalidOperationException)
        {
            // The element or its scroll container was replaced mid-navigation.
        }
    }

    public void RequireAccessibleName(string name)
    {
        _ = FindByName(name, TimeSpan.FromSeconds(20));
    }

    /// <summary>
    /// Asserts an automation id is absent from the shell's UI Automation tree. A collapsed
    /// WinUI element is removed from the tree, so this is how a hidden panel is qualified.
    /// </summary>
    public void RequireAbsentAutomationId(string automationId, string because)
    {
        var element = TryFindAutomationId(automationId, TimeSpan.FromSeconds(3));
        if (element is not null && !element.Current.IsOffscreen)
            Fail($"Automation id '{automationId}' is present and on-screen, but {because}");
    }

    public AutomationElement? TryFindAutomationId(string automationId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ThrowIfProcessExited();
            try
            {
                var element = Window.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
                element ??= FindRaw(Window, current => current.Current.AutomationId == automationId);
                if (element is not null)
                    return element;
            }
            catch (ElementNotAvailableException)
            {
                _window = WaitForShellWindow(_process, TimeSpan.FromSeconds(15));
            }

            Thread.Sleep(200);
        }

        return null;
    }

    /// <summary>
    /// Opens an <c>Expander</c> and waits for its content to be realized. A collapsed Expander
    /// keeps its content out of the UIA tree entirely, so a test that asserts controls inside one
    /// must open it first rather than drop the assertion. <see cref="TryInvokeOrSelect"/> cannot do
    /// this: an Expander's header exposes ExpandCollapse, not Invoke or SelectionItem.
    /// </summary>
    public void ExpandAutomationId(string automationId)
    {
        var element = FindByAutomationId(automationId, TimeSpan.FromSeconds(20));
        for (var candidate = element;
             candidate is not null;
             candidate = TreeWalker.ControlViewWalker.GetParent(candidate))
        {
            if (!candidate.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern))
                continue;

            var expandCollapse = (ExpandCollapsePattern)pattern;
            if (expandCollapse.Current.ExpandCollapseState != ExpandCollapseState.Expanded)
                expandCollapse.Expand();
            WaitForIdle();
            return;
        }

        Fail($"Automation id '{automationId}' does not support ExpandCollapse.");
    }

    /// <summary>
    /// Invokes a control that lives in one of the shell's popup windows rather than in the shell
    /// window itself. WinUI hosts a <c>MenuFlyout</c> in its own top-level
    /// <c>PopupWindowSiteBridge</c> HWND, so <see cref="SelectAutomationId"/> - which searches the
    /// shell window's descendants - cannot see the items. Pair it with
    /// <see cref="RequireSecondaryWindow"/>, which already enumerates only this process's windows.
    /// </summary>
    public void SelectAutomationIdIn(AutomationElement window, string automationId)
    {
        var element = window.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        if (element is null)
        {
            Fail($"Automation id '{automationId}' was not found in the supplied window.");
            return;
        }

        if (!TryInvokeOrSelect(element))
            Fail($"Automation id '{automationId}' supports neither Invoke nor SelectionItem.");
    }

    public void SelectAutomationId(string automationId)
    {
        var element = FindByAutomationId(automationId, TimeSpan.FromSeconds(20));
        if (TryInvokeOrSelect(element))
            return;

        for (var parent = TreeWalker.ControlViewWalker.GetParent(element);
             parent is not null;
             parent = TreeWalker.ControlViewWalker.GetParent(parent))
        {
            if (TryInvokeOrSelect(parent))
                return;
        }

        Fail($"Automation id '{automationId}' supports neither Invoke nor SelectionItem.");
    }

    /// <summary>
    /// Expands a combo box and selects the item with the given text. WinUI hosts the drop-down
    /// in a popup outside the combo box's own subtree, so the item is searched from the window.
    /// </summary>
    public void SelectAccessibleName(string name)
    {
        var element = FindByName(name, TimeSpan.FromSeconds(20));
        if (TryInvokeOrSelect(element))
            return;

        for (var parent = TreeWalker.ControlViewWalker.GetParent(element);
             parent is not null;
             parent = TreeWalker.ControlViewWalker.GetParent(parent))
        {
            if (TryInvokeOrSelect(parent))
                return;
        }

        Fail($"Accessible name '{name}' supports neither Invoke nor SelectionItem.");
    }

    /// <summary>
    /// Opens a CommandBar's overflow menu. Secondary commands are intentionally unrealized until
    /// the platform-owned More button is opened, so callers must open it before asserting those
    /// commands rather than weakening the command assertion.
    /// </summary>
    public void OpenCommandBarOverflow(string commandBarName)
    {
        var commandBar = FindByName(commandBarName, TimeSpan.FromSeconds(8));
        var overflow = FindRaw(commandBar, current =>
        {
            try
            {
                var name = current.Current.Name;
                return current.Current.ControlType == ControlType.Button &&
                       (name.Contains("More", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("overflow", StringComparison.OrdinalIgnoreCase));
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
        });

        // Some WinUI builds do not expose the overflow button under the CommandBar's raw subtree;
        // constrain the fallback to a button with the platform's known More label.
        overflow ??= FindRaw(Window, current =>
        {
            try
            {
                var name = current.Current.Name;
                return current.Current.ControlType == ControlType.Button &&
                       (name.Equals("More", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("More buttons", StringComparison.OrdinalIgnoreCase));
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
        });

        if (overflow is null)
            Fail($"CommandBar '{commandBarName}' did not expose a platform overflow button.");
        if (!TryInvokeOrSelect(overflow))
            Fail($"CommandBar '{commandBarName}' overflow button could not be invoked.");
        WaitForIdle();
    }

    public void SelectComboBoxItem(string automationId, string itemText)
    {
        var combo = FindByAutomationId(automationId, TimeSpan.FromSeconds(20));
        if (!combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandCollapse))
            Fail($"Automation id '{automationId}' is not an expandable combo box.");

        var pattern = (ExpandCollapsePattern)expandCollapse;
        pattern.Expand();
        WaitForIdle();
        try
        {
            var item = FindByName(itemText, TimeSpan.FromSeconds(10));
            if (!TryInvokeOrSelect(item))
                Fail($"Combo box item '{itemText}' could not be selected.");
        }
        finally
        {
            try
            {
                if (pattern.Current.ExpandCollapseState == ExpandCollapseState.Expanded)
                    pattern.Collapse();
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            WaitForIdle();
        }
    }

    public void SetValueAutomationId(string automationId, string value)
    {
        var element = FindByAutomationId(automationId, TimeSpan.FromSeconds(20));
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            Fail($"Automation id '{automationId}' does not support ValuePattern.");
        ((ValuePattern)pattern).SetValue(value);
        WaitForIdle();
    }

    public string ReadValueAutomationId(string automationId)
    {
        var element = FindByAutomationId(automationId, TimeSpan.FromSeconds(20));
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            Fail($"Automation id '{automationId}' does not support ValuePattern.");
        return ((ValuePattern)pattern).Current.Value;
    }

    /// <summary>Moves UIA focus through named controls and verifies each control accepts focus.</summary>
    public void AssertKeyboardFocusTraversal(params string[] automationIds)
    {
        if (automationIds is null || automationIds.Length == 0)
            throw new ArgumentException("At least one control is required.", nameof(automationIds));

        foreach (var automationId in automationIds)
        {
            var element = RequireAutomationId(automationId, mustBeOnscreen: true);
            element.SetFocus();
            WaitForIdle();
            var focused = AutomationElement.FocusedElement;
            if (focused is null || !IsSelfOrDescendant(focused, element))
                Fail($"Keyboard focus did not reach automation id '{automationId}'.");
        }
    }

    private static bool IsSelfOrDescendant(AutomationElement candidate, AutomationElement expected)
    {
        try
        {
            if (candidate.Equals(expected) || candidate.Current.AutomationId == expected.Current.AutomationId)
                return true;
            for (var parent = TreeWalker.ControlViewWalker.GetParent(candidate);
                 parent is not null;
                 parent = TreeWalker.ControlViewWalker.GetParent(parent))
            {
                if (parent.Equals(expected) || parent.Current.AutomationId == expected.Current.AutomationId)
                    return true;
            }
        }
        catch (ElementNotAvailableException)
        {
        }

        return false;
    }

    public string Capture(string slug) => UiScreenshot.Capture(_window, slug);

    /// <summary>Reads the shell's own log so a crash is reported with its stack, not just a timeout.</summary>
    public string ReadLog() => ReadStartupLog(_profile.MuesliRoot);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _window = null;
        TryKill(_process);
        _profile.Dispose();
    }

    private static void ThrowIfAlreadyRunning()
    {
        var existing = Process.GetProcessesByName("Muesli.Windows.WinUI");
        try
        {
            if (existing.Length > 0)
            {
                throw new InvalidOperationException(
                    $"{existing.Length} WinUI shell process(es) are already running. The shell is single-instance, " +
                    "so a stray process would redirect the harness launch. Close it before running WinUI automation.");
            }
        }
        finally
        {
            foreach (var process in existing)
                process.Dispose();
        }
    }

    private static string ReadStartupLog(string profileRoot)
    {
        try
        {
            var directory = Path.Combine(profileRoot, "logs");
            if (!Directory.Exists(directory))
                return " No shell log was written.";
            var log = new DirectoryInfo(directory)
                .EnumerateFiles("*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            if (log is null)
                return " No shell log was written.";
            var text = File.ReadAllText(log.FullName);
            return string.IsNullOrWhiteSpace(text)
                ? " The shell log is empty."
                : $"{Environment.NewLine}--- {log.Name} ---{Environment.NewLine}{text}";
        }
        catch (IOException exception)
        {
            return $" The shell log could not be read: {exception.Message}";
        }
    }

    private static AutomationElement WaitForShellWindow(Process process, TimeSpan timeout, string proofAutomationId = "MainNavigation")
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The WinUI shell exited before its window appeared (exit {process.ExitCode}).");
            }

            try
            {
                // Discover HWNDs first so an unrelated desktop UIA provider cannot block
                // attachment. Only query automation within the process launched by this test.
                var handles = new List<IntPtr>();
                EnumWindows((handle, _) =>
                {
                    GetWindowThreadProcessId(handle, out var owner);
                    if (owner == process.Id) handles.Add(handle);
                    return true;
                }, IntPtr.Zero);
                foreach (var handle in handles)
                {
                    try
                    {
                        var window = AutomationElement.FromHandle(handle);
                        var proof = window.FindFirst(
                            TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.AutomationIdProperty, proofAutomationId));
                        if (proof is not null)
                            return window;
                    }
                    catch (ElementNotAvailableException)
                    {
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException($"Timed out waiting for the WinUI shell window for pid {process.Id} (proof '{proofAutomationId}').");
    }

    private AutomationElement FindByAutomationId(string automationId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ThrowIfProcessExited();
            try
            {
                var element = Window.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
                element ??= FindRaw(Window, current => current.Current.AutomationId == automationId);
                if (element is not null)
                    return element;
            }
            catch (ElementNotAvailableException)
            {
                _window = WaitForShellWindow(_process, TimeSpan.FromSeconds(15));
            }

            Thread.Sleep(200);
        }

        Fail($"Automation id '{automationId}' was not found in the WinUI shell.");
        throw new InvalidOperationException("Unreachable");
    }

    private AutomationElement FindByName(string name, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ThrowIfProcessExited();
            try
            {
                var element = Window.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, name));
                element ??= FindRaw(Window, current => current.Current.Name == name);
                if (element is not null)
                    return element;
            }
            catch (ElementNotAvailableException)
            {
                _window = WaitForShellWindow(_process, TimeSpan.FromSeconds(15));
            }

            Thread.Sleep(200);
        }

        Fail($"Accessible name '{name}' was not found in the WinUI shell.");
        throw new InvalidOperationException("Unreachable");
    }

    private static AutomationElement? FindRaw(AutomationElement root, Func<AutomationElement, bool> predicate)
    {
        var walker = TreeWalker.RawViewWalker;
        var pending = new Stack<AutomationElement>();
        pending.Push(root);
        var remaining = 6000;
        while (pending.Count > 0 && remaining-- > 0)
        {
            var current = pending.Pop();
            try
            {
                if (predicate(current))
                    return current;
                for (var child = walker.GetFirstChild(current); child is not null; child = walker.GetNextSibling(child))
                    pending.Push(child);
            }
            catch (ElementNotAvailableException)
            {
            }
        }

        return null;
    }

    private static bool TryInvokeOrSelect(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            {
                ((InvokePattern)invoke).Invoke();
                WaitForIdle();
                return true;
            }

            if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
            {
                ((SelectionItemPattern)selection).Select();
                WaitForIdle();
                return true;
            }
        }
        catch (ElementNotAvailableException)
        {
            // The page is replacing its visual tree after a navigation.
        }

        return false;
    }

    private static void WaitForIdle() => Thread.Sleep(350);

    private void ActivateMainWindow()
    {
        try
        {
            _process.Refresh();
            var handle = _process.MainWindowHandle;
            if (handle == IntPtr.Zero)
                handle = Window.Current.NativeWindowHandle;

            if (handle != 0)
            {
                ShowWindow(handle, ShowNormal);
                SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SetWindowPosShow | SetWindowPosNoMove | SetWindowPosNoSize);
                SetWindowPos(handle, HwndNotTopmost, 0, 0, 0, 0, SetWindowPosShow | SetWindowPosNoMove | SetWindowPosNoSize);
                BringWindowToTop(handle);

                // UIA can find a background window while another app owns the desktop. Attach
                // the test input queue temporarily so foreground activation is deterministic.
                var currentThread = GetCurrentThreadId();
                var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
                var targetThread = GetWindowThreadProcessId(handle, out _);
                if (foregroundThread != 0 && foregroundThread != currentThread)
                    AttachThreadInput(currentThread, foregroundThread, true);
                if (targetThread != 0 && targetThread != currentThread)
                    AttachThreadInput(currentThread, targetThread, true);
                try
                {
                    AllowSetForegroundWindow(AllowAnyProcess);
                    SetForegroundWindow(handle);
                    SwitchToThisWindow(handle, true);
                }
                finally
                {
                    if (targetThread != 0 && targetThread != currentThread)
                        AttachThreadInput(currentThread, targetThread, false);
                    if (foregroundThread != 0 && foregroundThread != currentThread)
                        AttachThreadInput(currentThread, foregroundThread, false);
                }
            }

            SetFocusWithRetry();
        }
        catch (ElementNotAvailableException)
        {
            _window = WaitForShellWindow(_process, TimeSpan.FromSeconds(15));
            SetFocusWithRetry();
        }
    }

    /// <summary>
    /// <see cref="AutomationElement.SetFocus"/> throws
    /// <see cref="InvalidOperationException"/> ("Target element cannot receive focus") when another
    /// process owns the foreground at that instant, which on this machine happened on roughly one
    /// unpackaged launch in three and failed
    /// <c>Startup_registration_is_reported_unavailable_without_package_identity</c> even though the
    /// shell had demonstrably started (its startup log line was present in the failure detail).
    /// This is the TEST-04 robustness class from the Prompt 0 baseline. Retrying re-activates the
    /// window and tries again; it does not relax any assertion the test makes afterwards, and a
    /// window that genuinely never becomes focusable still fails here.
    /// </summary>
    private void SetFocusWithRetry(int attempts = 5)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Window.SetFocus();
                return;
            }
            catch (InvalidOperationException) when (attempt < attempts)
            {
                var handle = GetNativeWindowHandle();
                if (handle != IntPtr.Zero)
                {
                    ShowWindow(handle, ShowNormal);
                    BringWindowToTop(handle);
                    AllowSetForegroundWindow(AllowAnyProcess);
                    SetForegroundWindow(handle);
                }

                Thread.Sleep(600);
            }
        }
    }

    private IntPtr GetNativeWindowHandle()
    {
        _process.Refresh();
        var handle = _process.MainWindowHandle;
        if (handle == IntPtr.Zero)
            handle = Window.Current.NativeWindowHandle;
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("The WinUI shell has no native window handle.");
        return handle;
    }

    private static double GetDpiScale(IntPtr handle)
    {
        var dpi = GetDpiForWindow(handle);
        return Math.Max(1d, dpi / 96d);
    }

    private static int GetWindowLeft(IntPtr handle) => GetWindowRect(handle, out var bounds) ? bounds.Left : 0;
    private static int GetWindowTop(IntPtr handle) => GetWindowRect(handle, out var bounds) ? bounds.Top : 0;

    private void ThrowIfProcessExited()
    {
        if (_process.HasExited)
            Fail($"The WinUI shell exited during the test (exit {_process.ExitCode}).{ReadStartupLog(_profile.MuesliRoot)}");
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Fail(string message)
    {
        TryCapture("winui-assertion");
        Assert.Fail(message);
        throw new InvalidOperationException("Unreachable");
    }

    private void TryCapture(string slug)
    {
        try
        {
            UiScreenshot.Capture(_window, slug);
        }
        catch
        {
            // A capture failure must not mask the assertion that triggered it.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch
        {
            // The shell may already be gone.
        }
        finally
        {
            process.Dispose();
        }
    }

    private delegate bool EnumWindowCallback(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool SystemParametersInfo(int uiAction, int uiParam, ref HighContrastInfo pvParam, int fWinIni);

    private const int SpiGetHighContrast = 66;
    private const int HighContrastOnFlag = 0x0001;

    private static bool IsOsHighContrast()
    {
        var info = new HighContrastInfo { Size = Marshal.SizeOf<HighContrastInfo>() };
        return SystemParametersInfo(SpiGetHighContrast, info.Size, ref info, 0)
            && (info.Flags & HighContrastOnFlag) == HighContrastOnFlag;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct HighContrastInfo
    {
        public int Size;
        public int Flags;
        public IntPtr DefaultScheme;
    }
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hWnd, out WindowRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct WindowRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}
