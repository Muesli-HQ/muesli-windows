using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.Pages;
using Muesli.Windows.WinUI.ViewModels;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.System;

namespace Muesli.Windows.WinUI;

public sealed partial class OnboardingWindow : Window
{
    // macOS OnboardingView is 640x520; the bottom bar and Windows caption strip need a little more height.
    private const double WidthDip = 640;
    private const double HeightDip = 600;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    public OnboardingViewModel ViewModel { get; }

    public OnboardingWindow()
    {
        ViewModel = new OnboardingViewModel(
            App.Settings,
            App.Dictation,
            App.Models,
            App.ComputerUse,
            new OnboardingProgressStore(App.Library.Profile.OnboardingProgressPath));
        InitializeComponent();
        ViewModel.Completed += (_, finished) =>
        {
            Completed = finished;
            Close();
        };
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ViewModel.Step))
            {
                ContentScroller.ChangeView(0, 0, null, true);
                ApiKeyBox.Password = "";
            }
        };

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
        }
        CenterOnScreen();

        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated) ViewModel.NotifyActivated();
        };
        Closed += (_, _) => ViewModel.Dispose();
        RootGrid.ActualThemeChanged += (_, _) => App.ApplyCaptionButtonColors(AppWindow, RootGrid.ActualTheme);
        RootGrid.Loaded += (_, _) => App.ApplyCaptionButtonColors(AppWindow, RootGrid.ActualTheme);
        // A ScrollViewer sizes a MaxWidth panel to its content, so a narrow step would sit off
        // centre; the column takes the viewport width up to the macOS 520 DIP instead.
        ContentScroller.SizeChanged += (_, args) => ContentColumn.Width = Math.Min(520, args.NewSize.Width);
    }

    public bool Completed { get; private set; }

    public static Visibility HasText(string? value) =>
        string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static string Chevron(bool expanded) => expanded ? "" : "";

    public static Brush CardBorder(bool selected) => Theme(selected ? "MuesliAccentBrush" : "MuesliBorderSoftBrush");

    public static Brush CardBackground(bool selected) => Theme(selected ? "MuesliAccentMutedBrush" : "MuesliSurfaceRaisedBrush");

    public static Brush DotFill(bool current) => Theme(current ? "MuesliAccentBrush" : "MuesliTextTertiaryBrush");

    public static Brush ResultForeground(bool hasResult) => Theme(hasResult ? "MuesliTextPrimaryBrush" : "MuesliTextTertiaryBrush");

    private static Brush Theme(string key) =>
        Application.Current.Resources[key] as Brush ?? new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private void Primary_Click(object sender, RoutedEventArgs e) => ViewModel.PrimaryCommand.Execute(null);

    private void GrantMicrophone_Click(object sender, RoutedEventArgs e) => ViewModel.GrantMicrophoneCommand.Execute(null);

    private void ModelCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OnboardingModelCard card }) ViewModel.ChooseModelCommand.Execute(card);
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e) => ViewModel.ApiKey = ApiKeyBox.Password;

    /// <summary>"Change Shortcut" listens here, with the same rules as the Shortcuts page.</summary>
    private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!ViewModel.IsRecordingHotkey) return;
        e.Handled = true;
        if (e.Key == VirtualKey.Escape)
        {
            ViewModel.CancelHotkeyRecording();
            return;
        }
        if (ShortcutsPage.CaptureGesture(e.Key) is { } gesture) ViewModel.ApplyCapturedHotkey(gesture);
    }

    private void CenterOnScreen()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = Math.Max(1d, GetDpiForWindow(hwnd) / 96d);
        var width = (int)Math.Round(WidthDip * scale);
        var height = (int)Math.Round(HeightDip * scale);
        try
        {
            if (DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest)?.WorkArea is { Width: > 0, Height: > 0 } area)
            {
                width = Math.Min(width, area.Width);
                height = Math.Min(height, area.Height);
                AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
                return;
            }
        }
        catch (Exception)
        {
            // No display resolved; fall back to a plain resize.
        }
        AppWindow.Resize(new SizeInt32(width, height));
    }
}
