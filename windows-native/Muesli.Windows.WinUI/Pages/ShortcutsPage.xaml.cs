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
            }
        };
        Loaded += (_, _) =>
        {
            ViewModel.Load();
            ApplyResponsiveLayout(ContentRoot.ActualWidth);
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
        if (e.Key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
            VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or
            VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or
            VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            return;
        }

        var label = KeyLabel(e.Key);
        if (label is null) return;
        var parts = new List<string>();
        if (IsDown(VirtualKey.Control)) parts.Add("Ctrl");
        if (IsDown(VirtualKey.Shift)) parts.Add("Shift");
        if (IsDown(VirtualKey.Menu)) parts.Add("Alt");
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) parts.Add("Win");
        parts.Add(label);
        ViewModel.ApplyCapturedGesture(string.Join('+', parts));
        ApplyCaptureChrome();
        ChangePushToTalkButton.Focus(FocusState.Programmatic);
    }

    private void HoldThreshold_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (IsLoaded) ViewModel.ApplyHoldThreshold();
    }

    private void ApplyResponsiveLayout(double width)
    {
        // Shared tier, not a private 760 breakpoint (App.xaml:26-45).
        var compact = MuesliPageMetrics.IsCompact(width) || MuesliPageMetrics.IsMedium(width);
        ContentRoot.Padding = MuesliPageMetrics.ContentPadding(width);

        if (compact)
        {
            Grid.SetRow(PttHoldRow, 1);
            Grid.SetColumn(PttHoldRow, 0);
            PttHoldRow.HorizontalAlignment = HorizontalAlignment.Left;
            PttHoldRow.Margin = new Thickness(0, 12, 0, 0);
        }
        else
        {
            Grid.SetRow(PttHoldRow, 0);
            Grid.SetColumn(PttHoldRow, 1);
            PttHoldRow.HorizontalAlignment = HorizontalAlignment.Right;
            PttHoldRow.Margin = new Thickness(0);
        }

        PlaceUnavailableRow(ComputerUseKeycap, ComputerUseReason, ComputerUseChange, compact);
        PlaceUnavailableRow(QuillKeycap, QuillReason, QuillChange, compact);
        PlaceUnavailableRow(MeetingKeycap, MeetingReason, MeetingChange, compact);
    }

    private static void PlaceUnavailableRow(FrameworkElement keycap, FrameworkElement reason, FrameworkElement button, bool compact)
    {
        if (compact)
        {
            Grid.SetRow(keycap, 0);
            Grid.SetColumn(keycap, 0);
            Grid.SetColumnSpan(keycap, 1);

            Grid.SetRow(button, 0);
            Grid.SetColumn(button, 1);
            Grid.SetColumnSpan(button, 2);
            button.HorizontalAlignment = HorizontalAlignment.Right;

            Grid.SetRow(reason, 1);
            Grid.SetColumn(reason, 0);
            Grid.SetColumnSpan(reason, 3);
        }
        else
        {
            Grid.SetRow(keycap, 0);
            Grid.SetColumn(keycap, 0);
            Grid.SetColumnSpan(keycap, 1);

            Grid.SetRow(reason, 0);
            Grid.SetColumn(reason, 1);
            Grid.SetColumnSpan(reason, 1);

            Grid.SetRow(button, 0);
            Grid.SetColumn(button, 2);
            Grid.SetColumnSpan(button, 1);
            button.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }

    private void ApplyCaptureChrome()
    {
        var capturing = ViewModel.IsCapturing;
        PushKeycapWell.BorderBrush = ThemeBrush(capturing ? "MuesliBorderStrongBrush" : "ControlFillColorTransparentBrush");
        PushKeycapWell.Background = ThemeBrush(capturing ? "MuesliAccentMutedBrush" : "ControlFillColorTransparentBrush");
        ChangePushToTalkButton.Style = capturing
            ? (Style)Application.Current.Resources["MuesliPrimaryButtonStyle"]
            : (Style)Application.Current.Resources["MuesliSecondaryButtonStyle"];
    }

    private static Brush ThemeBrush(string key) =>
        Application.Current.Resources[key] is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private static double Token(string key, double fallback) =>
        Application.Current.Resources[key] is double value ? value : fallback;

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
