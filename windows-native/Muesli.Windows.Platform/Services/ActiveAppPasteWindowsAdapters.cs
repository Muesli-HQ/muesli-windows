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

        return new PasteTargetInfo(processName, processId, IntegrityLevel(processId) > OwnIntegrityLevel.Value);
    }

    private static readonly Lazy<int> OwnIntegrityLevel = new(() => IntegrityLevel((uint)Environment.ProcessId));

    /// <summary>Mandatory integrity RID (medium 0x2000, high 0x3000). An elevated process whose token a
    /// medium process cannot open reads as high; one Muesli cannot open at all reads as unknown (0).</summary>
    private static int IntegrityLevel(uint processId)
    {
        const uint processQueryLimitedInformation = 0x1000, tokenQuery = 0x0008;
        const int tokenIntegrityLevel = 25, errorAccessDenied = 5, high = 0x3000;
        var process = OpenProcess(processQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return 0;
        try
        {
            if (!OpenProcessToken(process, tokenQuery, out var token))
                return Marshal.GetLastWin32Error() == errorAccessDenied ? high : 0;
            try
            {
                _ = GetTokenInformation(token, tokenIntegrityLevel, IntPtr.Zero, 0, out var size);
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!GetTokenInformation(token, tokenIntegrityLevel, buffer, size, out _)) return 0;
                    var sid = Marshal.ReadIntPtr(buffer); // TOKEN_MANDATORY_LABEL.Label.Sid
                    var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                    return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <param name="pasteShortcut">The saved paste keys; some terminals paste only with Ctrl+Shift+V or Shift+Insert.</param>
public sealed class NativeKeyboardInputAdapter(Func<string>? pasteShortcut = null) : IKeyboardInputAdapter
{
    public bool SendPasteShortcut() => WindowsInputInterop.SendPaste(pasteShortcut?.Invoke());

    public TextInputResult SendText(string text) => WindowsInputInterop.SendUnicodeText(text);
}

internal static class WindowsInputInterop
{
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfUnicode = 0x0004;
    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;
    private const ushort VkShift = 0x10;
    private const ushort VkInsert = 0x2D;
    private const uint KeyeventfExtendedKey = 0x0001;
    private const int SwRestore = 9;
    private const int UnicodeTextBatchLength = 256;

    /// <summary>Ctrl+C, for apps that expose no UI Automation text selection.</summary>
    internal static bool SendCopy()
    {
        const ushort vkC = 0x43;
        var inputs = new[]
        {
            KeyboardInput(VkControl, 0), KeyboardInput(vkC, 0),
            KeyboardInput(vkC, KeyeventfKeyup), KeyboardInput(VkControl, KeyeventfKeyup)
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    internal static bool SendPaste(string? shortcut)
    {
        var inputs = shortcut switch
        {
            "ctrl-shift-v" => new[]
            {
                KeyboardInput(VkControl, 0), KeyboardInput(VkShift, 0), KeyboardInput(VkV, 0),
                KeyboardInput(VkV, KeyeventfKeyup), KeyboardInput(VkShift, KeyeventfKeyup), KeyboardInput(VkControl, KeyeventfKeyup)
            },
            // Insert is an extended key; without the flag Windows reads it as numpad 0.
            "shift-insert" => new[]
            {
                KeyboardInput(VkShift, 0), KeyboardInput(VkInsert, KeyeventfExtendedKey),
                KeyboardInput(VkInsert, KeyeventfExtendedKey | KeyeventfKeyup), KeyboardInput(VkShift, KeyeventfKeyup)
            },
            _ => new[]
            {
                KeyboardInput(VkControl, 0), KeyboardInput(VkV, 0),
                KeyboardInput(VkV, KeyeventfKeyup), KeyboardInput(VkControl, KeyeventfKeyup)
            }
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
