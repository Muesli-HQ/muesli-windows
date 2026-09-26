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
    [NotifyPropertyChangedFor(nameof(ComputerUseShortcutState))]
    [ObservableProperty] public partial bool ComputerUseConfigured { get; private set; }
    [NotifyPropertyChangedFor(nameof(ComputerUseShortcutState))]
    [ObservableProperty] public partial string ComputerUseConfigurationReason { get; private set; } = "";
    [ObservableProperty] public partial int HoldThresholdMs { get; set; } = HotkeyTriggerTiming.DefaultThresholdMilliseconds;
    [NotifyPropertyChangedFor(nameof(PushToTalkDescription))]
    [ObservableProperty] public partial bool HandsFreeEnabled { get; private set; }

    public string PushToTalkDescription => HandsFreeEnabled
        ? "Hold to record, release to transcribe, or double-tap to lock hands-free"
        : "Hold to record, release to transcribe";
    public string CaptureCancelHint => "Escape cancels capture";

    /// <summary>
    /// Ctrl+Shift+F8 is genuinely registered for the whole session by
    /// <c>WinUiComputerUseContext.RegisterVoiceShortcut</c>, so this row describes a working
    /// shortcut with a precondition. When the required configuration is incomplete the row says so
    /// with the exact validation reason and offers a route to Settings.
    /// </summary>
    public string ComputerUseShortcutState
    {
        get
        {
            if (!ComputerUseConfigured)
            {
                return string.IsNullOrWhiteSpace(ComputerUseConfigurationReason)
                    ? "Not configured. Computer Use needs an OpenAI planner, an allowed app, and privacy observation off."
                    : ComputerUseConfigurationReason;
            }

            return ComputerUseEnabled
                ? "Registered for this session. Focus an allowed app, then press the shortcut to start and finish a command."
                : "Registered for this session. Turn it on here to use the shortcut.";
        }
    }

    public bool ComputerUseNeedsConfiguration => !ComputerUseConfigured;

    /// <summary>
    /// P5-02. No Windows global shortcut exists for Quill; say that in Windows terms rather than
    /// describing it as "this macOS command".
    /// </summary>
    public string QuillUnavailableReason =>
        "Quill has no Windows runtime or global shortcut yet, so it cannot be triggered from the keyboard.";
    public string MeetingRecordingUnavailableReason =>
        "Meeting recording works, but no dedicated global shortcut is registered yet. Use the Meetings page, tray, or live notification to start and stop recording.";

    public void Load()
    {
        _settings = settingsContext.Load();
        ApplyHotkey(_settings.Hotkey);
        ComputerUseEnabled = _settings.ComputerUseEnabled;
        HandsFreeEnabled = _settings.EnableDoubleTapDictation;
        HoldThresholdMs = _settings.HotkeyTriggerThresholdMs;
        RefreshComputerUseConfiguration();
    }

    /// <summary>Re-reads the shared Computer Use configuration and the exact validation reason.</summary>
    public void RefreshComputerUseConfiguration()
    {
        _settings = settingsContext.Load();
        ComputerUseConfigured = ComputerUseConfiguration.TryValidate(
            _settings, _settings.ResolvedOpenAIApiKey, out var reason);
        ComputerUseConfigurationReason = ComputerUseConfigured ? "" : reason;
        OnPropertyChanged(nameof(ComputerUseNeedsConfiguration));
        OnPropertyChanged(nameof(ComputerUseShortcutState));
    }

    /// <summary>
    /// Enables or disables Computer Use from the Shortcuts page, using the same validation as
    /// Settings. An incomplete configuration keeps it off and reports the reason instead.
    /// </summary>
    public void SetComputerUseEnabled(bool enabled)
    {
        RefreshComputerUseConfiguration();
        if (enabled && !ComputerUseConfigured)
        {
            ComputerUseEnabled = false;
            ShowStatus(ComputerUseConfigurationReason, isError: true);
            return;
        }

        _settings = _settings with { ComputerUseEnabled = enabled };
        settingsContext.Save(_settings);
        ComputerUseEnabled = enabled;
        OnPropertyChanged(nameof(ComputerUseShortcutState));
        ShowStatus(enabled
            ? "Computer Use shortcut enabled."
            : "Computer Use shortcut disabled.");
    }

    /// <summary>
    /// Enables double-tap (hands-free) dictation. The dictation context reads
    /// <c>EnableDoubleTapDictation</c> from settings on every key event, so saving is enough to
    /// change the live gesture without re-registering the hook.
    /// </summary>
    public void SetHandsFreeEnabled(bool enabled)
    {
        _settings = _settings with { EnableDoubleTapDictation = enabled };
        settingsContext.Save(_settings);
        HandsFreeEnabled = enabled;
        ShowStatus(enabled
            ? "Hands-free dictation enabled. Double-tap the shortcut to start and stop."
            : "Hands-free dictation disabled.");
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
