using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Muesli.Windows.UITests;

/// <summary>
/// Out-of-process production-startup session: launches the built Muesli.exe against a
/// clean temp profile, attaches UI Automation by process id, and captures a screenshot
/// on failure. Never uses Phase 12 visual-preview arguments.
/// </summary>
internal sealed class MuesliUiSession : IDisposable
{
    private sealed class ExistingMuesliProcessException(string message) : InvalidOperationException(message);

    public const string SqliteHistoryCutoverVariable = "MUESLI_SQLITE_HISTORY_CUTOVER";

    private readonly MuesliCleanProfile _profile;
    private Process _process;
    private readonly bool _enableSqliteHistoryCutover;
    private AutomationElement? _window;
    private bool _disposed;

    private MuesliUiSession(
        MuesliCleanProfile profile,
        Process process,
        AutomationElement window,
        bool enableSqliteHistoryCutover)
    {
        _profile = profile;
        _process = process;
        _enableSqliteHistoryCutover = enableSqliteHistoryCutover;
        _window = window;
        Directory.CreateDirectory(UiScreenshot.ArtifactDirectory);
        VisitedPages.Add("dashboard");
        ActivateMainWindow();
    }

    public int ProcessId => _process.Id;
    public string ExecutablePath { get; private init; } = "";
    public IList<string> VisitedPages { get; } = new List<string>();
    public string ProfileMuesliRoot => _profile.MuesliRoot;
    public AutomationElement Window => _window ?? throw new InvalidOperationException("The Muesli window is no longer available.");

