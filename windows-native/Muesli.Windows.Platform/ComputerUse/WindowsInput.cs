using System.Runtime.InteropServices;

namespace Muesli.Windows.ComputerUse;

/// <summary>Synthetic mouse, keyboard and clipboard input. Coordinates are physical screen pixels.</summary>
public static class WindowsInput
{
    public static void Click(double x, double y, string button = "left", int clicks = 1)
    {
        MoveTo(x, y);
        var (down, up) = button.ToLowerInvariant() switch
        {
            "right" or "r" => (MouseRightDown, MouseRightUp),
            "middle" or "m" => (MouseMiddleDown, MouseMiddleUp),
            _ => (MouseLeftDown, MouseLeftUp),
        };
        for (var click = 0; click < Math.Clamp(clicks, 1, 3); click++)
        {
            Send(Mouse(down), Mouse(up));
            Thread.Sleep(40);
        }
    }

    /// <summary>A zero-distance mouse move: counts as user input without any visible effect.</summary>
    public static void NudgeMouse() => Send(Mouse(MouseMove));

    public static void MoveTo(double x, double y)
    {
        SetCursorPos((int)Math.Round(x), (int)Math.Round(y));
        Thread.Sleep(30);
    }

    /// <summary>Scrolls at a point. <paramref name="notches"/> is positive for down/right.</summary>
    public static void Scroll(double x, double y, int notches, bool horizontal)
    {
        MoveTo(x, y);
        var flag = horizontal ? MouseHWheel : MouseWheel;
        // Wheel delta: positive scrolls up (vertical) or right (horizontal).
        var delta = horizontal ? notches * 120 : -notches * 120;
        Send(Mouse(flag, unchecked((uint)delta)));
        Thread.Sleep(80);
    }

    public static void Drag(double fromX, double fromY, double toX, double toY)
    {
        MoveTo(fromX, fromY);
        Send(Mouse(MouseLeftDown));
        const int steps = 12;
        for (var step = 1; step <= steps; step++)
        {
            SetCursorPos((int)Math.Round(fromX + (toX - fromX) * step / steps), (int)Math.Round(fromY + (toY - fromY) * step / steps));
            Send(Mouse(MouseMove));
            Thread.Sleep(15);
        }
        Send(Mouse(MouseLeftUp));
    }

    /// <summary>Presses a chord such as Ctrl+Shift+T: modifiers down in order, key, then release in reverse.</summary>
    public static void PressChord(IReadOnlyList<ushort> keys)
    {
        var inputs = keys.Select(key => Key(key, down: true)).Concat(keys.Reverse().Select(key => Key(key, down: false))).ToArray();
        Send(inputs);
        Thread.Sleep(60);
    }

    /// <summary>
    /// Types literal text as Unicode key packets, so case and symbols never depend on the Shift
    /// state (modern apps read Shift when they handle a key, which races a fast burst). Windows
    /// keeps one packet slot per thread, so after each character this waits for the app's UI
    /// thread to catch up; otherwise a busy app reads every queued packet as the last one.
    /// Newlines and tabs become Enter and Tab.
    /// </summary>
    public static void TypeText(string text, CancellationToken cancellationToken)
    {
        var target = GetForegroundWindow();
        for (var index = 0; index < text.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var character = text[index];
            if (character == '\r') continue;
            if (character is '\n' or '\t')
            {
                var key = character == '\n' ? VkReturn : VkTab;
                Send(Key(key, true), Key(key, false));
                WaitForApp(target, 25);
                continue;
            }
            if (char.IsHighSurrogate(character) && index + 1 < text.Length)
            {
                var low = text[++index];
                Send(UnicodeKey(character, true), UnicodeKey(character, false), UnicodeKey(low, true), UnicodeKey(low, false));
            }
            else
            {
                Send(UnicodeKey(character, true), UnicodeKey(character, false));
            }
            WaitForApp(target, 4);
        }
    }

    /// <summary>Returns once the window's UI thread is pumping messages again, plus a short pause
    /// for the queued key to be handled.</summary>
    private static void WaitForApp(IntPtr window, int pauseMs)
    {
        SendMessageTimeoutW(window, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero, 0x2 /* SMTO_ABORTIFHUNG */, 250, out _);
        Thread.Sleep(pauseMs);
    }

    /// <summary>Pastes text through the clipboard and then restores the user's clipboard text or image.</summary>
    public static void PasteText(string text)
    {
        var saved = Sta.Run(() =>
        {
            var previous = Retry(() => System.Windows.Forms.Clipboard.ContainsText() ? (object?)System.Windows.Forms.Clipboard.GetText()
                : System.Windows.Forms.Clipboard.ContainsImage() ? System.Windows.Forms.Clipboard.GetImage() : null);
            SetClipboardText(text);
            return previous;
        });
        var target = GetForegroundWindow();
        PressChord([VkControl, (ushort)'V']);
        WaitForApp(target, 400);
        Sta.Run(() => Retry(() =>
        {
            switch (saved)
            {
                case string previousText: SetClipboardText(previousText); break;
                case System.Drawing.Image image: System.Windows.Forms.Clipboard.SetImage(image); break;
                default: System.Windows.Forms.Clipboard.Clear(); break;
            }
            return true;
        }));
    }

