using System.Windows.Input;

namespace Muesli.Windows.Services;

public sealed record HotkeyGesture(Key Key, ModifierKeys Modifiers, string Label)
{
    private const ModifierKeys SupportedModifiers =
        ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt | ModifierKeys.Windows;

    public static HotkeyGesture Parse(string? value)
    {
        var input = string.IsNullOrWhiteSpace(value) ? "F8" : value.Trim();
        var key = Key.None;
        var modifiers = ModifierKeys.None;
        var keyCount = 0;

        foreach (var rawPart in input.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart.Replace(" ", "", StringComparison.OrdinalIgnoreCase);
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Control;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Shift;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("Option", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Alt;
            }
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Windows;
            }
            else if (Enum.TryParse(part, ignoreCase: true, out Key parsedKey) && parsedKey != Key.None)
            {
                key = parsedKey;
                keyCount++;
            }
            else
            {
                throw new InvalidOperationException($"Unsupported hotkey: {input}");
            }
        }

        if (keyCount != 1 || key == Key.None)
        {
            throw new InvalidOperationException($"Unsupported hotkey: {input}");
        }

        return new HotkeyGesture(key, modifiers, CanonicalLabel(key, modifiers));
    }

    public bool Matches(Key key, ModifierKeys modifiers) =>
        key == Key && (modifiers & SupportedModifiers) == Modifiers;

    private static string CanonicalLabel(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join('+', parts);
    }
}
