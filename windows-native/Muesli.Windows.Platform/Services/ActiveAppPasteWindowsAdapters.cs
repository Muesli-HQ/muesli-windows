using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Muesli.Windows.Services;

/// <summary>
/// Windows implementation of foreground-window activation and private target metadata.
/// Titles are intentionally not read; only the process name and id are exposed to the core
/// paste coordinator.
/// </summary>
public sealed class NativeWindowActivationAdapter : IWindowActivationAdapter, IWindowDescriptionAdapter
{
    public IntPtr ForegroundWindow => WindowsInputInterop.GetForegroundWindowNative();

    public bool Exists(IntPtr window) => WindowsInputInterop.IsWindowNative(window);

    public bool RequestForeground(IntPtr window) => WindowsInputInterop.FocusTargetWindow(window);

    public PasteTargetInfo DescribeWindow(IntPtr targetWindow)
    {
        if (targetWindow == IntPtr.Zero || !WindowsInputInterop.IsWindowNative(targetWindow))
        {
            return PasteTargetInfo.Unknown;
        }

        _ = GetWindowThreadProcessId(targetWindow, out var processId);
        var processName = "unknown";
        try
        {
            processName = Process.GetProcessById(checked((int)processId)).ProcessName;
        }
        catch
        {
            // The target may close between shortcut press and metadata capture.
        }

        return new PasteTargetInfo(processName, processId);
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}

public sealed class NativeKeyboardInputAdapter : IKeyboardInputAdapter
{
    public bool SendPasteShortcut() => WindowsInputInterop.SendCtrlV();

    public TextInputResult SendText(string text) => WindowsInputInterop.SendUnicodeText(text);
}

internal static class WindowsInputInterop
{
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfUnicode = 0x0004;
    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;
    private const int SwRestore = 9;
    private const int UnicodeTextBatchLength = 256;

    internal static bool SendCtrlV()
    {
        var inputs = new[]
        {
            KeyboardInput(VkControl, 0),
            KeyboardInput(VkV, 0),
            KeyboardInput(VkV, KeyeventfKeyup),
            KeyboardInput(VkControl, KeyeventfKeyup)
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    internal static TextInputResult SendUnicodeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new TextInputResult(true, false);
        }

        var mayHaveInsertedText = false;
        for (var offset = 0; offset < text.Length; offset += UnicodeTextBatchLength)
        {
            var length = Math.Min(UnicodeTextBatchLength, text.Length - offset);
            var inputs = new Input[length * 2];
            for (var index = 0; index < length; index++)
            {
                var character = text[offset + index];
                inputs[index * 2] = UnicodeKeyboardInput(character, 0);
                inputs[index * 2 + 1] = UnicodeKeyboardInput(character, KeyeventfKeyup);
            }

            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            if (sent != inputs.Length)
            {
                return new TextInputResult(false, mayHaveInsertedText || sent > 0);
            }

            mayHaveInsertedText = true;
        }

        return new TextInputResult(true, mayHaveInsertedText);
    }

    internal static bool FocusTargetWindow(IntPtr targetWindow)
    {
        if (targetWindow == IntPtr.Zero || !IsWindow(targetWindow))
        {
            return false;
        }

        if (IsIconic(targetWindow))
        {
            ShowWindow(targetWindow, SwRestore);
        }

        var foregroundWindow = GetForegroundWindow();
        var currentThread = GetCurrentThreadId();
        var foregroundThread = foregroundWindow == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foregroundWindow, out _);
        var targetThread = GetWindowThreadProcessId(targetWindow, out _);

        if (foregroundThread != 0 && foregroundThread != currentThread)
        {
            AttachThreadInput(currentThread, foregroundThread, true);
        }

        if (targetThread != 0 && targetThread != currentThread)
        {
            AttachThreadInput(currentThread, targetThread, true);
        }

        try
        {
            return SetForegroundWindow(targetWindow);
        }
        finally
        {
            if (targetThread != 0 && targetThread != currentThread)
            {
                AttachThreadInput(currentThread, targetThread, false);
            }

            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    internal static int NativeInputStructureSize => Marshal.SizeOf<Input>();

    internal static IntPtr GetForegroundWindowNative() => GetForegroundWindow();

    internal static bool IsWindowNative(IntPtr window) => IsWindow(window);

    private static Input KeyboardInput(ushort virtualKey, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInputData
            {
                VirtualKey = virtualKey,
                ScanCode = 0,
                Flags = flags,
                Time = 0,
                ExtraInfo = UIntPtr.Zero
            }
        }
    };

    private static Input UnicodeKeyboardInput(char character, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInputData
            {
                VirtualKey = 0,
                ScanCode = character,
                Flags = flags | KeyeventfUnicode,
                Time = 0,
                ExtraInfo = UIntPtr.Zero
            }
        }
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, Input[] inputs, int sizeOfInputStructure);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInputData Keyboard;

        // INPUT is a native union. MOUSEINPUT is larger than KEYBDINPUT on
        // 64-bit Windows, so represent the full union or SendInput rejects it.
        [FieldOffset(0)]
        public MouseInputData Mouse;

        [FieldOffset(0)]
        public HardwareInputData Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInputData
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }
}
