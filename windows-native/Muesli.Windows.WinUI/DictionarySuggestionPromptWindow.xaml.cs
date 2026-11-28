using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Muesli.Windows.Services;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.System;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace Muesli.Windows.WinUI;

/// <summary>
/// "Add correction?" card offered after the user corrects a dictated word. A port of macOS
/// <c>DictionarySuggestionPromptController</c>: same card, buttons and close control, shown
/// beside the floating pill without taking focus, closing itself after 15 seconds unless the
/// pointer is over it. Add and Ignore are answers; closing or timing out is not.
/// </summary>
public sealed partial class DictionarySuggestionPromptWindow : Window
{
    private const double CardWidth = 356;
    private const double CardHeight = 96;
    private const double CloseSize = 22;
    private const double CardInset = CloseSize / 2 + 1;
    private const double PanelWidth = CardWidth + CardInset;
    private const double PanelHeight = CardHeight + CardInset;
    private const double AnchorGap = 10;
    private const double ScreenMargin = 12;
    private const double DismissSeconds = 15;
    private const double MinimumResumeSeconds = 2;
    private const double FadeOutSeconds = 0.2;

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

    private readonly DictionarySuggestion _suggestion;
    private readonly RectInt32? _anchor;
    private readonly Action _onAdd;
    private readonly Action _onIgnore;
    private readonly Action _onDismiss;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly IntPtr _hwnd;
    private readonly bool _reduceMotion;
    private double _remainingSeconds = DismissSeconds;
    private int _hoverCount;
    private bool _closing;
    private double _scale = 1;

    public DictionarySuggestionPromptWindow(
        DictionarySuggestion suggestion,
        RectInt32? anchor,
        Action onAdd,
        Action onIgnore,
        Action onDismiss)
    {
        InitializeComponent();
        _suggestion = suggestion;
        _anchor = anchor;
        _onAdd = onAdd;
        _onIgnore = onIgnore;
        _onDismiss = onDismiss;
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

        var style = GetWindowLongPtr(_hwnd, GwlExStyle);
        SetWindowLongPtr(_hwnd, GwlExStyle, (style | WsExToolWindow | WsExNoActivate) & ~WsExAppWindow);
        var none = DwmColorNone;
        DwmSetWindowAttribute(_hwnd, DwmwaBorderColor, ref none, sizeof(int));
        var doNotRound = DwmwcpDoNotRound;
        DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref doNotRound, sizeof(int));

        var detail = Detail(suggestion);
        DetailText.Text = MiddleTruncated(detail, DetailText.Width);
        ToolTipService.SetToolTip(DetailText, detail);
        ApplyAccessibility(detail);
        ApplyHighContrastIfNeeded();

