using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.ViewModels;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.System;

namespace Muesli.Windows.WinUI;

public sealed class OnboardingBoolVisConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var visible = value is true;
        if (Invert) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed partial class OnboardingWindow : Window
{
    private const double DefaultWidthDip = 820;
    // P8-02: the permissions step is the tallest, and at 700 DIP its last row was cut in half by the
    // fixed footer. 780 DIP clears it on a 1080p/125 % display; ResizeForDpi clamps to the work area
    // so a shorter display still gets a window that fits and the ScrollViewer keeps the overflow
    // reachable.
    private const double DefaultHeightDip = 780;
    private const double MinimumWidthDip = 560;
    private const double MinimumHeightDip = 560;
    private const double RailWidthDip = 236;

    private readonly IntPtr _hwnd;
    private double _effectiveWidthDip = DefaultWidthDip;
    private double _effectiveHeightDip = DefaultHeightDip;
    private uint _lastDpi;
    private bool _resizingForDpi;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    public OnboardingViewModel ViewModel { get; }

    public OnboardingWindow()
    {
        ViewModel = new OnboardingViewModel(
            App.Settings,
            App.Startup,
            App.Dictation,
            App.Models,
            new OnboardingProgressStore(App.Library.Profile.OnboardingProgressPath));
        InitializeComponent();
        RootGrid.DataContext = ViewModel;
        ViewModel.Completed += (_, finished) =>
        {
            Completed = finished;
            Close();
        };
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ViewModel.StatusIsError) or nameof(ViewModel.Status) or nameof(ViewModel.ShowStatus))
            {
                StatusBar.Severity = ViewModel.StatusIsError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
            }

            if (args.PropertyName == nameof(ViewModel.Step))
            {
                // A retained offset would open the next step mid-card with no cue that anything sits
                // above, the same reason Prompt 5 reset the Settings scroller on tab change.
                ContentScroller.ChangeView(null, 0, null, true);
                UpdateScrollAffordance();
            }
        };

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _lastDpi = GetDpiForWindow(_hwnd);
        // P8-03: without this the OS paints the system-accent DWM title bar and setup reads as a
        // different product from the dashboard, which has extended into its own title bar since P1-02.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ResizeForDpi(_lastDpi, forceDefaultSize: true);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
            presenter.IsResizable = true;
        }

        AppWindow.Changed += AppWindow_Changed;
        Activated += (_, _) => ViewModel.NotifyActivated();
        Closed += (_, _) => ViewModel.Dispose();
        RootGrid.ActualThemeChanged += (_, _) => App.ApplyCaptionButtonColors(AppWindow, RootGrid.ActualTheme);
        RootGrid.Loaded += (_, _) =>
        {
            App.ApplyCaptionButtonColors(AppWindow, RootGrid.ActualTheme);
            ResizeForDpi(GetDpiForWindow(_hwnd), forceDefaultSize: false);
            ApplyCompactLayout(RootGrid.ActualWidth);
            UpdateScrollAffordance();
        };
        // The ScrollViewer's own SizeChanged does not fire when the step swaps one card stack for a
        // taller one, so the content panel drives the same re-evaluation.
        ContentColumn.SizeChanged += ContentScroller_SizeChanged;
    }

    public bool Completed { get; private set; }

    public static Visibility ToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ToInvertedVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    public Visibility BoolToVisibility(bool value) => ToVisibility(value);

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyCompactLayout(e.NewSize.Width);

    private void ApplyCompactLayout(double width)
    {
        var compact = width > 0 && width < 760;
        var stackedFooter = width > 0 && width < 640;
        StepRail.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RailSplitter.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RailColumn.Width = compact ? new GridLength(0) : new GridLength(RailWidthDip);
        // Shared gutter and vertical rhythm (App.xaml:26-45 contract) rather than this window's own
        // 20/32 pair. The rail-visibility threshold above stays local because it is a structural
        // choice, not a padding one. The old bottom padding of 8 (0 when compact) is what let the
        // permissions list sit flush against the footer bar and read as clipped (P8-02).
        var contentWidth = Math.Max(0, width - (compact ? 0 : RailWidthDip + 1));
        var padding = MuesliPageMetrics.ContentPadding(contentWidth);
        ContentColumn.Padding = padding;
        FooterBar.Padding = new Thickness(padding.Left, compact ? 12 : 14, padding.Right, compact ? 16 : 18);
        TitleText.FontSize = compact ? 26 : 32;
        IndicatorPreviewActions.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
        Grid.SetColumn(PermissionsToolbar, compact ? 0 : 1);
        Grid.SetRow(PermissionsToolbar, compact ? 1 : 0);
        PermissionsToolbar.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        FooterActions.Orientation = stackedFooter ? Orientation.Vertical : Orientation.Horizontal;
        SkipButton.HorizontalAlignment = stackedFooter ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        NextButton.HorizontalAlignment = stackedFooter ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        FinishButton.HorizontalAlignment = stackedFooter ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
    }

    // P8-02: the permissions step is taller than the window, and a WinUI ScrollViewer offers no
    // persistent cue that content continues below the fold — its ScrollBar stays in the NoIndicator
    // visual state until the pointer enters the region, so with a fixed footer bar the cut last row
    // reads as a layout bug rather than as scrollable content. MoreBelowButton is an explicit,
    // focusable, screen-reader-visible affordance driven by these members: shown only while the
    // scroller genuinely has unseen content below, and invoking it pages down.
    private void ContentScroller_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e) =>
        UpdateScrollAffordance();

    private void ContentScroller_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateScrollAffordance();

    private void MoreBelow_Click(object sender, RoutedEventArgs e)
    {
        var viewport = ContentScroller.ViewportHeight;
        var advance = viewport > 0 ? viewport * 0.85 : 240;
        var target = Math.Min(ContentScroller.ScrollableHeight, ContentScroller.VerticalOffset + advance);
        ContentScroller.ChangeView(null, target, null);
    }

    private void UpdateScrollAffordance()
    {
        // One DIP of slack: both figures are doubles and a fully scrolled viewer can land a
        // fraction short of the end, which would otherwise leave the cue permanently visible.
        var hasMoreBelow = ContentScroller.ScrollableHeight > 1 &&
                           ContentScroller.VerticalOffset < ContentScroller.ScrollableHeight - 1;
        var visibility = hasMoreBelow ? Visibility.Visible : Visibility.Collapsed;
        if (MoreBelowButton.Visibility == visibility)
        {
            return;
        }

        // Never collapse the control that currently holds keyboard focus: hand focus to the footer
        // action the user would have reached next instead of stranding it on a hidden element.
        if (!hasMoreBelow && MoreBelowButton.FocusState != FocusState.Unfocused)
        {
            var next = NextButton.Visibility == Visibility.Visible ? NextButton : FinishButton;
            next.Focus(FocusState.Programmatic);
        }

        MoreBelowButton.Visibility = visibility;
    }

    private void GrantPermission_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OnboardingPermissionItem item })
        {
            ViewModel.OpenPermissionSettingsCommand.Execute(item);
        }
    }

    private void PrepareModel_Click(object sender, RoutedEventArgs e) =>
        ExecuteModelCommand(sender, ViewModel.PrepareModelCommand);

    private void VerifyModel_Click(object sender, RoutedEventArgs e) =>
        ExecuteModelCommand(sender, ViewModel.VerifyModelCommand);

    private void RetryModel_Click(object sender, RoutedEventArgs e) =>
        ExecuteModelCommand(sender, ViewModel.RetryModelCommand);

    private void CancelModel_Click(object sender, RoutedEventArgs e) =>
        ExecuteModelCommand(sender, ViewModel.CancelModelCommand);

    private static void ExecuteModelCommand(object sender, IRelayCommand command)
    {
        if (sender is FrameworkElement { DataContext: OnboardingModelRoleItem item } && command.CanExecute(item))
        {
            command.Execute(item);
        }
    }

    private void RootGrid_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        e.Handled = true;
        if (ViewModel.SkipCommand.CanExecute(null))
        {
            ViewModel.SkipCommand.Execute(null);
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_resizingForDpi) return;

        var dpi = GetDpiForWindow(_hwnd);
        var scale = Math.Max(1d, dpi / 96d);
        var dpiChanged = dpi != _lastDpi;
        if (args.DidSizeChange && !dpiChanged)
        {
            _effectiveWidthDip = Math.Max(MinimumWidthDip, sender.Size.Width / scale);
            _effectiveHeightDip = Math.Max(MinimumHeightDip, sender.Size.Height / scale);
            if (sender.Size.Width < _effectiveWidthDip * scale ||
                sender.Size.Height < _effectiveHeightDip * scale)
            {
                ResizeForDpi(dpi, forceDefaultSize: false);
            }
        }

        if (dpiChanged)
        {
            _lastDpi = dpi;
            ResizeForDpi(dpi, forceDefaultSize: false);
        }
    }

    /// <summary>
    /// Applies the current effective size at the given DPI, clamped to the monitor's work area.
    /// </summary>
    /// <remarks>
    /// Same defect class as P1-03, which <c>MainWindow.ResizeForDpi</c> (MainWindow.xaml.cs:103-181)
    /// already fixed: this window only called <see cref="AppWindow.Resize"/>, so a fresh profile
    /// opened setup at 1025x875 px from 224,224 on a 1020 px work area — 79 px of the footer, and
    /// therefore Next/Finish, off the bottom of the screen. The clamp and the centred default-size
    /// placement are copied from that implementation rather than re-derived.
    /// </remarks>
    private void ResizeForDpi(uint dpi, bool forceDefaultSize)
    {
        var scale = Math.Max(1d, dpi / 96d);
        if (forceDefaultSize)
        {
            _effectiveWidthDip = DefaultWidthDip;
            _effectiveHeightDip = DefaultHeightDip;
        }

        var width = Math.Max(MinimumWidthDip, _effectiveWidthDip);
        var height = Math.Max(MinimumHeightDip, _effectiveHeightDip);
        var widthPixels = (int)Math.Round(width * scale);
        var heightPixels = (int)Math.Round(height * scale);

        var workArea = TryGetWorkArea();
        if (workArea is { } area && area.Width > 0 && area.Height > 0)
        {
            widthPixels = Math.Min(widthPixels, area.Width);
            heightPixels = Math.Min(heightPixels, area.Height);
            _effectiveWidthDip = Math.Max(MinimumWidthDip, widthPixels / scale);
            _effectiveHeightDip = Math.Max(MinimumHeightDip, heightPixels / scale);
        }

        _resizingForDpi = true;
        try
        {
            if (workArea is { } bounds && bounds.Width > 0 && bounds.Height > 0)
            {
                var maxLeft = Math.Max(bounds.X, bounds.X + bounds.Width - widthPixels);
                var maxTop = Math.Max(bounds.Y, bounds.Y + bounds.Height - heightPixels);
                var left = forceDefaultSize
                    ? bounds.X + Math.Max(0, (bounds.Width - widthPixels) / 2)
                    : Math.Clamp(AppWindow.Position.X, bounds.X, maxLeft);
                var top = forceDefaultSize
                    ? bounds.Y + Math.Max(0, (bounds.Height - heightPixels) / 2)
                    : Math.Clamp(AppWindow.Position.Y, bounds.Y, maxTop);
                AppWindow.MoveAndResize(new RectInt32(left, top, widthPixels, heightPixels));
            }
            else
            {
                AppWindow.Resize(new SizeInt32(widthPixels, heightPixels));
            }
        }
        finally
        {
            _resizingForDpi = false;
        }
    }

    /// <summary>
    /// Work area (screen minus taskbar) of the display the window is on, in physical pixels. Returns
    /// null when no display resolves so sizing falls back to the previous behaviour rather than failing.
    /// </summary>
    private RectInt32? TryGetWorkArea()
    {
        try
        {
            return DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest)?.WorkArea;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
