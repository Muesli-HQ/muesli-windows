using CommunityToolkit.Mvvm.ComponentModel;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.WinUI.ViewModels;

/// <summary>
/// Presentation state for the Shortcuts page.
/// </summary>
/// <param name="settingsContext">Settings persistence.</param>
/// <param name="reapplyHotkey">
/// Re-registers the live push-to-talk hook from the saved settings and returns null on success or
/// the host's failure text. Saving alone does not change the running hook: the WH_KEYBOARD_LL hook
/// is installed once from <c>App.OnLaunched</c> (App.xaml.cs:197) and
/// <c>WinUiDictationContext.RegisterHotkey</c> (Services/WinUiDictationContext.cs:52-84) is not
/// called again, so without this the page would report a new gesture while the old one stayed live
/// until restart - and the old gesture, still hooked, was swallowed before it could be captured.
/// </param>
public partial class ShortcutsPageViewModel(
    WinUiSettingsContext settingsContext,
    Func<string?>? reapplyHotkey = null) : ObservableObject
{
    private MuesliSettings _settings = new();
    private string _captureTarget = "push";

    [ObservableProperty] public partial string Hotkey { get; private set; } = "F8";
    [ObservableProperty] public partial IReadOnlyList<string> HotkeyParts { get; private set; } = ["F8"];
    [ObservableProperty] public partial string CapturePrompt { get; private set; } = "Change Shortcut";
    [ObservableProperty] public partial bool IsCapturing { get; private set; }
    [ObservableProperty] public partial string StatusMessage { get; private set; } = "";
    [ObservableProperty] public partial bool IsStatusOpen { get; set; }
    [ObservableProperty] public partial bool IsStatusError { get; private set; }
    [NotifyPropertyChangedFor(nameof(ComputerUseShortcutState))]
    [ObservableProperty] public partial bool ComputerUseEnabled { get; private set; }
    [ObservableProperty] public partial int HoldThresholdMs { get; set; } = HotkeyTriggerTiming.DefaultThresholdMilliseconds;

    public string PushToTalkDescription => "Hold to record, release to transcribe";
    public string CaptureCancelHint => "Escape cancels capture";

    /// <summary>
    /// P5-02. Ctrl+Shift+F8 is genuinely registered for the whole session by
    /// <c>WinUiComputerUseContext.RegisterVoiceShortcut</c>
    /// (Services/WinUiComputerUseContext.cs:25-29, called unconditionally from App.xaml.cs:131),
    /// so this row describes a working shortcut with a precondition — not a missing one.
    /// </summary>
    public string ComputerUseShortcutState =>
        ComputerUseEnabled
            ? "Registered for this session. Focus an allowed app, then press the shortcut to start and finish a command."
            : "Registered for this session, but it does nothing until Computer Use is turned on in Settings.";

    /// <summary>
    /// P5-02. No Windows global shortcut exists for Quill; say that in Windows terms rather than
    /// describing it as "this macOS command".
    /// </summary>
    public string QuillUnavailableReason =>
        "No Windows global shortcut has been implemented for Quill, so it cannot be triggered from the keyboard.";
    public string MeetingRecordingUnavailableReason =>
        "Use the Meetings page, tray, or live transcript controls until a dedicated global shortcut is registered.";

    public void Load()
    {
        _settings = settingsContext.Load();
        ApplyHotkey(_settings.Hotkey);
        ComputerUseEnabled = _settings.ComputerUseEnabled;
        HoldThresholdMs = _settings.HotkeyTriggerThresholdMs;
    }

    public void BeginCapture(string target = "push")
    {
        _captureTarget = target;
        IsCapturing = true;
        CapturePrompt = "Press a shortcut…";
        IsStatusOpen = false;
    }

    public void CancelCapture()
    {
        IsCapturing = false;
        CapturePrompt = "Change Shortcut";
    }

    public void ApplyCapturedGesture(string gesture)
    {
        try
        {
            if (_captureTarget != "push")
            {
                throw new InvalidOperationException("Only the push-to-talk shortcut can currently be customized.");
            }

            var parsed = HotkeyGestureParser.Parse(gesture);
            if (HotkeyGestureParser.IsEscape(parsed.VirtualKey))
            {
                throw new InvalidOperationException("Escape is reserved for cancelling an active dictation.");
            }
            if (!HotkeyConflictProbe.IsAdvisoryRegistrationAvailable(parsed.DisplayName))
            {
                throw new InvalidOperationException("Windows reports that this shortcut is already registered by another app.");
            }

            _settings = _settings with { Hotkey = parsed.DisplayName };
            settingsContext.Save(_settings);
            ApplyHotkey(parsed.DisplayName);
            var failure = reapplyHotkey?.Invoke();
            ShowStatus(
                failure is null
                    ? $"Push-to-talk shortcut changed to {Hotkey}."
                    : $"{Hotkey} was saved, but it is not listening yet: {failure}",
                isError: failure is not null);
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message, isError: true);
        }
        finally
        {
            CancelCapture();
        }
    }

    public void ApplyHoldThreshold()
    {
        var threshold = HotkeyTriggerTiming.ClampMilliseconds(HoldThresholdMs);
        if (threshold == _settings.HotkeyTriggerThresholdMs)
        {
            HoldThresholdMs = threshold;
            return;
        }

        _settings = _settings with { HotkeyTriggerThresholdMs = threshold };
        settingsContext.Save(_settings);
        HoldThresholdMs = threshold;
        ShowStatus($"Hold threshold set to {threshold} ms.");
    }

    private void ApplyHotkey(string value)
    {
        var parsed = HotkeyGestureParser.Parse(value);
        Hotkey = parsed.DisplayName;
        HotkeyParts = SplitHotkey(parsed.DisplayName);
    }

    private static IReadOnlyList<string> SplitHotkey(string displayName)
    {
        var parts = displayName.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? [displayName] : parts;
    }

    private void ShowStatus(string message, bool isError = false)
    {
        StatusMessage = message;
        IsStatusError = isError;
        IsStatusOpen = true;
    }
}
