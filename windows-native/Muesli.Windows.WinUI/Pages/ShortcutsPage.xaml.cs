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

    public ShortcutsPageViewModel ViewModel { get; } = new(App.Settings, ReapplyDictationHotkey);

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
                ApplyCaptureChrome();
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
            ApplyCaptureChrome();
            _ready = true;
        };
        SizeChanged += (_, args) => ApplyResponsiveLayout(args.NewSize.Width);
        ActualThemeChanged += (_, _) => ApplyCaptureChrome();
    }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility InvertBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

    /// <summary>macOS hotkeyBadge prints the whole chord in one badge: "Ctrl + Shift + F8".</summary>
    public static string Keys(IReadOnlyList<string> parts) => string.Join(" + ", parts);

    private void ChangeShortcut_Click(object sender, RoutedEventArgs e)
    {
        _recorder.Reset();
        ViewModel.BeginCapture();
        ApplyCaptureChrome();
        Focus(FocusState.Programmatic);
    }

    /// <summary>Persists double-tap (hands-free) enablement from this page once the user toggles it.</summary>
    private void HandsFree_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ViewModel.SetHandsFreeEnabled(HandsFreeToggle.IsOn);
    }

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

    /// <summary>Meeting recording has no global shortcut yet; open the page that starts it.</summary>
    private void OpenMeetings_Click(object sender, RoutedEventArgs e)
    {
        (App.Window as MainWindow)?.ShellPage?.SyncRailSelection("meetings");
        Frame?.Navigate(typeof(MeetingsPage));
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (!ViewModel.IsCapturing)
        {
            base.OnKeyDown(e);
            return;
        }

        e.Handled = true;
        if (e.Key == VirtualKey.Escape)
        {
            ViewModel.CancelCapture();
            ApplyCaptureChrome();
            ChangePushToTalkButton.Focus(FocusState.Programmatic);
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
        ViewModel.ApplyCapturedGesture(gesture);
        ApplyCaptureChrome();
        ChangePushToTalkButton.Focus(FocusState.Programmatic);
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
        var capturing = ViewModel.IsCapturing;
        PushKeycapWell.BorderBrush = ThemeBrush(capturing ? "MuesliAccentBrush" : "MuesliBorderSoftBrush");
        PushKeycapWell.Background = ThemeBrush(capturing ? "MuesliAccentMutedBrush" : "MuesliSurfaceRaisedBrush");
    }

    private static Brush ThemeBrush(string key) =>
        Application.Current.Resources[key] is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
}
