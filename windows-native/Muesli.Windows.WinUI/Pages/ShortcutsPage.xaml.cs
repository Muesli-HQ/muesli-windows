using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Muesli.Windows.WinUI.Services;
using Muesli.Windows.WinUI.ViewModels;
using Windows.System;

namespace Muesli.Windows.WinUI.Pages;

public sealed partial class ShortcutsPage : Page
{
    private bool _ready;
    private readonly HotkeyRecorder _recorder = new();

    public ShortcutsPageViewModel ViewModel { get; } = new(App.Settings, ReapplyDictationHotkey, App.ComputerUse.RegisterVoiceShortcut, App.RegisterMeetingShortcut, App.Quill.RegisterShortcut);

    /// <summary>
    /// Re-installs the global push-to-talk hook from the saved gesture. Returns null when the hook
    /// is live, otherwise the host's own explanation so the page never claims a shortcut works when
    /// Windows refused it.
    /// </summary>
    private static string? ReapplyDictationHotkey()
    {
        App.Dictation.RegisterHotkey();
        return App.Dictation.IsHotkeyRegistered ? null : App.Dictation.Status;
    }

    public ShortcutsPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ViewModel.IsStatusError) or nameof(ViewModel.IsStatusOpen))
            {
                StatusBar.Severity = ViewModel.IsStatusError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
            }

            if (args.PropertyName is nameof(ViewModel.IsCapturing) or null)
            {
                // Pressing the current shortcut must record it, not start a dictation.
                App.Dictation.PauseHotkey(ViewModel.IsCapturing);
            }
        };
        // Leaving the page mid-capture must give the shortcut back.
        Unloaded += (_, _) =>
        {
            if (ViewModel.IsCapturing) ViewModel.CancelCapture();
        };
        Loaded += (_, _) =>
        {
            ViewModel.Load();
            ApplyResponsiveLayout(ActualWidth);
            _ready = true;
        };
        SizeChanged += (_, args) => ApplyResponsiveLayout(args.NewSize.Width);
    }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility InvertBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

    /// <summary>macOS hotkeyBadge prints the whole chord in one badge: "Ctrl + Shift + F8".</summary>
    public static string Keys(IReadOnlyList<string> parts) => string.Join(" + ", parts);

    private void ChangeComputerUseShortcut_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.BeginCapture("computer-use");
        ApplyCaptureChrome();
        Focus(FocusState.Programmatic);
    }

    private void ChangeShortcut_Click(object sender, RoutedEventArgs e)
    {
        _recorder.Reset();
        ViewModel.BeginCapture();
        Focus(FocusState.Programmatic);
    }

    /// <summary>Persists double-tap (hands-free) enablement from this page once the user toggles it.</summary>
    private void HandsFree_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ViewModel.SetHandsFreeEnabled(HandsFreeToggle.IsOn);
    }

    private void PushToTalk_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || PushToTalkToggle.IsOn == ViewModel.PushToTalkEnabled) return;
        ViewModel.SetPushToTalkEnabled(PushToTalkToggle.IsOn);
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e) => ViewModel.ResetToDefaults();

    /// <summary>Persists Computer Use enablement from this page once the user toggles it.</summary>
    private void ComputerUse_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ViewModel.SetComputerUseEnabled(ComputerUseToggle.IsOn);
    }

    /// <summary>Routes to the Settings Computer Use section when the feature is not yet configured.</summary>
    private void ComputerUseConfigure_Click(object sender, RoutedEventArgs e)
    {
        (App.Window as MainWindow)?.ShellPage?.SyncRailSelection("settings");
        Frame?.Navigate(typeof(SettingsPage), "computer-use");
    }

    private void Quill_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || QuillToggle.IsOn == ViewModel.QuillEnabled) return;
        ViewModel.SetQuillEnabled(QuillToggle.IsOn);
        // Refused (not signed in): put the switch back, since the OneWay binding saw no change.
        QuillToggle.IsOn = ViewModel.QuillEnabled;
    }

    private void ChangeQuillShortcut_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.BeginCapture("quill");
        ApplyCaptureChrome();
        Focus(FocusState.Programmatic);
    }

    private void MeetingShortcut_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || MeetingShortcutToggle.IsOn == ViewModel.MeetingShortcutEnabled) return;
        ViewModel.SetMeetingShortcutEnabled(MeetingShortcutToggle.IsOn);
    }

    private void ChangeMeetingShortcut_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.BeginCapture("meeting");
        ApplyCaptureChrome();
        Focus(FocusState.Programmatic);
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (!ViewModel.IsCapturing)
        {
            base.OnKeyDown(e);
            return;
        }

        e.Handled = true;
        var button = ViewModel.IsCapturingComputerUse ? ChangeComputerUseButton
            : ViewModel.IsCapturingMeeting ? ChangeMeetingButton
            : ViewModel.IsCapturingQuill ? ChangeQuillButton : ChangePushToTalkButton;
        if (e.Key == VirtualKey.Escape)
        {
            ViewModel.CancelCapture();
            ApplyCaptureChrome();
            button.Focus(FocusState.Programmatic);
            return;
        }
        Apply(_recorder.KeyDown(e));
    }

    protected override void OnKeyUp(KeyRoutedEventArgs e)
    {
        if (!ViewModel.IsCapturing)
        {
            base.OnKeyUp(e);
            return;
        }

        e.Handled = true;
        Apply(_recorder.KeyUp(e));
    }

    private void Apply(string? gesture)
    {
        if (gesture is null) return;
        var button = ViewModel.IsCapturingComputerUse ? ChangeComputerUseButton
            : ViewModel.IsCapturingMeeting ? ChangeMeetingButton
            : ViewModel.IsCapturingQuill ? ChangeQuillButton : ChangePushToTalkButton;
        ViewModel.ApplyCapturedGesture(gesture);
        ApplyCaptureChrome();
        button.Focus(FocusState.Programmatic);
    }

    private void HoldThreshold_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (IsLoaded) ViewModel.ApplyHoldThreshold();
    }

    private void ApplyResponsiveLayout(double width) =>
        ContentRoot.Padding = MuesliPageMetrics.ContentPadding(width);

    /// <summary>While capturing, the badge takes the accent tint so it reads as listening.</summary>
    private void ApplyCaptureChrome()
    {
        var computerUse = ViewModel.IsCapturingComputerUse;
        var meeting = ViewModel.IsCapturingMeeting;
        var quill = ViewModel.IsCapturingQuill;
        QuillKeycapWell.BorderBrush = ThemeBrush(quill ? "MuesliAccentBrush" : "MuesliBorderSoftBrush");
        QuillKeycapWell.Background = ThemeBrush(quill ? "MuesliAccentMutedBrush" : "MuesliSurfaceRaisedBrush");
        MeetingKeycapWell.BorderBrush = ThemeBrush(meeting ? "MuesliAccentBrush" : "MuesliBorderSoftBrush");
        MeetingKeycapWell.Background = ThemeBrush(meeting ? "MuesliAccentMutedBrush" : "MuesliSurfaceRaisedBrush");
        ComputerUseKeycapWell.BorderBrush = ThemeBrush(computerUse ? "MuesliAccentBrush" : "MuesliBorderSoftBrush");
        ComputerUseKeycapWell.Background = ThemeBrush(computerUse ? "MuesliAccentMutedBrush" : "MuesliSurfaceRaisedBrush");
    }

    private static Brush ThemeBrush(string key) =>
        Application.Current.Resources[key] is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
}
