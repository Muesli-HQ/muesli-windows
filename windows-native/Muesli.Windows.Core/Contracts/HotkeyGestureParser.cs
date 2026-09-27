namespace Muesli.Windows.Core.Contracts;

/// <summary>
/// Parses the persisted hotkey format without depending on WPF input types.
/// The parser returns Windows virtual-key values so both the WPF fallback and
/// the WinUI platform adapter can share the same validation rules.
/// </summary>
public static class HotkeyGestureParser
{
    private const int VkEscape = 0x1B;

    public static HotkeyGesture Parse(string? value)
    {
        var input = string.IsNullOrWhiteSpace(value) ? "F8" : value.Trim();
        var virtualKey = 0;
        var keyLabel = string.Empty;
        var keyCount = 0;
        var modifiers = HotkeyModifiers.None;

        foreach (var rawPart in input.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart.Replace(" ", string.Empty, StringComparison.Ordinal);
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Control;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Shift;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("Option", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Alt;
            }
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("Windows", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("Meta", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Windows;
            }
            else if (TryParseVirtualKey(part, out var parsedVirtualKey, out var parsedLabel))
            {
                virtualKey = parsedVirtualKey;
                keyLabel = parsedLabel;
                keyCount++;
            }
            else
            {
                throw new InvalidOperationException($"Unsupported hotkey: {input}");
            }
        }

        if (keyCount != 1 || virtualKey == 0)
        {
            throw new InvalidOperationException($"Unsupported hotkey: {input}");
        }

        return new HotkeyGesture(virtualKey, modifiers, CanonicalLabel(keyLabel, modifiers));
    }

    public static bool Matches(HotkeyGesture gesture, int virtualKey, HotkeyModifiers modifiers) =>
        gesture.VirtualKey == virtualKey && gesture.Modifiers == (modifiers & SupportedModifiers);

    public static bool IsEscape(int virtualKey) => virtualKey == VkEscape;

    public static bool IsGestureModifier(int virtualKey) => virtualKey is
        0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    private const HotkeyModifiers SupportedModifiers =
        HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt | HotkeyModifiers.Windows;

    private static string CanonicalLabel(string keyLabel, HotkeyModifiers modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        parts.Add(keyLabel);
        return string.Join('+', parts);
    }

    private static bool TryParseVirtualKey(string value, out int virtualKey, out string label)
    {
        virtualKey = 0;
        label = string.Empty;
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length == 1)
        {
            var character = normalized[0];
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = character;
                label = character.ToString();
                return true;
            }
        }

        if (normalized.StartsWith("F", StringComparison.Ordinal) &&
            int.TryParse(normalized.AsSpan(1), out var functionNumber) &&
            functionNumber is >= 1 and <= 24)
        {
            virtualKey = 0x70 + functionNumber - 1;
            label = $"F{functionNumber}";
            return true;
        }

        var parsed = normalized switch
        {
            "ESC" or "ESCAPE" => (0x1B, "Escape"),
            "SPACE" or "SPACEBAR" => (0x20, "Space"),
            "TAB" => (0x09, "Tab"),
            "ENTER" or "RETURN" => (0x0D, "Enter"),
            "BACKSPACE" or "BACK" => (0x08, "Backspace"),
            "DELETE" or "DEL" => (0x2E, "Delete"),
            "INSERT" or "INS" => (0x2D, "Insert"),
            "HOME" => (0x24, "Home"),
            "END" => (0x23, "End"),
            "PAGEUP" or "PGUP" => (0x21, "PageUp"),
            "PAGEDOWN" or "PGDN" => (0x22, "PageDown"),
            "UP" or "ARROWUP" => (0x26, "Up"),
            "DOWN" or "ARROWDOWN" => (0x28, "Down"),
            "LEFT" or "ARROWLEFT" => (0x25, "Left"),
            "RIGHT" or "ARROWRIGHT" => (0x27, "Right"),
            "CAPSLOCK" => (0x14, "CapsLock"),
            "NUMLOCK" => (0x90, "NumLock"),
            "SCROLLLOCK" => (0x91, "ScrollLock"),
            "PRINTSCREEN" or "PRTSC" => (0x2C, "PrintScreen"),
            "PAUSE" => (0x13, "Pause"),
            "APPS" or "CONTEXTMENU" => (0x5D, "Apps"),
            "NUMPAD0" => (0x60, "NumPad0"),
            "NUMPAD1" => (0x61, "NumPad1"),
            "NUMPAD2" => (0x62, "NumPad2"),
            "NUMPAD3" => (0x63, "NumPad3"),
            "NUMPAD4" => (0x64, "NumPad4"),
            "NUMPAD5" => (0x65, "NumPad5"),
            "NUMPAD6" => (0x66, "NumPad6"),
            "NUMPAD7" => (0x67, "NumPad7"),
            "NUMPAD8" => (0x68, "NumPad8"),
            "NUMPAD9" => (0x69, "NumPad9"),
            "MULTIPLY" or "NUMPADMULTIPLY" => (0x6A, "Multiply"),
            "ADD" or "NUMPADADD" => (0x6B, "Add"),
            "SUBTRACT" or "NUMPADSUBTRACT" => (0x6D, "Subtract"),
            "DECIMAL" or "NUMPADDECIMAL" => (0x6E, "Decimal"),
            "DIVIDE" or "NUMPADDIVIDE" => (0x6F, "Divide"),
            "OEMSEMICOLON" => (0xBA, "OemSemicolon"),
            "OEMPLUS" => (0xBB, "OemPlus"),
            "OEMCOMMA" => (0xBC, "OemComma"),
            "OEMMINUS" => (0xBD, "OemMinus"),
            "OEMPERIOD" => (0xBE, "OemPeriod"),
            "OEMQUESTION" => (0xBF, "OemQuestion"),
            "OEMTILDE" => (0xC0, "OemTilde"),
            "OEMOPENBRACKETS" => (0xDB, "OemOpenBrackets"),
            "OEMPIPE" => (0xDC, "OemPipe"),
            "OEMCLOSEBRACKETS" => (0xDD, "OemCloseBrackets"),
            "OEMQUOTES" => (0xDE, "OemQuotes"),
            "OEM8" => (0xDF, "Oem8"),
            "OEMBACKSLASH" => (0xE2, "OemBackslash"),
            _ => (0, string.Empty)
        };

        virtualKey = parsed.Item1;
        label = parsed.Item2;
        return virtualKey != 0;
    }
}
