using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Muesli.Windows.Core.Services;
using Muesli.Windows.Services;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.System;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace Muesli.Windows.WinUI;

/// <summary>
/// A dedicated, reusable meeting-detection notification panel. It is independent of the dashboard
/// (never uses a XamlRoot), borderless, always-on-top, absent from the taskbar and Alt+Tab, and it
/// shows without stealing focus from the detected meeting's application.
/// </summary>
/// <remarks>
/// Geometry and behaviour mirror the shipping macOS <c>MeetingNotificationController</c>. The HTML
/// design system disagrees on fade-in, corner radius, and several colours; this window follows the
/// Swift controller and records the discrepancies in docs/WINDOWS_UI_QUALIFICATION.md.
/// </remarks>
public sealed partial class MeetingNotificationWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExAppWindow = 0x00040000;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpDoNotRound = 1;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);
    private const int RgnOr = 2;
    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorDefaultToPrimary = 1;
    private const int MdtEffectiveDpi = 0;

    private const double CloseVisualSize = MeetingNotificationLayout.CloseButtonSize;
    private const double CloseHitSize = 34;

    private readonly MeetingNotificationRequest _request;
    private readonly Action<MeetingNotificationAction> _onAction;
    private readonly Action _onDismiss;
    private readonly Action _onAutoDismiss;
    private readonly Action _onClose;
    private readonly bool _reduceMotion;
    private readonly MeetingNotificationCountdown _countdown = new();
    private readonly DispatcherTimer _timer;
    private readonly IntPtr _hwnd;
    private double _cardWidth;
    private int _hoverCount;
    private bool _closing;
    private bool _suppressCallbacks;

    public MeetingNotificationWindow(
        MeetingNotificationRequest request,
        Action<MeetingNotificationAction> onAction,
        Action onDismiss,
        Action onAutoDismiss,
        Action onClose)
    {
        InitializeComponent();
        _request = request;
        _onAction = onAction;
        _onDismiss = onDismiss;
        _onAutoDismiss = onAutoDismiss;
        _onClose = onClose;
        _hwnd = WindowNative.GetWindowHandle(this);
        _reduceMotion = !new UISettings().AnimationsEnabled;

        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
        }

        HideFromTaskbarAndSwitchers();
        SuppressSystemWindowChrome();

        AttachInteraction();
        Configure();
        ApplyAccessibility();
        ApplyHighContrastIfNeeded();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += OnTimerTick;
        Activated += (_, _) => { }; // never raise the dashboard; the window may take focus for keyboard use only
        Closed += OnClosed;
    }

    public string PromptId => _request.PromptId;

    /// <summary>True when the window is currently presenting this prompt.</summary>
    public bool IsPresenting => !_closing;

    /// <summary>Prevents callbacks firing for a prompt that is being replaced by a newer one.</summary>
    public void SuppressCallbacks()
    {
        _suppressCallbacks = true;
        _timer.Stop();
        try { Close(); } catch { /* already closing */ }
    }

    /// <summary>Shows the panel on the target monitor without activating it.</summary>
    public void ShowNotification()
    {
        PositionAndSize();
        ApplyRoundedRegion();
        AppWindow.Show(false);
        _countdown.Start(_request.DismissAfterSeconds);
        _timer.Start();
    }

    private void AttachInteraction()
    {
        Card.PointerEntered += OnHoverEnter;
        Card.PointerExited += OnHoverExit;
        DismissButton.PointerEntered += OnHoverEnter;
        DismissButton.PointerExited += OnHoverExit;

        SingleActionButton.Click += (_, _) => PerformAction(_request.SingleAction);
        PrimaryJoinButton.Click += (_, _) => PerformAction(ResolvedArmedAction());
        ChevronButton.Click += (_, _) => { /* Flyout opens automatically */ };
        DismissButton.Click += (_, _) => BeginClose(MeetingNotificationOutcome.Dismissed, invokeDismiss: true, invokeAutoDismiss: false);

        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            args.Handled = true;
            BeginClose(MeetingNotificationOutcome.Dismissed, invokeDismiss: true, invokeAutoDismiss: false);
        };
        Card.KeyboardAccelerators.Add(escape);
    }

    private void OnHoverEnter(object sender, PointerRoutedEventArgs e)
    {
        _hoverCount++;
        if (_hoverCount == 1)
        {
            _countdown.Pause();
            _timer.Stop();
        }
    }

    private void OnHoverExit(object sender, PointerRoutedEventArgs e)
    {
        if (_hoverCount == 0) return;
        _hoverCount--;
        if (_hoverCount == 0)
        {
            _countdown.Resume();
            if (!_closing) _timer.Start();
        }
    }

    private void OnTimerTick(object? sender, object e)
    {
        var expired = _countdown.Advance(TimeSpan.FromMilliseconds(50));
        CountdownScale.ScaleX = _countdown.Progress;
        if (expired)
        {
            BeginClose(MeetingNotificationOutcome.AutoDismissed, invokeDismiss: false, invokeAutoDismiss: true);
        }
    }

    private void PerformAction(MeetingNotificationAction action)
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        FadeOutThen(() =>
        {
            Close();
            _onAction?.Invoke(action);
        });
    }

    private void BeginClose(MeetingNotificationOutcome outcome, bool invokeDismiss, bool invokeAutoDismiss)
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        FadeOutThen(() =>
        {
            var wasPaused = _countdown.IsPaused;
            var fireAutoDismiss = invokeAutoDismiss &&
                                  MeetingNotificationAutoDismissPolicy.FiresAutoDismissAfterFade(wasPaused);
            if (fireAutoDismiss &&
                MeetingNotificationAutoDismissPolicy.SuppressesCloseCallbackDuringAutoDismiss(hasAutoDismissHandler: true))
            {
                _suppressCallbacks = true;
            }

            Close();
            if (invokeDismiss) _onDismiss?.Invoke();
            if (fireAutoDismiss) _onAutoDismiss?.Invoke();
        });
    }

    private void FadeOutThen(Action completed)
    {
        if (_reduceMotion)
        {
            Root.Opacity = 0;
            completed();
            return;
        }

        var fade = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = new Duration(TimeSpan.FromSeconds(MeetingNotificationPolicy.FadeOutSeconds)),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(fade, Root);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Completed += (_, _) => completed();
        storyboard.Begin();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _timer.Stop();
        _countdown.Stop();
        if (!_suppressCallbacks) _onClose?.Invoke();
    }

    private MeetingNotificationAction ResolvedArmedAction()
    {
        var resolved = _request.DefaultAction.Resolved(_request.HasJoinAndRecord, _request.HasJoinOnly);
        return resolved switch
        {
            MeetingJoinDefaultAction.JoinAndRecord => MeetingNotificationAction.JoinAndRecord,
            MeetingJoinDefaultAction.JoinOnly => MeetingNotificationAction.JoinOnly,
            _ => MeetingNotificationAction.TranscribeOnly
        };
    }

    private void Configure()
    {
        TitleText.Text = _request.Title;
        SubtitleText.Text = _request.Subtitle;
        PlatformGlyph.Text = _request.Glyph;
        PlatformShortLabel.Text = _request.ShortLabel;
        PlatformBadge.Background = AccentBrush(_request.AccentHex, 0x38);
        PlatformGlyph.Foreground = AccentBrush(_request.AccentHex, 0xFF);
        PlatformShortLabel.Foreground = AccentBrush(_request.AccentHex, 0xFF);

        var hasIcon = !string.IsNullOrEmpty(_request.Glyph);
        var textX = MeetingNotificationLayout.TextX(hasIcon);

        var titleWidth = MeasureTextDip(_request.Title, 13, semibold: true);
        var subtitleWidth = MeasureTextDip(_request.Subtitle, 11, semibold: false);
        var requiredText = Math.Max(titleWidth, subtitleWidth);

        if (_request.HasSplitAction)
        {
            _cardWidth = MeetingNotificationLayout.SplitActionCardWidth();
            var textMax = MeetingNotificationLayout.SplitButtonX(_cardWidth) - MeetingNotificationLayout.ActionGap - textX;
            TitleText.Width = Math.Max(0, textMax);
            SubtitleText.Width = Math.Max(0, textMax);
            SplitHost.Visibility = Visibility.Visible;
            SingleActionButton.Visibility = Visibility.Collapsed;
            PrimaryJoinText.Text = _request.DefaultAction.Label();
            BuildAlternativesMenu();
        }
        else
        {
            _cardWidth = MeetingNotificationLayout.SingleActionCardWidth(requiredText, textX);
            var textMax = MeetingNotificationLayout.SingleActionTextWidth(_cardWidth, textX);
            TitleText.Width = Math.Max(0, textMax);
            SubtitleText.Width = Math.Max(0, textMax);
            SplitHost.Visibility = Visibility.Collapsed;
            SingleActionButton.Visibility = Visibility.Visible;
            SingleActionText.Text = _request.ActionLabel;
        }

        Canvas.SetLeft(TitleText, textX);
        Canvas.SetLeft(SubtitleText, textX);
        Canvas.SetLeft(SingleActionButton, MeetingNotificationLayout.SingleActionButtonX(_cardWidth));
        Canvas.SetLeft(SplitHost, MeetingNotificationLayout.SplitButtonX(_cardWidth));
        Canvas.SetTop(SingleActionButton, MeetingNotificationLayout.ActionTop);
        Canvas.SetTop(SplitHost, MeetingNotificationLayout.ActionTop);

        Card.Width = _cardWidth;
        CardCanvasLayout();
    }

    private void CardCanvasLayout()
    {
        CountdownBar.Width = _cardWidth;
        Canvas.SetTop(CountdownBar, MeetingNotificationLayout.CardHeight - MeetingNotificationLayout.ProgressBarHeight);
    }

    private void BuildAlternativesMenu()
    {
        AlternativesFlyout.Items.Clear();
        var alternatives = _request.DefaultAction.AvailableAlternatives(
            _request.HasJoinAndRecord, _request.HasJoinOnly);
        foreach (var alternative in alternatives)
        {
            var item = new MenuFlyoutItem
            {
                Text = alternative.Label()
            };
            AutomationProperties.SetAutomationId(item, alternative switch
            {
                MeetingJoinDefaultAction.JoinAndRecord => "MeetingNotificationMenuJoinAndRecord",
                MeetingJoinDefaultAction.JoinOnly => "MeetingNotificationMenuJoinOnly",
                _ => "MeetingNotificationMenuTranscribeOnly"
            });
            AutomationProperties.SetName(item, alternative.Label());
            item.Click += (_, _) => PerformAction(alternative switch
            {
                MeetingJoinDefaultAction.JoinAndRecord => MeetingNotificationAction.JoinAndRecord,
                MeetingJoinDefaultAction.JoinOnly => MeetingNotificationAction.JoinOnly,
                _ => MeetingNotificationAction.TranscribeOnly
            });
            AlternativesFlyout.Items.Add(item);
        }

        AutomationProperties.SetName(
            ChevronButton,
            "More meeting actions: " + string.Join(", ", alternatives.Select(a => a.Label())));
    }

    private void ApplyAccessibility()
    {
        AutomationProperties.SetAutomationId(Root, "MeetingNotificationWindow");
        AutomationProperties.SetName(Root, $"Meeting detected. {_request.Title}. {_request.Subtitle}");
        AutomationProperties.SetAutomationId(TitleText, "MeetingNotificationTitle");
        AutomationProperties.SetName(TitleText, _request.Title);
        AutomationProperties.SetAutomationId(SubtitleText, "MeetingNotificationSubtitle");
        AutomationProperties.SetName(SubtitleText, _request.Subtitle);
        AutomationProperties.SetAutomationId(CountdownBar, "MeetingNotificationCountdown");
        AutomationProperties.SetName(CountdownBar, "Time before this notification closes");
        AutomationProperties.SetAutomationId(PlatformBadge, "MeetingNotificationPlatformBadge");
        AutomationProperties.SetName(PlatformBadge, $"Meeting platform: {_request.Platform}");
        AutomationProperties.SetAutomationId(SingleActionButton, "MeetingNotificationPrimaryAction");
        AutomationProperties.SetName(SingleActionButton, _request.ActionLabel);
        AutomationProperties.SetAutomationId(PrimaryJoinButton, "MeetingNotificationPrimaryAction");
        AutomationProperties.SetName(PrimaryJoinButton, _request.DefaultAction.Label());
        AutomationProperties.SetAutomationId(ChevronButton, "MeetingNotificationChevron");
        AutomationProperties.SetAutomationId(DismissButton, "MeetingNotificationDismiss");
        AutomationProperties.SetName(DismissButton, "Dismiss");
        ToolTipService.SetToolTip(DismissButton, "Dismiss");
    }

    private void ApplyHighContrastIfNeeded()
    {
        var settings = new AccessibilitySettings();
        if (!settings.HighContrast) return;
        var window = (Brush)Application.Current.Resources["SystemColorWindowColorBrush"];
        var text = (Brush)Application.Current.Resources["SystemColorWindowTextColorBrush"];
        var highlight = (Brush)Application.Current.Resources["SystemColorHighlightColorBrush"];
        var highlightText = (Brush)Application.Current.Resources["SystemColorHighlightTextColorBrush"];
        Card.Background = window;
        Card.BorderBrush = text;
        TitleText.Foreground = text;
        SubtitleText.Foreground = text;
        CountdownBar.Background = (Brush)Application.Current.Resources["SystemColorHotlightColorBrush"];
        SingleActionButton.Background = highlight;
        PrimaryJoinButton.Background = highlight;
        ChevronButton.Background = highlight;
        SingleActionText.Foreground = highlightText;
        PrimaryJoinText.Foreground = highlightText;
        ChevronGlyph.Foreground = highlightText;
        PlatformBadge.Background = highlight;
        PlatformGlyph.Foreground = highlightText;
        PlatformShortLabel.Foreground = highlightText;
        DismissVisual.Background = window;
        DismissVisual.BorderBrush = text;
        DismissGlyph.Foreground = text;
        DismissButton.BorderBrush = text;
    }

    /// <summary>
    /// Places the panel 16 DIPs below the top and left of the right edge of the target monitor's work
    /// area: the meeting window's monitor, then the cursor's, then the primary. Uses physical pixels
    /// because <see cref="AppWindow"/> sizing is physical, scaled by the target monitor's DPI.
    /// </summary>
    private void PositionAndSize()
    {
        var monitor = ResolveMonitor();
        var work = GetWorkAreaPixels(monitor);
        var scale = GetMonitorScale(monitor);

        var widthDip = MeetingNotificationLayout.PanelWidth(_cardWidth);
        var heightDip = MeetingNotificationLayout.PanelHeight;
        var widthPx = (int)Math.Round(widthDip * scale);
        var heightPx = (int)Math.Round(heightDip * scale);
        var marginPx = (int)Math.Round(MeetingNotificationLayout.ScreenMargin * scale);

        var workDip = new DipRect(
            work.Left / scale, work.Top / scale, work.Width / scale, work.Height / scale);
        var topLeft = MeetingNotificationLayout.PlaceTopRight(workDip, widthDip, heightDip);

        var xPx = Math.Clamp(topLeft.X * scale, work.Left, Math.Max(work.Left, work.Right - widthPx));
        var yPx = Math.Clamp(topLeft.Y * scale, work.Top, Math.Max(work.Top, work.Bottom - heightPx));

        AppWindow.MoveAndResize(new RectInt32(
            (int)Math.Round(xPx), (int)Math.Round(yPx), widthPx, heightPx));
    }

    private IntPtr ResolveMonitor()
    {
        if (TryGetMeetingWindow(out var meetingHandle) && meetingHandle != IntPtr.Zero)
        {
            return MonitorFromWindow(meetingHandle, MonitorDefaultToNearest);
        }

        if (GetCursorPos(out var cursor))
        {
            return MonitorFromPoint(cursor, MonitorDefaultToNearest);
        }

        return MonitorFromWindow(IntPtr.Zero, MonitorDefaultToPrimary);
    }

    private bool TryGetMeetingWindow(out IntPtr handle)
    {
        handle = _request.WindowHandle;
        return handle != IntPtr.Zero;
    }

    private double GetMonitorScale(IntPtr monitor)
    {
        try
        {
            if (GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
            {
                return Math.Max(1d, dpiX / 96d);
            }
        }
        catch
        {
            // Fall through to the window DPI.
        }

        return Math.Max(1d, GetDpiForWindow(_hwnd) / 96d);
    }

    private static DipRect GetWorkAreaPixels(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            var rect = info.WorkArea;
            return new DipRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }

        var primary = GetWorkAreaPixels(MonitorFromWindow(IntPtr.Zero, MonitorDefaultToPrimary));
        return primary;
    }

    private void ApplyRoundedRegion()
    {
        try
        {
            var scale = GetMonitorScale(ResolveMonitor());
            var right = (int)Math.Round((MeetingNotificationLayout.CardLeftInset + _cardWidth) * scale);
            var bottom = (int)Math.Round(MeetingNotificationLayout.CardHeight * scale);
            var diameter = (int)Math.Round(MeetingNotificationLayout.CardCornerRadius * 2 * scale);
            var card = CreateRoundRectRgn(0, 0, right + 1, bottom + 1, diameter, diameter);

            var centreX = (int)Math.Round((1 + (CloseVisualSize / 2)) * scale);
            var centreY = (int)Math.Round((CloseVisualSize / 2) * scale);
            var radius = (int)Math.Round(CloseVisualSize / 2 * scale);
            var close = CreateEllipticRgn(centreX - radius, centreY - radius, centreX + radius, centreY + radius);

            var combined = CreateRectRgn(0, 0, 0, 0);
            CombineRgn(combined, card, close, RgnOr);
            DeleteObject(card);
            DeleteObject(close);
            if (SetWindowRgn(_hwnd, combined, true) == 0)
            {
                DeleteObject(combined);
            }
        }
        catch
        {
            // A shaped-window failure must not stop the notification from showing.
        }
    }

    private double MeasureTextDip(string text, double fontSize, bool semibold)
    {
        try
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                FontFamily = (FontFamily)Application.Current.Resources[
                    semibold ? "MuesliFontFamilySemiBold" : "MuesliFontFamily"]
            };
            block.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            return block.DesiredSize.Width;
        }
        catch
        {
            return MeetingNotificationTextMetrics.EstimateWidthDip(text, fontSize);
        }
    }

    private static Brush AccentBrush(string hex, byte alpha)
    {
        var value = (hex ?? "").Trim().TrimStart('#');
        if (value.Length == 6)
        {
            try
            {
                var packed = Convert.ToUInt32(value, 16);
                return new SolidColorBrush(global::Windows.UI.Color.FromArgb(
                    alpha,
                    (byte)((packed >> 16) & 0xFF),
                    (byte)((packed >> 8) & 0xFF),
                    (byte)(packed & 0xFF)));
            }
            catch (FormatException) { }
            catch (OverflowException) { }
        }

        return new SolidColorBrush(global::Windows.UI.Color.FromArgb(alpha, 0x7C, 0x87, 0x98));
    }

    private void HideFromTaskbarAndSwitchers()
    {
        var style = GetWindowLongPtr(_hwnd, GwlExStyle);
        style = (style | WsExToolWindow | WsExNoActivate) & ~WsExAppWindow;
        SetWindowLongPtr(_hwnd, GwlExStyle, style);
    }

    private void SuppressSystemWindowChrome()
    {
        var none = DwmColorNone;
        DwmSetWindowAttribute(_hwnd, DwmwaBorderColor, ref none, sizeof(int));
        var doNotRound = DwmwcpDoNotRound;
        DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref doNotRound, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern long GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern long SetWindowLongPtr(IntPtr hWnd, int nIndex, long value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr dest, IntPtr source1, IntPtr source2, int mode);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
