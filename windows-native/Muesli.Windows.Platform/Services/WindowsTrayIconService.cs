using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Muesli.Windows.Core.Contracts;

namespace Muesli.Windows.Services;

internal enum TrayCommand
{
    None = 0,
    Open,
    ToggleDictation,
    ResumeMeeting,
    PauseMeeting,
    ToggleMeeting,
    Dictations,
    Meetings,
    Settings,
    About,
    ResumeSetup,
    FeatureTour,
    Quit
}

/// <summary>One row of the tray menu; <see cref="TrayCommand.None"/> is a separator.</summary>
internal readonly record struct TrayMenuEntry(TrayCommand Command, string Label)
{
    public static readonly TrayMenuEntry Separator = new(TrayCommand.None, string.Empty);

    public bool IsSeparator => Command == TrayCommand.None;
}

/// <summary>What the tray menu offers for a given capture state, independent of how it is drawn.</summary>
internal static class TrayMenuModel
{
    /// <summary>
    /// True when nothing takes the foreground after the menu closes: it was cancelled, or the
    /// command only changes capture state. Starting or stopping a meeting is excluded because
    /// starting one opens the recording page.
    /// </summary>
    public static bool LeavesFocusInTray(TrayCommand command) =>
        command is TrayCommand.None or TrayCommand.ToggleDictation
            or TrayCommand.PauseMeeting or TrayCommand.ResumeMeeting;

    public static IReadOnlyList<TrayMenuEntry> Build(
        bool dictationRecording,
        bool meetingRecording,
        bool meetingPaused,
        bool canResumeSetup,
        bool canReplayFeatureTour)
    {
        var entries = new List<TrayMenuEntry>
        {
            new(TrayCommand.Open, "Open Muesli"),
            new(TrayCommand.ToggleDictation, dictationRecording ? "Stop dictation" : "Start dictation")
        };
        if (meetingPaused)
        {
            entries.Add(new(TrayCommand.ResumeMeeting, "Resume meeting"));
            entries.Add(new(TrayCommand.ToggleMeeting, "Stop and save meeting"));
        }
        else if (meetingRecording)
        {
            entries.Add(new(TrayCommand.PauseMeeting, "Pause meeting"));
            entries.Add(new(TrayCommand.ToggleMeeting, "Stop and save meeting"));
        }
        else
        {
            entries.Add(new(TrayCommand.ToggleMeeting, "Start Quick Note"));
        }
        entries.Add(TrayMenuEntry.Separator);
        entries.Add(new(TrayCommand.Dictations, "Dictations"));
        entries.Add(new(TrayCommand.Meetings, "Meetings"));
        entries.Add(new(TrayCommand.Settings, "Settings"));
        entries.Add(new(TrayCommand.About, "About and diagnostics"));
        if (canResumeSetup) entries.Add(new(TrayCommand.ResumeSetup, "Resume setup"));
        if (canReplayFeatureTour) entries.Add(new(TrayCommand.FeatureTour, "Replay feature tour"));
        entries.Add(TrayMenuEntry.Separator);
        entries.Add(new(TrayCommand.Quit, "Quit"));
        return entries;
    }
}

/// <summary>
/// Windows notification-area adapter with framework-neutral callbacks. Talks to the shell directly
/// (<c>Shell_NotifyIcon</c> and a native popup menu) through a hidden window, so the app does not
/// carry Windows Forms for one icon. Construct it on the UI thread: the hidden window's messages
/// are delivered by that thread's message loop.
/// </summary>
public sealed class WindowsTrayIconService : IDisposable
{
    private const uint IconId = 1;
    private const string WindowClassName = "MuesliTrayIconWindow";

    private static readonly ConcurrentDictionary<nint, WindowsTrayIconService> Instances = new();
    // Held in a static so the delegate outlives every window that uses it.
    private static readonly NativeMethods.WndProc WindowProcedure = WndProc;
    private static readonly object ClassGate = new();
    private static bool _classRegistered;
    private static uint _taskbarCreatedMessage;

