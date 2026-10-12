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
/// <param name="reapplyComputerUseHotkey">Re-registers the Computer Use shortcut; null on success.</param>
/// <param name="reapplyMeetingHotkey">Re-registers the meeting recording shortcut; null on success.</param>
public partial class ShortcutsPageViewModel(
    WinUiSettingsContext settingsContext,
    Func<string?>? reapplyHotkey = null,
    Func<string?>? reapplyComputerUseHotkey = null,
    Func<string?>? reapplyMeetingHotkey = null) : ObservableObject
{
    private MuesliSettings _settings = new();
    private string _captureTarget = "push";

    [ObservableProperty] public partial string Hotkey { get; private set; } = "F8";
    [ObservableProperty] public partial IReadOnlyList<string> HotkeyParts { get; private set; } = ["F8"];
    [ObservableProperty] public partial string CapturePrompt { get; private set; } = "Change…";
    [ObservableProperty] public partial IReadOnlyList<string> ComputerUseHotkeyParts { get; private set; } = SplitHotkey(ComputerUseConfiguration.DefaultHotkey);
    [ObservableProperty] public partial string ComputerUseCapturePrompt { get; private set; } = "Change…";
    [ObservableProperty] public partial IReadOnlyList<string> MeetingHotkeyParts { get; private set; } = SplitHotkey(new MuesliSettings().MeetingRecordingHotkey);
    [ObservableProperty] public partial string MeetingCapturePrompt { get; private set; } = "Change…";
    [ObservableProperty] public partial bool MeetingShortcutEnabled { get; private set; }
    public bool IsCapturingComputerUse => IsCapturing && _captureTarget == "computer-use";
    public bool IsCapturingMeeting => IsCapturing && _captureTarget == "meeting";
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
    [NotifyPropertyChangedFor(nameof(PushToTalkOff))]
    [ObservableProperty] public partial bool PushToTalkEnabled { get; private set; } = true;
    [ObservableProperty] public partial bool IsAtDefaults { get; private set; } = true;

    public bool PushToTalkOff => !PushToTalkEnabled;

    public string PushToTalkDescription => HandsFreeEnabled
        ? "Hold to record, release to transcribe, or double-tap to lock hands-free"
        : "Hold to record, release to transcribe";
    public string CaptureCancelHint => "Escape cancels capture";

    /// <summary>
    /// The Computer Use shortcut is registered for the whole session by
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
                    ? "Not configured. Sign in to ChatGPT in Settings > Computer Use."
                    : ComputerUseConfigurationReason;
            }

            return ComputerUseEnabled
                ? "Press to start a spoken command and again to run it."
                : "Turn it on to use the shortcut.";
        }
    }

    public bool ComputerUseNeedsConfiguration => !ComputerUseConfigured;

    /// <summary>
    /// P5-02. No Windows global shortcut exists for Quill; say that in Windows terms rather than
    /// describing it as "this macOS command".
    /// </summary>
    public string QuillUnavailableReason =>
        "Not available on Windows yet.";

    public void Load()
    {
        _settings = settingsContext.Load();
        ApplyHotkey(_settings.Hotkey);
        ComputerUseHotkeyParts = SplitHotkey(HotkeyGestureParser.Parse(_settings.ComputerUseHotkey).DisplayName);
        ComputerUseEnabled = _settings.ComputerUseEnabled;
        HandsFreeEnabled = _settings.EnableDoubleTapDictation;
        HoldThresholdMs = _settings.HotkeyTriggerThresholdMs;
        PushToTalkEnabled = _settings.PushToTalkEnabled;
        MeetingShortcutEnabled = _settings.MeetingRecordingShortcutEnabled;
        MeetingHotkeyParts = SplitHotkey(HotkeyGestureParser.Parse(_settings.MeetingRecordingHotkey).DisplayName);
        RefreshComputerUseConfiguration();
        RefreshDefaults();
    }

    /// <summary>Turns the dictation shortcut on or off; off removes the global hook.</summary>
    public void SetPushToTalkEnabled(bool enabled)
    {
        _settings = _settings with { PushToTalkEnabled = enabled };
        settingsContext.Save(_settings);
        PushToTalkEnabled = enabled;
        var failure = reapplyHotkey?.Invoke();
        if (!enabled) ShowStatus("Push to Talk is turned off.");
        else ShowStatus(failure is null ? $"Push to Talk is on: {Hotkey}." : $"Push to Talk could not start: {failure}", failure is not null);
    }

    /// <summary>Turns the meeting recording shortcut on or off; off removes its hook.</summary>
    public void SetMeetingShortcutEnabled(bool enabled)
    {
        _settings = _settings with { MeetingRecordingShortcutEnabled = enabled };
        settingsContext.Save(_settings);
        MeetingShortcutEnabled = enabled;
        var failure = reapplyMeetingHotkey?.Invoke();
        var keys = HotkeyGestureParser.Parse(_settings.MeetingRecordingHotkey).DisplayName;
        if (!enabled) ShowStatus("Meeting recording shortcut disabled.");
        else ShowStatus(failure is null ? $"{keys} now starts and stops meeting recording." : $"The meeting shortcut could not start: {failure}", failure is not null);
    }

    /// <summary>
    /// macOS Reset to Defaults: every shortcut, Push to Talk on, Computer Use and the meeting
    /// shortcut off, default delay.
    /// </summary>
    public void ResetToDefaults()
    {
        _settings = _settings with
        {
            Hotkey = new MuesliSettings().Hotkey,
            PushToTalkEnabled = true,
            ComputerUseHotkey = ComputerUseConfiguration.DefaultHotkey,
            ComputerUseEnabled = false,
            MeetingRecordingHotkey = new MuesliSettings().MeetingRecordingHotkey,
            MeetingRecordingShortcutEnabled = false,
            HotkeyTriggerThresholdMs = HotkeyTriggerTiming.DefaultThresholdMilliseconds,
        };
        settingsContext.Save(_settings);
        Load();
        var dictationFailure = reapplyHotkey?.Invoke();
        var meetingFailure = reapplyMeetingHotkey?.Invoke();
        var failure = reapplyComputerUseHotkey?.Invoke() ?? dictationFailure ?? meetingFailure;
        ShowStatus(failure is null ? "Shortcuts reset to defaults." : $"Shortcuts reset, but one is not listening yet: {failure}", failure is not null);
    }

    /// <summary>Re-reads the shared Computer Use configuration and the exact validation reason.</summary>
    public void RefreshComputerUseConfiguration()
    {
        _settings = settingsContext.Load();
        ComputerUseConfigured = ComputerUseConfiguration.TryValidate(
            _settings, App.ComputerUse.ChatGpt.IsSignedIn, out var reason);
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
        if (target == "computer-use") ComputerUseCapturePrompt = "Press shortcut…";
        else if (target == "meeting") MeetingCapturePrompt = "Press shortcut…";
        else CapturePrompt = "Press shortcut…";
        IsStatusOpen = false;
    }

    public void CancelCapture()
    {
        IsCapturing = false;
        CapturePrompt = "Change…";
        ComputerUseCapturePrompt = "Change…";
        MeetingCapturePrompt = "Change…";
    }

    public void ApplyCapturedGesture(string gesture)
    {
        try
        {
            var parsed = HotkeyGestureParser.Parse(gesture);
            var computerUse = _captureTarget == "computer-use";
            if (HotkeyGestureParser.IsEscape(parsed.VirtualKey))
            {
                throw new InvalidOperationException("Escape is reserved for cancelling an active dictation.");
            }
            var meeting = _captureTarget == "meeting";
            foreach (var (owner, name, value) in new[]
            {
                ("push", "dictation", _settings.Hotkey),
                ("computer-use", "Computer Use", _settings.ComputerUseHotkey),
                ("meeting", "meeting recording", _settings.MeetingRecordingHotkey)
            })
            {
                var other = HotkeyGestureParser.Parse(value).DisplayName;
                if (owner != _captureTarget && parsed.DisplayName.Equals(other, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{other} is the {name} shortcut. Choose a different one.");
            }
            if (!HotkeyConflictProbe.IsAdvisoryRegistrationAvailable(parsed.DisplayName))
            {
                throw new InvalidOperationException("Windows reports that this shortcut is already registered by another app.");
            }

            if (meeting)
            {
                _settings = _settings with { MeetingRecordingHotkey = parsed.DisplayName };
                settingsContext.Save(_settings);
                MeetingHotkeyParts = SplitHotkey(parsed.DisplayName);
                var meetingFailure = _settings.MeetingRecordingShortcutEnabled ? reapplyMeetingHotkey?.Invoke() : null;
                ShowStatus(
                    !_settings.MeetingRecordingShortcutEnabled
                        ? $"Meeting recording shortcut changed to {parsed.DisplayName}. Turn it on to use it."
                        : meetingFailure is null
                            ? $"Meeting recording shortcut changed to {parsed.DisplayName}."
                            : $"{parsed.DisplayName} was saved, but it is not listening yet: {meetingFailure}",
                    isError: meetingFailure is not null);
                return;
            }

            if (computerUse)
            {
                _settings = _settings with { ComputerUseHotkey = parsed.DisplayName };
                settingsContext.Save(_settings);
                ComputerUseHotkeyParts = SplitHotkey(parsed.DisplayName);
                var registerFailure = reapplyComputerUseHotkey?.Invoke();
                ShowStatus(
                    registerFailure is null
                        ? $"Computer Use shortcut changed to {parsed.DisplayName}."
                        : $"{parsed.DisplayName} was saved, but it is not listening yet: {registerFailure}",
                    isError: registerFailure is not null);
                return;
            }

            _settings = _settings with { Hotkey = parsed.DisplayName };
            settingsContext.Save(_settings);
            ApplyHotkey(parsed.DisplayName);
            var failure = _settings.PushToTalkEnabled ? reapplyHotkey?.Invoke() : null;
            ShowStatus(
                !_settings.PushToTalkEnabled
                    ? $"Push-to-talk shortcut changed to {Hotkey}. Turn on Push to Talk to use it."
                    : failure is null
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

    private void RefreshDefaults() =>
        IsAtDefaults = HotkeyGestureParser.Parse(_settings.Hotkey).DisplayName == HotkeyGestureParser.Parse(new MuesliSettings().Hotkey).DisplayName
            && _settings.PushToTalkEnabled
            && HotkeyGestureParser.Parse(_settings.ComputerUseHotkey).DisplayName == HotkeyGestureParser.Parse(ComputerUseConfiguration.DefaultHotkey).DisplayName
            && !_settings.ComputerUseEnabled
            && !_settings.MeetingRecordingShortcutEnabled
            && HotkeyGestureParser.Parse(_settings.MeetingRecordingHotkey).DisplayName == HotkeyGestureParser.Parse(new MuesliSettings().MeetingRecordingHotkey).DisplayName
            && _settings.HotkeyTriggerThresholdMs == HotkeyTriggerTiming.DefaultThresholdMilliseconds;

    private void ShowStatus(string message, bool isError = false)
    {
        RefreshDefaults();
        StatusMessage = message;
        IsStatusError = isError;
        IsStatusOpen = true;
    }
}