    /// <summary>
    /// Puts only CF_UNICODETEXT on the clipboard (Windows synthesizes the other text formats on
    /// demand). WinForms also stores an eager ANSI copy, which some apps intermittently paste as
    /// "?" for non-Latin characters. The extra formats keep the text out of clipboard history.
    /// </summary>
    private static void SetClipboardText(string text)
    {
        for (var attempt = 0; !OpenClipboard(IntPtr.Zero); attempt++)
        {
            if (attempt >= 10) throw new ExternalException("The clipboard is in use by another app.");
            Thread.Sleep(50);
        }
        try
        {
            EmptyClipboard();
            var bytes = (text.Length + 1) * 2;
            var memory = GlobalAlloc(0x2 /* GMEM_MOVEABLE */, (UIntPtr)bytes);
            var pointer = GlobalLock(memory);
            Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
            Marshal.WriteInt16(pointer, text.Length * 2, 0);
            GlobalUnlock(memory);
            if (SetClipboardData(13 /* CF_UNICODETEXT */, memory) == IntPtr.Zero) throw new ExternalException("Could not set the clipboard.");
            foreach (var format in new[] { "ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
            {
                var flag = GlobalAlloc(0x2, (UIntPtr)4); // DWORD 0 = "no" for the history formats.
                Marshal.WriteInt32(GlobalLock(flag), 0);
                GlobalUnlock(flag);
                SetClipboardData(RegisterClipboardFormatW(format), flag);
            }
        }
        finally { CloseClipboard(); }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);

    private static T Retry<T>(Func<T> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return action(); }
            catch (ExternalException) when (attempt < 5) { Thread.Sleep(50); }
        }
    }

    // ---- Key names ----