    private readonly IUiDispatcher _dispatcher;
    private readonly Func<bool> _dictationRecording;
    private readonly Func<bool> _meetingRecording;
    private readonly Func<Task> _toggleDictation;
    private readonly Func<Task> _toggleMeeting;
    private readonly Action<string> _show;
    private readonly Action _quit;
    private readonly Func<bool>? _meetingPaused;
    private readonly Func<Task>? _pauseMeeting;
    private readonly Func<Task>? _resumeMeeting;
    private readonly Action? _resumeSetup;
    private readonly Action? _featureTour;
    private readonly string? _iconPath;
    private readonly string? _lightTaskbarIconPath;
    private readonly uint _ownerThreadId;
    private readonly nint _window;
    private nint _icon;
    private bool _ownsIcon;
    private string _tip;
    private int _disposed;

    public WindowsTrayIconService(
        IUiDispatcher dispatcher,
        Func<bool> dictationRecording,
        Func<bool> meetingRecording,
        Func<Task> toggleDictation,
        Func<Task> toggleMeeting,
        Action<string> show,
        Action quit,
        string? iconPath = null,
        Func<bool>? meetingPaused = null,
        Func<Task>? pauseMeeting = null,
        Func<Task>? resumeMeeting = null,
        Action? resumeSetup = null,
        Action? featureTour = null,
        string? lightTaskbarIconPath = null)
    {
        _dispatcher = dispatcher;
        _dictationRecording = dictationRecording;
        _meetingRecording = meetingRecording;
        _toggleDictation = toggleDictation;
        _toggleMeeting = toggleMeeting;
        _show = show;
        _quit = quit;
        _meetingPaused = meetingPaused;
        _pauseMeeting = pauseMeeting;
        _resumeMeeting = resumeMeeting;
        _resumeSetup = resumeSetup;
        _featureTour = featureTour;
        _iconPath = iconPath;
        _lightTaskbarIconPath = lightTaskbarIconPath;
        _tip = AppName;
        _ownerThreadId = NativeMethods.GetCurrentThreadId();
        // Window first: if creating it throws, there is no loaded icon to leak.
        _window = CreateHiddenWindow();
        (_icon, _ownsIcon) = LoadThemedIcon();
        Instances[_window] = this;
        AddIcon();
    }

