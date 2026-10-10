using System.Diagnostics;
using System.Runtime.InteropServices;
using Muesli.Windows.Core.Contracts;

namespace Muesli.Windows.Services;

/// <summary>
/// Windows low-level keyboard adapter.  It deliberately exposes only callbacks
/// and the neutral hotkey contract, keeping WPF and WinUI shells interchangeable.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmKeyup = 0x0101;
    private const int WmSyskeydown = 0x0104;
    private const int WmSyskeyup = 0x0105;

    private LowLevelKeyboardProc? _hookProc;
    private IntPtr _hookId;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private HotkeyGesture _gesture = HotkeyGestureParser.Parse("F8");
    private bool _isDown;
    private bool _escapeDown;
    private Action? _onDown;
    private Action? _onUp;
    private Func<bool>? _canCancel;
    private Action? _onCancel;
    private Action? _onOtherKey;

    public void Register(
        string gesture,
        Action onDown,
        Action onUp,
        Func<bool>? canCancel = null,
        Action? onCancel = null,
        Action? onOtherKey = null)
    {
        Dispose();
        _gesture = HotkeyGestureParser.Parse(gesture);
        if (HotkeyGestureParser.IsEscape(_gesture.VirtualKey))
        {
            throw new InvalidOperationException("Escape is reserved for cancelling dictation.");
        }

        _onDown = onDown;
        _onUp = onUp;
        _canCancel = canCancel;
        _onCancel = onCancel;
        _onOtherKey = onOtherKey;
        _hookProc = HookCallback;
        // Windows silently removes a low-level hook when its installing thread cannot pump
        // messages within one second. Keep it independent of UI, capture and inference stalls.
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _hookThread = new Thread(() => RunHook(ready))
        {
            IsBackground = true,
            Name = "Muesli keyboard hook"
        };
        _hookThread.Start();
        try { ready.Task.GetAwaiter().GetResult(); }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_hookThread is { } thread)
        {
            if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            thread.Join();
            _hookThread = null;
        }

        _isDown = false;
        _escapeDown = false;
        _hookProc = null;
    }

    private void RunHook(TaskCompletionSource ready)
    {
        try
        {
            _hookThreadId = GetCurrentThreadId();
            // Create the thread's message queue before registration/disposal can post to it.
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            _hookId = SetHook(_hookProc!);
            if (_hookId == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                    $"Could not register global keyboard hook for {_gesture.DisplayName}.");
            ready.SetResult();
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            if (!ready.TrySetException(exception))
                new AppLogService().Error("Global keyboard hook stopped unexpectedly.", exception);
        }
        finally
        {
            if (_hookId != IntPtr.Zero) UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            _hookThreadId = 0;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            var virtualKey = Marshal.ReadInt32(lParam);
            var isKeyDownMessage = message is WmKeydown or WmSyskeydown;
            var isKeyUpMessage = message is WmKeyup or WmSyskeyup;

            if (HotkeyGestureParser.IsEscape(virtualKey) &&
                (_escapeDown || (_canCancel?.Invoke() ?? false)))
            {
                if (isKeyDownMessage && !_escapeDown)
                {
                    _escapeDown = true;
                    _onCancel?.Invoke();
                }
                else if (isKeyUpMessage)
                {
                    _escapeDown = false;
                }

                return (IntPtr)1;
            }

            if (isKeyDownMessage && HotkeyGestureParser.Matches(_gesture, virtualKey, ModifierKeysFromState()))
            {
                if (!_isDown)
                {
                    _isDown = true;
                    _onDown?.Invoke();
                }

                return (IntPtr)1;
            }

            if (isKeyUpMessage && virtualKey == _gesture.VirtualKey && _isDown)
            {
                // Release must complete the gesture even if modifiers were released first.
                _isDown = false;
                _onUp?.Invoke();
                return (IntPtr)1;
            }

            if (isKeyDownMessage && _isDown && virtualKey != _gesture.VirtualKey &&
                !HotkeyGestureParser.IsEscape(virtualKey) &&
                !HotkeyGestureParser.IsGestureModifier(virtualKey))
            {
                _isDown = false;
                _onOtherKey?.Invoke();
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static HotkeyModifiers ModifierKeysFromState()
    {
        var modifiers = HotkeyModifiers.None;
        if (IsKeyDown(0x11)) modifiers |= HotkeyModifiers.Control;
        if (IsKeyDown(0x10)) modifiers |= HotkeyModifiers.Shift;
        if (IsKeyDown(0x12)) modifiers |= HotkeyModifiers.Alt;
        if (IsKeyDown(0x5B) || IsKeyDown(0x5C)) modifiers |= HotkeyModifiers.Windows;
        return modifiers;
    }

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using var currentProcess = Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule;
        return SetWindowsHookEx(WhKeyboardLl, proc, GetModuleHandle(currentModule?.ModuleName), 0);
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    private static extern bool PeekMessage(out Message message, IntPtr window, uint minimum, uint maximum, uint remove);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessage(out Message message, IntPtr window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessage(ref Message message);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
}