    public const ushort VkControl = 0x11, VkShift = 0x10, VkAlt = 0x12, VkReturn = 0x0D, VkTab = 0x09;

    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = VkReturn, ["return"] = VkReturn, ["kp_enter"] = VkReturn, ["numpad_enter"] = VkReturn,
        ["tab"] = VkTab, ["escape"] = 0x1B, ["esc"] = 0x1B, ["backspace"] = 0x08, ["back_space"] = 0x08,
        ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["space"] = 0x20, ["spacebar"] = 0x20,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["arrowup"] = 0x26, ["arrowdown"] = 0x28, ["arrowleft"] = 0x25, ["arrowright"] = 0x27,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["page_up"] = 0x21, ["prior"] = 0x21,
        ["pagedown"] = 0x22, ["page_down"] = 0x22, ["next"] = 0x22, ["capslock"] = 0x14, ["caps_lock"] = 0x14,
        ["menu"] = 0x5D, ["apps"] = 0x5D, ["contextmenu"] = 0x5D, ["printscreen"] = 0x2C, ["print"] = 0x2C,
        ["numpad_add"] = 0x6B, ["kp_add"] = 0x6B, ["numpad_subtract"] = 0x6D, ["kp_subtract"] = 0x6D,
        ["numpad_multiply"] = 0x6A, ["kp_multiply"] = 0x6A, ["numpad_divide"] = 0x6F, ["kp_divide"] = 0x6F,
        ["numpad_decimal"] = 0x6E, ["kp_decimal"] = 0x6E,
    };

    private static readonly Dictionary<string, char> PunctuationKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["period"] = '.', ["dot"] = '.', ["comma"] = ',', ["slash"] = '/', ["backslash"] = '\\', ["semicolon"] = ';',
        ["colon"] = ':', ["apostrophe"] = '\'', ["quote"] = '\'', ["quotedbl"] = '"', ["minus"] = '-', ["hyphen"] = '-',
        ["equal"] = '=', ["equals"] = '=', ["plus"] = '+', ["bracketleft"] = '[', ["bracketright"] = ']',
        ["braceleft"] = '{', ["braceright"] = '}', ["grave"] = '`', ["question"] = '?', ["greater"] = '>', ["less"] = '<',
        ["exclam"] = '!', ["at"] = '@', ["numbersign"] = '#', ["dollar"] = '$', ["percent"] = '%', ["asterisk"] = '*',
        ["underscore"] = '_', ["bar"] = '|', ["asciitilde"] = '~', ["parenleft"] = '(', ["parenright"] = ')',
    };

    private static readonly HashSet<string> WindowsKeyNames = new(StringComparer.OrdinalIgnoreCase)
        { "win", "windows", "super", "super_l", "super_r", "os", "lwin", "rwin", "windows_l", "windows_r" };

    /// <summary>
    /// Parses a key plus modifiers into virtual-key codes. Accepts X keysym-style names
    /// (<c>Control_L+a</c>, <c>Return</c>, <c>KP_5</c>), common aliases, and macOS planner
    /// modifiers (command maps to Ctrl, option to Alt). The Windows key is refused.
    /// </summary>
    public static IReadOnlyList<ushort> ParseChord(string key, IEnumerable<string> modifiers)
    {
        var parts = modifiers.Concat(key.Split('+', StringSplitOptions.TrimEntries)).Where(part => part.Length > 0).ToList();
        // "ctrl++" means the plus key.
        if (key.EndsWith("++", StringComparison.Ordinal)) parts.Add("plus");
        if (parts.Count == 0) throw new ArgumentException("No key was given.");
        var result = new List<ushort>();
        void Add(ushort vk) { if (!result.Contains(vk)) result.Add(vk); }
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            if (WindowsKeyNames.Contains(part)) throw new ArgumentException("The Windows key is not allowed.");
            var modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" or "control_l" or "control_r" or "ctl" or "command" or "cmd" or "meta" or "⌘" or "^" => VkControl,
                "shift" or "shift_l" or "shift_r" or "⇧" => VkShift,
                "alt" or "alt_l" or "alt_r" or "option" or "opt" or "menu_l" or "⌥" => VkAlt,
                _ => (ushort)0,
            };
            if (modifier != 0 && index < parts.Count - 1) { Add(modifier); continue; }
            if (modifier != 0) { Add(modifier); continue; } // A lone modifier press.
            foreach (var vk in KeyCodes(part)) Add(vk);
        }
        // Modifiers first, then the key.
        return result.Where(IsModifier).Concat(result.Where(vk => !IsModifier(vk))).ToArray();
    }

    private static bool IsModifier(ushort vk) => vk is VkControl or VkShift or VkAlt;

    private static IEnumerable<ushort> KeyCodes(string name)
    {
        if (NamedKeys.TryGetValue(name, out var named)) return [named];
        var lower = name.ToLowerInvariant();
        if (lower.Length is >= 2 and <= 3 && lower[0] == 'f' && int.TryParse(lower[1..], out var function) && function is >= 1 and <= 24)
            return [(ushort)(0x70 + function - 1)];
        foreach (var prefix in new[] { "kp_", "numpad_", "numpad" })
            if (lower.StartsWith(prefix, StringComparison.Ordinal) && lower.Length == prefix.Length + 1 && char.IsDigit(lower[^1]))
                return [(ushort)(0x60 + lower[^1] - '0')];
        var character = PunctuationKeys.TryGetValue(name, out var punctuation) ? punctuation : name.Length == 1 ? name[0] : '\0';
        if (character == '\0') throw new ArgumentException($"Unknown key '{name}'.");
        if (char.IsLetter(character)) return [(ushort)char.ToUpperInvariant(character)];
        var scan = VkKeyScanW(character);
        if (scan == -1) throw new ArgumentException($"Key '{name}' is not on this keyboard layout.");
        var codes = new List<ushort>();
        var shiftState = (scan >> 8) & 0xFF;
        if ((shiftState & 1) != 0) codes.Add(VkShift);
        if ((shiftState & 2) != 0) codes.Add(VkControl);
        if ((shiftState & 4) != 0) codes.Add(VkAlt);
        codes.Add((ushort)(scan & 0xFF));
        return codes;
    }

    // ---- SendInput ----

    private const uint MouseMove = 0x0001, MouseLeftDown = 0x0002, MouseLeftUp = 0x0004, MouseRightDown = 0x0008,
        MouseRightUp = 0x0010, MouseMiddleDown = 0x0020, MouseMiddleUp = 0x0040, MouseWheel = 0x0800, MouseHWheel = 0x01000;
    private const uint KeyUp = 0x0002, KeyUnicode = 0x0004, KeyExtended = 0x0001;

    private static Input Mouse(uint flags, uint data = 0) =>
        new() { Type = 0, Union = new InputUnion { Mouse = new MouseInput { Flags = flags, Data = data } } };

    private static Input Key(ushort vk, bool down) =>
        new()
        {
            Type = 1,
            Union = new InputUnion { Keyboard = new KeyboardInput
            {
                VirtualKey = vk,
                ScanCode = (ushort)MapVirtualKeyW(vk, 0),
                Flags = (down ? 0 : KeyUp) | (IsExtended(vk) ? KeyExtended : 0),
            } },
        };

    private static Input UnicodeKey(char character, bool down) =>
        new() { Type = 1, Union = new InputUnion { Keyboard = new KeyboardInput { ScanCode = character, Flags = KeyUnicode | (down ? 0 : KeyUp) } } };

    /// <summary>Navigation keys need the extended flag or they arrive as numpad keys.</summary>
    private static bool IsExtended(ushort vk) => vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5D or 0x6F;

    private static void Send(params Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new InvalidOperationException("Windows blocked the input (a higher-privilege window may be in front).");
    }

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public IntPtr ExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern short VkKeyScanW(char character);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);
}

/// <summary>Runs a function on a short-lived STA thread (clipboard and shell COM need one).</summary>
public static class Sta
{
    public static T Run<T>(Func<T> function)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = function(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException(error.Message, error);
        return result;
    }
}