    public void Refresh() => _dispatcher.TryEnqueue(() =>
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var tip = StatusText();
        if (tip == _tip) return;
        _tip = tip;
        var data = NewIconData(NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP);
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data);
    });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var data = NewIconData(0);
        // Harmless if the icon was never added (Explorer was not running): the call just fails.
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref data);
        // A window can only be destroyed by the thread that created it.
        if (NativeMethods.GetCurrentThreadId() == _ownerThreadId) NativeMethods.DestroyWindow(_window);
        else NativeMethods.PostMessageW(_window, NativeMethods.WM_CLOSE, 0, 0);
        if (_ownsIcon && _icon != 0) NativeMethods.DestroyIcon(_icon);
        _icon = 0;
    }

    private static nint CreateHiddenWindow()
    {
        var instance = NativeMethods.GetModuleHandleW(null);
        lock (ClassGate)
        {
            if (!_classRegistered)
            {
                var windowClass = new NativeMethods.WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WindowProcedure),
                    hInstance = instance,
                    lpszClassName = WindowClassName
                };
                if (NativeMethods.RegisterClassExW(ref windowClass) == 0)
                    throw new InvalidOperationException($"Could not register the tray window class (error {Marshal.GetLastWin32Error()}).");
                // Explorer broadcasts this when the taskbar is recreated; every icon must be re-added.
                _taskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
                _classRegistered = true;
            }
        }
        // A top-level window that is never shown, not a message-only one: only top-level windows
        // receive the TaskbarCreated and theme-change broadcasts.
        var window = NativeMethods.CreateWindowExW(
            NativeMethods.WS_EX_TOOLWINDOW, WindowClassName, string.Empty, 0, 0, 0, 0, 0, 0, 0, instance, 0);
        if (window == 0)
            throw new InvalidOperationException($"Could not create the tray window (error {Marshal.GetLastWin32Error()}).");
        return window;
    }

    private static nint WndProc(nint window, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (Instances.TryGetValue(window, out var self) && self.HandleMessage(message, wParam, lParam)) return 0;
        }
        catch
        {
            // A managed exception must never unwind into user32.
        }
        return NativeMethods.DefWindowProcW(window, message, wParam, lParam);
    }

    private bool HandleMessage(uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case NativeMethods.TrayCallbackMessage:
                if (Volatile.Read(ref _disposed) != 0) return true;
                // NOTIFYICON_VERSION_4: the event is the low word of lParam, the anchor point is in wParam.
                var notification = (uint)(lParam.ToInt64() & 0xFFFF);
                if (notification == NativeMethods.WM_CONTEXTMENU)
                {
                    var packed = wParam.ToInt64();
                    ShowMenu((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
                }
                else if (notification is NativeMethods.WM_LBUTTONDBLCLK or NativeMethods.NIN_KEYSELECT)
                {
                    // Double-click, or Enter/Space on the keyboard-focused icon. The shell may send
                    // the key notification twice for Enter; opening the window twice is harmless.
                    Run(TrayCommand.Open);
                }
                return true;
            case NativeMethods.WM_SETTINGCHANGE:
                // Like the macOS menu-bar template, the tray mark follows the taskbar's light or dark theme.
                if (_lightTaskbarIconPath is not null && lParam != 0
                    && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
                {
                    ReloadIcon();
                }
                return false;
            case NativeMethods.WM_CLOSE:
                NativeMethods.DestroyWindow(_window);
                return true;
            case NativeMethods.WM_DESTROY:
                Instances.TryRemove(_window, out _);
                return false;
            default:
                if (message == _taskbarCreatedMessage && Volatile.Read(ref _disposed) == 0)
                {
                    AddIcon();
                    return true;
                }
                return false;
        }
    }

    private void AddIcon()
    {
        var data = NewIconData(NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP);
        // If Explorer is not running yet this fails, and the TaskbarCreated broadcast adds it later.
        if (!NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref data)) return;
        data.uVersion = NativeMethods.NOTIFYICON_VERSION_4;
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_SETVERSION, ref data);
    }

    private void ReloadIcon()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var (prior, ownedPrior) = (_icon, _ownsIcon);
        (_icon, _ownsIcon) = LoadThemedIcon();
        var data = NewIconData(NativeMethods.NIF_ICON);
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data);
        if (ownedPrior && prior != 0) NativeMethods.DestroyIcon(prior);
    }

    private NativeMethods.NOTIFYICONDATAW NewIconData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
        hWnd = _window,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = NativeMethods.TrayCallbackMessage,
        hIcon = _icon,
        szTip = _tip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    // The menu is built when it is opened, so it always reflects the capture state at that moment.
    private void ShowMenu(int x, int y)
    {
        var entries = TrayMenuModel.Build(
            _dictationRecording(),
            _meetingRecording(),
            _meetingPaused?.Invoke() == true,
            _resumeSetup is not null,
            _featureTour is not null);
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            foreach (var entry in entries)
            {
                if (entry.IsSeparator) NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, 0, null);
                else NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, (nuint)(int)entry.Command, entry.Label);
            }
            // Without foreground ownership the menu would not close when the user clicks elsewhere.
            NativeMethods.SetForegroundWindow(_window);
            var chosen = NativeMethods.TrackPopupMenuEx(
                menu,
                NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_NONOTIFY,
                x, y, _window, 0);
            NativeMethods.PostMessageW(_window, NativeMethods.WM_NULL, 0, 0);
            var command = (TrayCommand)chosen;
            if (command != TrayCommand.None) Run(command);
            // Hand keyboard focus back to the notification area, as the shell contract asks, when
            // the menu was cancelled with Escape or the command opens no window. Commands that
            // show a window must keep the foreground or that window could not come to the front.
            // If the user dismissed the menu by clicking another window, that window is already
            // in the foreground and keeps the focus.
            if (TrayMenuModel.LeavesFocusInTray(command) && NativeMethods.GetForegroundWindow() == _window)
            {
                var data = NewIconData(0);
                NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_SETFOCUS, ref data);
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    private void Run(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.Open: Show("timeline"); break;
            case TrayCommand.ToggleDictation: _dispatcher.TryEnqueue(() => _ = _toggleDictation()); break;
            case TrayCommand.ResumeMeeting:
                _dispatcher.TryEnqueue(() => _ = _resumeMeeting?.Invoke() ?? Task.CompletedTask);
                break;
            case TrayCommand.PauseMeeting:
                _dispatcher.TryEnqueue(() => _ = _pauseMeeting?.Invoke() ?? Task.CompletedTask);
                break;
            case TrayCommand.ToggleMeeting: _dispatcher.TryEnqueue(() => _ = _toggleMeeting()); break;
            case TrayCommand.Dictations: Show("dictations"); break;
            case TrayCommand.Meetings: Show("meetings"); break;
            case TrayCommand.Settings: Show("settings"); break;
            case TrayCommand.About: Show("about"); break;
            case TrayCommand.ResumeSetup: if (_resumeSetup is not null) _dispatcher.TryEnqueue(_resumeSetup); break;
            case TrayCommand.FeatureTour: if (_featureTour is not null) _dispatcher.TryEnqueue(_featureTour); break;
            case TrayCommand.Quit: _dispatcher.TryEnqueue(_quit); break;
        }
    }

    // Hovering the tray icon must disclose any active capture.
    private string StatusText() =>
        _meetingPaused?.Invoke() == true ? $"{AppName} · Meeting paused"
        : _meetingRecording() ? $"{AppName} · Recording meeting"
        : _dictationRecording() ? $"{AppName} · Dictating"
        : AppName;

    // "Muesli Dev A" in a development slot, so its tray icon is not mistaken for the main build.
    private static string AppName => Muesli.Windows.Core.Profiles.MuesliDevSlot.AppDisplayName;

    private void Show(string destination) => _dispatcher.TryEnqueue(() => _show(destination));

    private (nint Icon, bool Owned) LoadThemedIcon() =>
        LoadIcon(_lightTaskbarIconPath is not null && TaskbarUsesLightTheme() ? _lightTaskbarIconPath : _iconPath);

    private static bool TaskbarUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
    }

    private static (nint Icon, bool Owned) LoadIcon(string? iconPath)
    {
        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
        {
            var icon = NativeMethods.LoadImageW(
                0, iconPath, NativeMethods.IMAGE_ICON,
                NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON),
                NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSMICON),
                NativeMethods.LR_LOADFROMFILE);
            if (icon != 0) return (icon, true);
        }
        // The stock application icon is shared by the system and must not be destroyed.
        return (NativeMethods.LoadIconW(0, NativeMethods.IDI_APPLICATION), false);
    }

    private static class NativeMethods
    {
        public const uint WM_NULL = 0x0000;
        public const uint WM_DESTROY = 0x0002;
        public const uint WM_CLOSE = 0x0010;
        public const uint WM_SETTINGCHANGE = 0x001A;
        public const uint WM_CONTEXTMENU = 0x007B;
        public const uint WM_LBUTTONDBLCLK = 0x0203;
        public const uint TrayCallbackMessage = 0x8000 + 1; // WM_APP + 1

        public const uint NIM_ADD = 0;
        public const uint NIM_MODIFY = 1;
        public const uint NIM_DELETE = 2;
        public const uint NIM_SETFOCUS = 3;
        public const uint NIM_SETVERSION = 4;
        public const uint NIF_MESSAGE = 0x01;
        public const uint NIF_ICON = 0x02;
        public const uint NIF_TIP = 0x04;
        public const uint NIF_SHOWTIP = 0x80;
        public const uint NOTIFYICON_VERSION_4 = 4;
        public const uint NIN_KEYSELECT = 0x0401; // NIN_SELECT | NINF_KEY

        public const uint MF_STRING = 0x0000;
        public const uint MF_SEPARATOR = 0x0800;
        public const uint TPM_RIGHTBUTTON = 0x0002;
        public const uint TPM_NONOTIFY = 0x0080;
        public const uint TPM_RETURNCMD = 0x0100;

        public const uint WS_EX_TOOLWINDOW = 0x00000080;
        public const uint IMAGE_ICON = 1;
        public const uint LR_LOADFROMFILE = 0x0010;
        public const int SM_CXSMICON = 49;
        public const int SM_CYSMICON = 50;
        public const nint IDI_APPLICATION = 32512;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public nint lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public nint hInstance;
            public nint hIcon;
            public nint hCursor;
            public nint hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public nint hIconSm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATAW
        {
            public uint cbSize;
            public nint hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public nint hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public nint hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        public static extern ushort RegisterClassExW(ref WNDCLASSEXW windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint RegisterWindowMessageW(string name);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        public static extern nint CreateWindowExW(
            uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyWindow(nint window);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(nint window);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern nint GetForegroundWindow();

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern nint CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AppendMenuW(nint menu, uint flags, nuint itemId, string? text);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyMenu(nint menu);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern nint LoadImageW(nint instance, string name, uint type, int width, int height, uint load);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern nint LoadIconW(nint instance, nint iconName);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(nint icon);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int GetSystemMetrics(int index);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern nint GetModuleHandleW(string? moduleName);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern uint GetCurrentThreadId();
    }
}
