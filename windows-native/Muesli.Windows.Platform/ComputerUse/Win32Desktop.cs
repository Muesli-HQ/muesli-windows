using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace Muesli.Windows.ComputerUse;

/// <summary>A top-level window Computer Use can target.</summary>
public sealed record CuaWindow(long Id, string AppName, string AppId, int ProcessId, string Title, CuaRect Frame, bool IsForeground);

/// <summary>Win32 window enumeration, app identity and screen capture for Computer Use.</summary>
public static class Win32Desktop
{
    private static readonly int OwnProcessId = Environment.ProcessId;

    /// <summary>Processes whose windows Computer Use never observes or targets (Muesli itself).</summary>
    public static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Muesli.Windows.WinUI", "Muesli.Windows.Indicator.Wpf",
    };

    public static IReadOnlyList<CuaWindow> ListWindows()
    {
        var foreground = GetForegroundWindow();
        var windows = new List<CuaWindow>();
        EnumWindows((handle, _) =>
        {
            if (Describe(handle, foreground) is { } window) windows.Add(window);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    /// <summary>The window the user is working in: the foreground window, or the topmost
    /// targetable window when Muesli itself is in front.</summary>
    public static CuaWindow? UserWindow() =>
        ListWindows() is var windows && windows.Count > 0
            ? windows.FirstOrDefault(window => window.IsForeground) ?? windows[0]
            : null;

    public static CuaWindow? Describe(IntPtr handle) => Describe(handle, GetForegroundWindow());

    private static CuaWindow? Describe(IntPtr handle, IntPtr foreground)
    {
        if (!IsWindowVisible(handle) || GetWindow(handle, GwOwner) != IntPtr.Zero) return null;
        if ((GetWindowLongPtr(handle, GwlExStyle).ToInt64() & WsExToolWindow) != 0) return null;
        if (DwmGetWindowAttribute(handle, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return null;
        var title = WindowTitle(handle);
        if (title.Length == 0) return null;
        var frame = WindowFrame(handle);
        if (frame is null || frame.Width < 40 || frame.Height < 40) return null;
        var (processId, appId, appName) = Identity(handle);
        if (processId == 0 || processId == OwnProcessId || ExcludedProcesses.Contains(appId)) return null;
        return new CuaWindow(handle.ToInt64(), appName, appId, processId, title, frame, handle == foreground);
    }

    /// <summary>Process behind a window. UWP apps live in ApplicationFrameHost with the real app
    /// in a child window owned by a different process.</summary>
    public static (int ProcessId, string AppId, string AppName) Identity(IntPtr handle)
    {
        _ = GetWindowThreadProcessId(handle, out var processId);
        var (appId, appName) = ProcessNames((int)processId);
        if (appId.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            var childProcess = 0u;
            EnumChildWindows(handle, (child, parameter) =>
            {
                _ = GetWindowThreadProcessId(child, out var candidate);
                if (candidate == processId) return true;
                childProcess = candidate;
                return false;
            }, IntPtr.Zero);
            if (childProcess != 0)
            {
                processId = childProcess;
                (appId, appName) = ProcessNames((int)processId);
            }
        }
        return ((int)processId, appId, appName);
    }

    private static (string AppId, string AppName) ProcessNames(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var name = process.ProcessName;
            string? description = null;
            try { description = process.MainModule?.FileVersionInfo.FileDescription; } catch { /* elevated or protected */ }
            return (name, string.IsNullOrWhiteSpace(description) ? name : description.Trim());
        }
        catch { return ("", ""); }
    }

    public static string WindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0) return "";
        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    /// <summary>Visible window bounds in physical screen pixels (excludes the invisible resize border).</summary>
    public static CuaRect? WindowFrame(IntPtr handle)
    {
        if (DwmGetWindowAttribute(handle, DwmwaExtendedFrameBounds, out NativeRect rect, Marshal.SizeOf<NativeRect>()) != 0 &&
            !GetWindowRect(handle, out rect)) return null;
        return new CuaRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public static CuaRect CursorPosition() =>
        GetCursorPos(out var point) ? new CuaRect(point.X, point.Y, 0, 0) : new CuaRect(0, 0, 0, 0);

    public static bool IsAlive(long windowId) => IsWindow(new IntPtr(windowId));

    public static bool IsForeground(long windowId) => GetForegroundWindow() == new IntPtr(windowId);

    /// <summary>The top-level window under a screen point (0 when none).</summary>
    public static long RootWindowAt(double x, double y) =>
        GetAncestor(WindowFromPoint(new NativePoint { X = (int)Math.Round(x), Y = (int)Math.Round(y) }), GaRoot).ToInt64();

    private const uint GaRoot = 2;
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr handle, uint flags);

    /// <summary>
    /// Restores and foregrounds a window. Windows only lets the process that received the last
    /// input change the foreground, so a zero-distance mouse move first gives this process that
    /// right. (The common Alt-tap trick is avoided: the Alt reaches the newly focused app and puts
    /// it in menu key-tip mode, turning the next typed letters into shortcuts.)
    /// </summary>
    public static bool Activate(long windowId)
    {
        var handle = new IntPtr(windowId);
        if (!IsWindow(handle)) return false;
        if (IsIconic(handle)) ShowWindow(handle, SwRestore);
        if (GetForegroundWindow() == handle) return true;
        WindowsInput.NudgeMouse();
        SetForegroundWindow(handle);
        if (!WaitForeground(handle))
        {
            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var ownThread = GetCurrentThreadId();
            AttachThreadInput(ownThread, foregroundThread, true);
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
            AttachThreadInput(ownThread, foregroundThread, false);
        }
        return WaitForeground(handle);
    }

    private static bool WaitForeground(IntPtr handle)
    {
        for (var attempt = 0; attempt < 20 && GetForegroundWindow() != handle; attempt++) Thread.Sleep(25);
        return GetForegroundWindow() == handle;
    }

    private const int SwRestore = 9;
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachInput);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    /// <summary>Copies the on-screen pixels of a rectangle (the window must be visible).</summary>
    public static Bitmap CaptureScreen(CuaRect frame)
    {
        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        var area = Rectangle.Intersect(virtualScreen,
            new Rectangle((int)frame.X, (int)frame.Y, (int)frame.Width, (int)frame.Height));
        if (area.Width <= 0 || area.Height <= 0) throw new InvalidOperationException("The window is off screen.");
        var bitmap = new Bitmap(area.Width, area.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(area.Location, Point.Empty, area.Size);
        return bitmap;
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }

    private const int GwOwner = 4;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x80;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, int command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out NativeRect value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
}