    public static MuesliUiSession LaunchProduction(
        bool enableSqliteHistoryCutover = false,
        bool seedDeterministicMeeting = false)
    {
        var exe = TestPaths.MuesliExecutable;
        ThrowIfExistingFrom(exe);
        var profile = new MuesliCleanProfile(seedDeterministicMeeting);
        profile.AssertIsolatedFromDeveloperProfile();

        var start = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false
        };
        // Pin the child to the harness profile explicitly. The production shell now honors this
        // profile contract, so UI automation does not depend on the Windows known-folder cache or
        // accidentally open the developer's real history while exercising the SQLite cutover.
        start.Environment["MUESLI_PROFILE_ROOT"] = profile.MuesliRoot;
        if (enableSqliteHistoryCutover)
            start.Environment[SqliteHistoryCutoverVariable] = "1";

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("Process.Start returned null for Muesli.exe.");
        }
        catch
        {
            profile.Dispose();
            throw;
        }

        try
        {
            var pid = process.Id;
            var window = WaitForMainWindow(process, TimeSpan.FromSeconds(90));
            var session = new MuesliUiSession(
                profile,
                process,
                window,
                enableSqliteHistoryCutover)
            { ExecutablePath = exe };
            session.AssertCleanProfileWasUsed();
            return session;
        }
        catch (Exception exception)
        {
            var pid = process.HasExited ? "exited" : process.Id.ToString();
            try { UiScreenshot.Capture(null, "attach-failed"); } catch { }
            TryKill(process);
            profile.Dispose();
            throw new InvalidOperationException(
                $"UI Automation could not attach to the launched Muesli.exe (pid {pid}). {exception.GetType().Name}: {exception.Message}",
                exception);
        }
    }

    public void RequireAccessibleName(string name, bool mustBeOnscreen = false)
    {
        var element = FindByName(name, TimeSpan.FromSeconds(15));
        if (mustBeOnscreen && element.Current.IsOffscreen)
            Fail($"Accessible name '{name}' exists but is off-screen.");
    }

    public void Invoke(string name)
    {
        SelectOrInvoke(name);
    }

    public void InvokeAutomationId(string automationId)
    {
        EnsureForegroundForInteraction($"invoke '{automationId}'");
        var element = FindByAutomationId(automationId, TimeSpan.FromSeconds(10));
        if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            Fail($"Automation id '{automationId}' does not support Invoke.");
        ((InvokePattern)invoke).Invoke();
        WaitForIdle();
    }

    public void SetValueAutomationId(string automationId, string value)
    {
        EnsureForegroundForInteraction($"set value on '{automationId}'");
        var element = FindByAutomationId(automationId, TimeSpan.FromSeconds(10));
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            Fail($"Automation id '{automationId}' does not support ValuePattern.");
        ((ValuePattern)pattern).SetValue(value);
        WaitForIdle();
    }

    public string ReadValueAutomationId(string automationId)
    {
        var element = FindByAutomationId(automationId, TimeSpan.FromSeconds(10));
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            Fail($"Automation id '{automationId}' does not support ValuePattern.");
        return ((ValuePattern)pattern).Current.Value;
    }

    public void ClickAutomationId(string automationId)
    {
        EnsureForegroundForInteraction($"click '{automationId}'");
        ClickElement(
            FindByAutomationId(automationId, TimeSpan.FromSeconds(10)),
            $"Automation id '{automationId}'");
    }

    public void ClickAccessibleName(string name)
    {
        EnsureForegroundForInteraction($"click '{name}'");
        ClickElement(
            FindByName(name, TimeSpan.FromSeconds(10)),
            $"Accessible name '{name}'");
    }

    /// <summary>
    /// Clicks the named element with the physical pointer, without first trying Invoke or
    /// SelectionItem. Some surfaces carry their open action on a raw <c>MouseLeftButtonUp</c>
    /// handler on a <c>Border</c> inside a <c>ListBoxItem</c>: the item exposes SelectionItem,
    /// so <see cref="ClickAccessibleName"/>'s pattern-first path selects the row and returns
    /// without ever running the handler. The meeting cards on the WPF Meetings page are one
    /// (<c>Features/Meetings/MeetingsView.xaml:107-118</c>), so opening a meeting the way a user
    /// does needs the pointer path explicitly rather than an assertion that tolerates the row
    /// merely being selected.
    /// </summary>
    public void ClickAccessibleNameWithPointer(string name)
    {
        EnsureForegroundForInteraction($"pointer-click '{name}'");
        ClickElementWithPointer(
            FindByName(name, TimeSpan.FromSeconds(10)),
            $"Accessible name '{name}'");
    }

    private void ClickElement(AutomationElement element, string description)
    {
        if (TryInvokeOrSelect(element))
        {
            return;
        }

        // WPF templates often expose a named TextBlock inside a clickable Button or
        // ListBoxItem. UIA gives that child no Invoke pattern or clickable point, so walk
        // its control ancestors before falling back to a physical click.
        for (var parent = TreeWalker.ControlViewWalker.GetParent(element);
             parent is not null;
             parent = TreeWalker.ControlViewWalker.GetParent(parent))
        {
            if (TryInvokeOrSelect(parent))
                return;
        }

        ClickElementWithPointer(element, description);
    }

    private void ClickElementWithPointer(AutomationElement element, string description)
    {
        if (!element.Current.IsOffscreen && element.TryGetClickablePoint(out var point))
        {
            SetCursorPos((int)point.X, (int)point.Y);
            mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
            WaitForIdle();
            return;
        }

        Fail($"{description} is not invokable and has no clickable point.");
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
            // The page may be replacing its visual tree after a navigation or selection.
        }

        return false;
    }

    public void RestartProduction()
    {
        ThrowIfProcessExited();
        TryKill(_process);
        var start = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!,
            UseShellExecute = false
        };
        start.Environment["MUESLI_PROFILE_ROOT"] = _profile.MuesliRoot;
        if (_enableSqliteHistoryCutover)
            start.Environment[SqliteHistoryCutoverVariable] = "1";
        _process = Process.Start(start)
                   ?? throw new InvalidOperationException("Restarted Muesli.exe Process.Start returned null.");
        try
        {
            _window = WaitForMainWindow(_process, TimeSpan.FromSeconds(90));
            AssertCleanProfileWasUsed();
        }
        catch
        {
            TryKill(_process);
            _window = null;
            throw;
        }
    }

    public void SelectOrInvoke(string name)
    {
        EnsureForegroundForInteraction($"select or invoke '{name}'");
        var element = FindByName(name, TimeSpan.FromSeconds(10));
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            ((InvokePattern)invoke).Invoke();
        else if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
            ((SelectionItemPattern)selection).Select();
        else
            Fail($"Element '{name}' supports neither Invoke nor SelectionItem.");
        WaitForIdle();
    }

    public void NavigateTo(string pageKey, string navigationName, string pageEvidenceName)
    {
        Invoke(navigationName);
        RequireAccessibleName(pageEvidenceName, mustBeOnscreen: true);
        if (!VisitedPages.Contains(pageKey, StringComparer.Ordinal))
            VisitedPages.Add(pageKey);
    }

    public void TabUntilNamedFocus(int maximumTabs = 12)
    {
        ActivateMainWindow();
        Window.SetFocus();
        WaitForIdle();
        for (var i = 0; i < maximumTabs; i++)
        {
            SendKeys.SendWait("{TAB}");
            WaitForIdle();
            AutomationElement? focused;
            try
            {
                focused = AutomationElement.FocusedElement;
            }
            catch (ElementNotAvailableException)
            {
                continue;
            }

            if (focused is null)
                continue;
            if (focused.Current.ProcessId != ProcessId)
                continue;
            if (!string.IsNullOrWhiteSpace(focused.Current.Name))
                return;
        }

        Fail("Keyboard traversal did not land on a Muesli element with an accessible name.");
    }

    public Process LaunchSecondInstance()
    {
        var start = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!,
            UseShellExecute = false
        };
        start.Environment["MUESLI_PROFILE_ROOT"] = _profile.MuesliRoot;
        if (_enableSqliteHistoryCutover)
            start.Environment[SqliteHistoryCutoverVariable] = "1";
        return Process.Start(start) ?? throw new InvalidOperationException("Second Muesli.exe Process.Start returned null.");
    }

    public void Run(Action body)
    {
        try
        {
            // UI Automation can attach to a background HWND when another desktop app owns
            // the foreground. Bring the test's own dashboard forward before every workflow;
            // otherwise clickable-point assertions are measuring the app behind the user desktop.
            ActivateMainWindow();
            body();
        }
        catch
        {
            TryCapture("test-failure");
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _window = null;
        TryKill(_process);
        _profile.Dispose();
    }

    private AutomationElement FindByName(string name, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        ElementNotAvailableException? lastUnavailable = null;
        while (DateTime.UtcNow < deadline)
        {
            ThrowIfProcessExited();
            try
            {
                var element = Window.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, name));
                element ??= FindByNameRaw(Window, name);
                if (element is not null)
                    return element;
            }
            catch (ElementNotAvailableException exception)
            {
                lastUnavailable = exception;
                _window = WaitForMainWindow(_process, TimeSpan.FromSeconds(15));
            }

            Thread.Sleep(200);
        }

        Fail($"Accessible name '{name}' was not found.{(lastUnavailable is null ? "" : " The window became unavailable while searching.")}");
        throw new InvalidOperationException("Unreachable");
    }

    private AutomationElement FindByAutomationId(string automationId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        ElementNotAvailableException? lastUnavailable = null;
        while (DateTime.UtcNow < deadline)
        {
            ThrowIfProcessExited();
            try
            {
                var element = Window.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
                element ??= FindByAutomationIdRaw(Window, automationId);
                if (element is not null)
                    return element;
            }
            catch (ElementNotAvailableException exception)
            {
                lastUnavailable = exception;
                _window = WaitForMainWindow(_process, TimeSpan.FromSeconds(15));
            }

            Thread.Sleep(200);
        }

        Fail($"Automation id '{automationId}' was not found.{(lastUnavailable is null ? "" : " The window became unavailable while searching.")}");
        throw new InvalidOperationException("Unreachable");
    }

    private static AutomationElement? FindByAutomationIdRaw(AutomationElement root, string automationId)
    {
        var walker = TreeWalker.RawViewWalker;
        var pending = new Stack<AutomationElement>();
        pending.Push(root);
        var remaining = 4000;
        while (pending.Count > 0 && remaining-- > 0)
        {
            var current = pending.Pop();
            try
            {
                if (string.Equals(current.Current.AutomationId, automationId, StringComparison.Ordinal))
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

    private static AutomationElement? FindByNameRaw(AutomationElement root, string name)
    {
        var walker = TreeWalker.RawViewWalker;
        var pending = new Stack<AutomationElement>();
        pending.Push(root);
        var remaining = 4000;
        while (pending.Count > 0 && remaining-- > 0)
        {
            var current = pending.Pop();
            try
            {
                if (string.Equals(current.Current.Name, name, StringComparison.Ordinal))
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

    public void ReattachMainWindow()
    {
        _window = WaitForMainWindow(_process, TimeSpan.FromSeconds(20));
    }

    public void AssertCleanProfileWasUsed()
    {
        _profile.AssertIsolatedFromDeveloperProfile();
        var logs = Path.Combine(_profile.MuesliRoot, "logs");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && !Directory.Exists(logs))
            Thread.Sleep(200);
        Assert.True(
            Directory.Exists(logs),
            "Production startup did not write logs into the redirected APPDATA profile. The harness must not use the developer %APPDATA%\\muesli directory.");
    }

    private static AutomationElement WaitForMainWindow(Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Muesli.exe exited before the dashboard window appeared (exit {process.ExitCode}). Another UI instance may own the per-user mutex.");
            }

            try
            {
                var windows = AutomationElement.RootElement.FindAll(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
                foreach (AutomationElement window in windows)
                {
                    AutomationElement? nav;
                    try
                    {
                        nav = window.FindFirst(
                            TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.NameProperty, "Navigate to Dictations"));
                    }
                    catch (ElementNotAvailableException)
                    {
                        continue;
                    }

                    if (nav is not null)
                        return window;
                }
            }
            catch (ElementNotAvailableException)
            {
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException($"Timed out waiting for the Muesli dashboard window for pid {process.Id}.");
    }

    private void ActivateMainWindow()
    {
        try
        {
            _process.Refresh();
            var handle = _process.MainWindowHandle;
            if (handle == IntPtr.Zero)
            {
                handle = Window.Current.NativeWindowHandle;
            }

            if (handle != 0)
            {
                ShowWindow(handle, ShowNormal);
                // UIA can find a minimized/background WPF window while another desktop app
                // owns the foreground. Temporarily making it topmost guarantees the click
                // coordinates and screenshot bounds refer to the app under test.
                SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SetWindowPosShow | SetWindowPosNoMove | SetWindowPosNoSize);
                SetWindowPos(handle, HwndNotTopmost, 0, 0, 0, 0, SetWindowPosShow | SetWindowPosNoMove | SetWindowPosNoSize);
                BringWindowToTop(handle);

                // Windows normally limits foreground changes to the process that owns the
                // current input queue. Temporarily attach the test queue to both windows so
                // UIA clicks cannot land on whatever browser or meeting window was foreground.
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

            Window.SetFocus();
        }
        catch (ElementNotAvailableException)
        {
            _window = WaitForMainWindow(_process, TimeSpan.FromSeconds(15));
            Window.SetFocus();
        }
    }

    private void EnsureForegroundForInteraction(string operation)
    {
        ActivateMainWindow();
        var handle = GetNativeWindowHandle();
        if (handle == IntPtr.Zero || GetForegroundWindow() != handle)
        {
            var actual = GetForegroundWindow();
            Fail($"Refusing to {operation}: Muesli window 0x{handle.ToInt64():X} is not foreground (actual 0x{actual.ToInt64():X}).");
        }
    }

    private IntPtr GetNativeWindowHandle()
    {
        _process.Refresh();
        var handle = _process.MainWindowHandle;
        if (handle == IntPtr.Zero)
            handle = Window.Current.NativeWindowHandle;
        return handle;
    }

    private void ThrowIfProcessExited()
    {
        if (_process.HasExited)
            Fail($"Muesli.exe exited during the test (exit {_process.ExitCode}).");
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Fail(string message)
    {
        TryDumpOwnButtonNames();
        TryCapture("assertion");
        Assert.Fail(message);
    }

    private void TryDumpOwnButtonNames()
    {
        try
        {
            Directory.CreateDirectory(UiScreenshot.ArtifactDirectory);
            var names = new List<string>();
            var buttons = Window.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement button in buttons)
            {
                try
                {
                    var name = button.Current.Name;
                    if (!string.IsNullOrWhiteSpace(name))
                        names.Add(name);
                }
                catch (ElementNotAvailableException)
                {
                }
            }

            File.WriteAllLines(Path.Combine(UiScreenshot.ArtifactDirectory, "own-buttons.txt"), names);
        }
        catch
        {
        }
    }

    private void TryCapture(string slug)
    {
        try
        {
            UiScreenshot.Capture(_window, slug);
        }
        catch
        {
        }
    }

    private static void WaitForIdle() => Thread.Sleep(800);

    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    private const int ShowNormal = 5;
    private const int AllowAnyProcess = -1;
    private const uint SetWindowPosNoSize = 0x0001;
    private const uint SetWindowPosNoMove = 0x0002;
    private const uint SetWindowPosShow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    internal static void ThrowIfExistingFrom(string exePath)
    {
        var full = Path.GetFullPath(exePath);
        foreach (var candidate in Process.GetProcessesByName("Muesli"))
        {
            try
            {
                var path = candidate.MainModule?.FileName;
                if (path is not null && string.Equals(Path.GetFullPath(path), full, StringComparison.OrdinalIgnoreCase))
                {
                    candidate.Dispose();
                    throw new ExistingMuesliProcessException(
                        $"L09 UI Automation will not terminate an existing Muesli.exe at '{full}'. Close that process and rerun with {UiAutomationEnvironment.EnableVariable}=1.");
                }
                candidate.Dispose();
            }
            catch (ExistingMuesliProcessException)
            {
                throw;
            }
            catch (Exception)
            {
                candidate.Dispose();
            }
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
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            process.Dispose();
        }
    }
}
