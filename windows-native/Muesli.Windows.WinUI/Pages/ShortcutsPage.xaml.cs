using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Muesli.Windows.WinUI.ViewModels;
using Windows.System;

namespace Muesli.Windows.WinUI.Pages;

public sealed partial class ShortcutsPage : Page
{
    private bool _ready;

    public ShortcutsPageViewModel ViewModel { get; } = new(App.Settings, ReapplyDictationHotkey, App.ComputerUse.RegisterVoiceShortcut);

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
            }
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

    private void ChangeComputerUseShortcut_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.BeginCapture("computer-use");
        ApplyCaptureChrome();
        Focus(FocusState.Programmatic);
    }

    private void ChangeShortcut_Click(object sender, RoutedEventArgs e)
    {
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
        var button = ViewModel.IsCapturingComputerUse ? ChangeComputerUseButton : ChangePushToTalkButton;
        if (e.Key == VirtualKey.Escape)
        {
            ViewModel.CancelCapture();
            ApplyCaptureChrome();
            button.Focus(FocusState.Programmatic);
            return;
        }
        if (CaptureGesture(e.Key) is not { } gesture) return;
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
        var push = ViewModel.IsCapturing && !computerUse;
        PushKeycapWell.BorderBrush = ThemeBrush(push ? "MuesliAccentBrush" : "MuesliBorderSoftBrush");
        PushKeycapWell.Background = ThemeBrush(push ? "MuesliAccentMutedBrush" : "MuesliSurfaceRaisedBrush");
        ComputerUseKeycapWell.BorderBrush = ThemeBrush(computerUse ? "MuesliAccentBrush" : "MuesliBorderSoftBrush");
        ComputerUseKeycapWell.Background = ThemeBrush(computerUse ? "MuesliAccentMutedBrush" : "MuesliSurfaceRaisedBrush");
    }

    private static Brush ThemeBrush(string key) =>
        Application.Current.Resources[key] is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    /// <summary>
    /// The gesture for a key press with the modifiers held now ("Ctrl+Shift+D"), or null while
    /// only a modifier is down or the key cannot be a shortcut. Onboarding captures with this too.
    /// </summary>
    internal static string? CaptureGesture(VirtualKey key)
    {
        if (key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
            VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or
            VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or
            VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            return null;
        }

        var label = KeyLabel(key);
        if (label is null) return null;
        var parts = new List<string>();
        if (IsDown(VirtualKey.Control)) parts.Add("Ctrl");
        if (IsDown(VirtualKey.Shift)) parts.Add("Shift");
        if (IsDown(VirtualKey.Menu)) parts.Add("Alt");
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) parts.Add("Win");
        parts.Add(label);
        return string.Join('+', parts);
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static string? KeyLabel(VirtualKey key)
    {
        var value = (int)key;
        if (value is >= (int)VirtualKey.A and <= (int)VirtualKey.Z ||
            value is >= (int)VirtualKey.Number0 and <= (int)VirtualKey.Number9)
        {
            return ((char)value).ToString();
        }
        if (value is >= (int)VirtualKey.F1 and <= (int)VirtualKey.F24)
        {
            return $"F{value - (int)VirtualKey.F1 + 1}";
        }
        return key switch
        {
            VirtualKey.Space => "Space",
            VirtualKey.Tab => "Tab",
            VirtualKey.Enter => "Enter",
            VirtualKey.Back => "Backspace",
            VirtualKey.Delete => "Delete",
            VirtualKey.Insert => "Insert",
            VirtualKey.Home => "Home",
            VirtualKey.End => "End",
            VirtualKey.PageUp => "PageUp",
            VirtualKey.PageDown => "PageDown",
            VirtualKey.Up => "Up",
            VirtualKey.Down => "Down",
            VirtualKey.Left => "Left",
            VirtualKey.Right => "Right",
            _ => null
        };
    }
}
