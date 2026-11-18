namespace Muesli.Windows.Services;

/// <summary>What the keyboard hook should do after one event in lone-modifier mode.</summary>
[Flags]
public enum ModifierHoldAction
{
    None = 0,
    /// <summary>The hold started: begin push-to-talk.</summary>
    Start = 1,
    /// <summary>The key was released after a clean hold: finish push-to-talk.</summary>
    Stop = 2,
    /// <summary>Another key joined the hold, so it is a combination (Right Shift+A): cancel.</summary>
    Cancel = 4,
    /// <summary>
    /// Inject the menu mask key now, while Alt/Win is still held, so Windows sees the press "used"
    /// and does not focus the menu bar or open Start on release.
    /// </summary>
    SendMenuMask = 8
}

/// <summary>
/// A lone right-side modifier (Right Ctrl/Alt/Shift/Win) as a hold-to-talk key, decided from raw
/// low-level hook events. Events always pass through, so the key keeps working as a modifier.
/// Framework-free so the hold rules can be tested without a keyboard.
/// </summary>
public sealed class ModifierHoldTracker
{
    private const int LeftControl = 0xA2;
    private const int RightAlt = 0xA5;
    private const int RightWin = 0x5C;

    /// <summary>
    /// On AltGr layouts Windows sends a synthetic Left Ctrl, scan code 0x21D, with every Right Alt
    /// press, repeat and release. It carries no user intent and must never read as "another key".
    /// </summary>
    public const uint AltGrPhantomCtrlScanCode = 0x21D;

    private readonly int _gestureKey;
    private bool _held;
    private bool _combined;
    private bool _active;

    public ModifierHoldTracker(int gestureKey) => _gestureKey = gestureKey;

    public bool IsActive => _active;

    public static bool IsAltGrPhantomCtrl(int virtualKey, uint scanCode) =>
        virtualKey == LeftControl && scanCode == AltGrPhantomCtrlScanCode;

    /// <param name="otherModifiersHeld">
    /// Whether any other modifier is physically down; read only for the gesture key's first press.
    /// </param>
    public ModifierHoldAction Process(int virtualKey, uint scanCode, bool isKeyDown, bool isKeyUp, bool injected, Func<bool> otherModifiersHeld)
    {
        // Injected input (other apps' automation, our own mask key) never drives dictation.
        if (injected || IsAltGrPhantomCtrl(virtualKey, scanCode)) return ModifierHoldAction.None;

        if (isKeyDown)
        {
            if (virtualKey == _gestureKey)
            {
                // Auto-repeat arrives as more key-downs while held; only the first starts the hold.
                if (_held) return ModifierHoldAction.None;
                _held = true;
                _combined = otherModifiersHeld();
                if (_combined || _active) return ModifierHoldAction.None;
                _active = true;
                return _gestureKey is RightAlt or RightWin
                    ? ModifierHoldAction.Start | ModifierHoldAction.SendMenuMask
                    : ModifierHoldAction.Start;
            }

            if (!_held || _combined) return ModifierHoldAction.None;
            _combined = true;
            if (!_active) return ModifierHoldAction.None;
            _active = false;
            return ModifierHoldAction.Cancel;
        }

        if (isKeyUp && virtualKey == _gestureKey)
        {
            _held = false;
            _combined = false;
            if (!_active) return ModifierHoldAction.None;
            _active = false;
            return ModifierHoldAction.Stop;
        }

        return ModifierHoldAction.None;
    }

    /// <summary>Forget the current hold (hook re-registered, hotkey paused).</summary>
    public void Reset()
    {
        _held = false;
        _combined = false;
        _active = false;
    }
}