        AddButton.Click += (_, _) => Answer(_onAdd);
        IgnoreButton.Click += (_, _) => Answer(_onIgnore);
        DismissButton.Click += (_, _) => Close(fade: true, notify: true);
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            args.Handled = true;
            Close(fade: true, notify: true);
        };
        Root.KeyboardAccelerators.Add(escape);
        Root.PointerEntered += OnHoverEnter;
        Root.PointerExited += OnHoverExit;
        _timer.Tick += OnTick;
    }

    /// <summary>The correction as macOS writes it: <c>"observed" -&gt; "replacement"</c>.</summary>
    public static string Detail(DictionarySuggestion suggestion) => $"\"{suggestion.Observed}\" -> \"{suggestion.Replacement}\"";

    /// <summary>Shows the card beside the pill without activating it.</summary>
    public void ShowPrompt()
    {
        PositionAndSize();
        ApplyRoundedRegion();
        AppWindow.Show(false);
        _timer.Start();
    }

    /// <summary>Closes without reporting an outcome (another prompt or a settings change replaced it).</summary>
    public void CloseQuietly() => Close(fade: false, notify: false);

    private void Answer(Action answer)
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        Close();
        answer();
    }

    private void Close(bool fade, bool notify, bool timed = false)
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        void Finish()
        {
            Close();
            if (notify) _onDismiss();
        }

        if (!fade || _reduceMotion)
        {
            Finish();
            return;
        }

        var animation = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = new Duration(TimeSpan.FromSeconds(FadeOutSeconds)),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(animation, Root);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            // macOS restores the card if the pointer arrived during the fade-out of a timed close;
            // the countdown then resumes from two seconds when the pointer leaves.
            if (timed && _hoverCount > 0)
            {
                storyboard.Stop();
                Root.Opacity = 1;
                _closing = false;
                return;
            }
            Finish();
        };
        storyboard.Begin();
    }

    private void OnHoverEnter(object sender, PointerRoutedEventArgs e)
    {
        if (++_hoverCount == 1)
        {
            _remainingSeconds = Math.Max(MinimumResumeSeconds, _remainingSeconds);
            _timer.Stop();
        }
    }

    private void OnHoverExit(object sender, PointerRoutedEventArgs e)
    {
        if (_hoverCount == 0) return;
        if (--_hoverCount == 0 && !_closing) _timer.Start();
    }

    private void OnTick(object? sender, object e)
    {
        _remainingSeconds = Math.Max(0, _remainingSeconds - _timer.Interval.TotalSeconds);
        CountdownScale.ScaleX = _remainingSeconds / DismissSeconds;
        if (_remainingSeconds <= 0) Close(fade: true, notify: true, timed: true);
    }

    /// <summary>
    /// Long corrections lose their middle, as macOS's <c>byTruncatingMiddle</c>; the tooltip keeps
    /// the full text.
    /// </summary>
    private string MiddleTruncated(string text, double width)
    {
        if (Measure(text) <= width) return text;
        var keep = text.Length - 1;
        while (keep > 2)
        {
            var head = (keep + 1) / 2;
            var candidate = text[..head] + "…" + text[^(keep - head)..];
            if (Measure(candidate) <= width) return candidate;
            keep--;
        }
        return "…";
    }

    private double Measure(string text)
    {
        var block = new TextBlock { Text = text, FontSize = DetailText.FontSize, FontFamily = DetailText.FontFamily };
        block.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return block.DesiredSize.Width;
    }

    private void ApplyAccessibility(string detail)
    {
        AutomationProperties.SetAutomationId(Root, "DictionarySuggestionPrompt");
        AutomationProperties.SetName(Root, $"Add correction? {detail}");
        AutomationProperties.SetAutomationId(AddButton, "DictionarySuggestionPromptAdd");
        AutomationProperties.SetName(AddButton, "Add");
        AutomationProperties.SetAutomationId(IgnoreButton, "DictionarySuggestionPromptIgnore");
        AutomationProperties.SetName(IgnoreButton, "Ignore");
        AutomationProperties.SetAutomationId(DismissButton, "DictionarySuggestionPromptDismiss");
        AutomationProperties.SetName(DismissButton, "Dismiss");
        ToolTipService.SetToolTip(DismissButton, "Dismiss");
        AutomationProperties.SetName(CountdownTrack, "Time before this suggestion closes");
    }

    private void ApplyHighContrastIfNeeded()
    {
        if (!new AccessibilitySettings().HighContrast) return;
        var window = (Brush)Application.Current.Resources["SystemColorWindowColorBrush"];
        var text = (Brush)Application.Current.Resources["SystemColorWindowTextColorBrush"];
        var highlight = (Brush)Application.Current.Resources["SystemColorHighlightColorBrush"];
        var highlightText = (Brush)Application.Current.Resources["SystemColorHighlightTextColorBrush"];
        Card.Background = window;
        Card.BorderBrush = text;
        TitleText.Foreground = text;
        DetailText.Foreground = text;
        BookIcon.Foreground = text;
        CountdownBar.Background = (Brush)Application.Current.Resources["SystemColorHotlightColorBrush"];
        AddButton.Background = highlight;
        AddButton.Foreground = highlightText;
        IgnoreButton.Background = window;
        IgnoreButton.Foreground = text;
        IgnoreButton.BorderBrush = text;
        IgnoreButton.BorderThickness = new Thickness(1);
        DismissVisual.Background = window;
        DismissVisual.BorderBrush = text;
        DismissGlyph.Foreground = text;
    }

    /// <summary>
    /// Centred on the pill, 10 points above it, or below it when there is no room above, kept 12
    /// points inside the work area (macOS <c>frame(for:anchorFrame:)</c>). With no pill on screen the
    /// card sits near the bottom centre of the monitor under the pointer.
    /// </summary>
    private void PositionAndSize()
    {
        var monitor = _anchor is { } anchor
            ? MonitorFromPoint(new NativePoint { X = anchor.X + anchor.Width / 2, Y = anchor.Y + anchor.Height / 2 }, MonitorDefaultToNearest)
            : GetCursorPos(out var cursor) ? MonitorFromPoint(cursor, MonitorDefaultToNearest) : MonitorFromWindow(IntPtr.Zero, MonitorDefaultToPrimary);
        _scale = MonitorScale(monitor);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(monitor, ref info);
        var work = info.WorkArea;

        var width = (int)Math.Round(PanelWidth * _scale);
        var height = (int)Math.Round(PanelHeight * _scale);
        var gap = (int)Math.Round(AnchorGap * _scale);
        var margin = (int)Math.Round(ScreenMargin * _scale);
        var frame = _anchor ?? new RectInt32(
            (work.Left + work.Right) / 2 - (int)Math.Round(22 * _scale),
            work.Bottom - (int)Math.Round((24 + 28) * _scale),
            (int)Math.Round(44 * _scale),
            (int)Math.Round(28 * _scale));

        var x = frame.X + frame.Width / 2 - width / 2;
        var y = frame.Y - gap - height;
        if (y < work.Top) y = frame.Y + frame.Height + gap;
        x = Math.Clamp(x, work.Left + margin, Math.Max(work.Left + margin, work.Right - width - margin));
        y = Math.Clamp(y, work.Top + margin, Math.Max(work.Top + margin, work.Bottom - height - margin));
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    /// <summary>The window is the rounded card plus the round close button; the rest shows the desktop.</summary>
    private void ApplyRoundedRegion()
    {
        try
        {
            var left = (int)Math.Round(CardInset * _scale);
            var right = (int)Math.Round(PanelWidth * _scale);
            var bottom = (int)Math.Round(PanelHeight * _scale);
            var diameter = (int)Math.Round(20 * _scale);
            var card = CreateRoundRectRgn(left, left, right + 1, bottom + 1, diameter, diameter);
            var centre = (int)Math.Round(CardInset * _scale);
            var radius = (int)Math.Round(CloseSize / 2 * _scale);
            var close = CreateEllipticRgn(centre - radius, centre - radius - (int)Math.Round(_scale), centre + radius + 1, centre + radius);
            var combined = CreateRectRgn(0, 0, 0, 0);
            CombineRgn(combined, card, close, RgnOr);
            DeleteObject(card);
            DeleteObject(close);
            if (SetWindowRgn(_hwnd, combined, true) == 0) DeleteObject(combined);
        }
        catch
        {
            // A shaped-window failure must not stop the prompt from showing.
        }
    }

    private double MonitorScale(IntPtr monitor) =>
        GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpi, out _) == 0 && dpi > 0
            ? Math.Max(1d, dpi / 96d)
            : Math.Max(1d, GetDpiForWindow(_hwnd) / 96d);

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

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern long GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] private static extern long SetWindowLongPtr(IntPtr hWnd, int nIndex, long value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr dest, IntPtr source1, IntPtr source2, int mode);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
}
