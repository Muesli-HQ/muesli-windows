using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Core.Profiles;
using Muesli.Windows.Core.Services;
using Muesli.Windows.Services;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.ViewManagement;

namespace Muesli.Windows.WinUI;

public sealed partial class DictationIndicatorWindow : Window, IDisposable
{
    // A click left of this many DIPs inside the recording/preparing pill cancels; the rest stops.
    private const double CollapseDelayMs = 140;

    /// <summary>Waveform bars and the stop square are White 85% per the macOS design system.</summary>
    private static readonly byte WaveformAlphaByte = (byte)(255 * FloatingIndicatorLayout.WaveformOpacity);

    private const int GwlExStyle = -20;
    private const long WsExAppWindow = 0x00040000;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;

    private readonly Services.WinUiDictationContext _dictation;
    private readonly IUiDispatcher _dispatcher;
    private readonly IntPtr _hwnd;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _returnTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _collapseTimer;
    private readonly List<Border> _recordingBars = [];

    private FloatingIndicatorState _state = FloatingIndicatorState.Idle;
    private bool _visible;
    private double _widthDip = FloatingIndicatorLayout.CompactIdleWidth;
    private double _heightDip = FloatingIndicatorLayout.CompactIdleHeight;
    private double _cornerRadiusDip = FloatingIndicatorLayout.CompactIdleRadius;

    private bool _hovered;
    private bool _pointerInside;
    private bool _suppressIdleHoverUntilMouseLeaves;

    private bool _dragging;
    private bool _dragMoved;
    private DipPoint _dragStartLocal;
    private DipPoint _dragWindowDip;

    private string _indicatorAnchor = "Middle Right";
    private double? _savedLeft;
    private double? _savedTop;

    // Presentation probe: renders the hardware-driven states for UI Automation. Inert unless
    // --indicator-probe was passed. See FloatingIndicatorProbe for why this is a command-line flag
    // plus a profile file rather than an environment variable.
    private readonly FileSystemWatcher? _probeWatcher;
    private FloatingIndicatorState? _probeState;

    // The status text of the outcome pill that has already had its dwell on screen, and the one
    // currently counting down. Dictation Status is sticky, so without these an expired pill would
    // re-classify to the same outcome and never return to idle.
    private string? _dismissedOutcomeStatus;
    private string? _pendingOutcomeStatus;

    private bool _lastShowSetting = true;
    private string _lastAnchor = "";
    private string _lastAutomationStatus = "\u0001";
    private int _disposed;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointL position);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern long GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern long SetWindowLongPtr(IntPtr hWnd, int nIndex, long value);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint key, byte alpha, uint flags);

    private const long WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x00000002;

    // Windows 11 paints a light 1-px border and rounds the corners of every top-level window.
    // On a 44x28 pill that border reads as bright bands along the straight edges, outside the
    // rounded region — the "random lines" around the pill. The reference has no such chrome.
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpDoNotRound = 1;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL
    {
        public int X;
        public int Y;
    }

    public DictationIndicatorWindow(
        Services.WinUiDictationContext dictation,
        IUiDispatcher dispatcher)
    {
        InitializeComponent();
        _dictation = dictation;
        _dispatcher = dispatcher;
        Title = "Muesli dictation indicator";
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _returnTimer = DispatcherQueue.CreateTimer();
        _returnTimer.Tick += ReturnTimer_Tick;
        _collapseTimer = DispatcherQueue.CreateTimer();
        _collapseTimer.Interval = TimeSpan.FromMilliseconds(CollapseDelayMs);
        _collapseTimer.Tick += CollapseTimer_Tick;

        ResizeDip(_widthDip, _heightDip);
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
        }

        // This floating pill must never appear in the taskbar or in Alt+Tab / window switchers.
        HideFromTaskbarAndSwitchers();
        SuppressSystemWindowChrome();

        var initial = App.Settings.Load();
        _lastShowSetting = initial.ShowFloatingIndicator;
        _lastAnchor = initial.IndicatorAnchor ?? "";
        _indicatorAnchor = string.IsNullOrWhiteSpace(initial.IndicatorAnchor) ? "Middle Right" : initial.IndicatorAnchor;
        _savedLeft = initial.IndicatorLeft;
        _savedTop = initial.IndicatorTop;

        _probeWatcher = TryStartPresentationProbe();

        _dictation.Changed += Dictation_Changed;
        _dictation.RecordingLevelChanged += Dictation_RecordingLevelChanged;
        App.Settings.Changed += Settings_Changed;
        RootBorder.ActualThemeChanged += (_, _) => _dispatcher.TryEnqueue(UpdateState);
        RootBorder.Loaded += (_, _) =>
        {
            RootBorder.IsTabStop = true;
            AutomationProperties.SetAutomationId(RootBorder, "FloatingDictationIndicator");
            UpdateState();
        };
        _dispatcher.TryEnqueue(UpdateState);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _returnTimer.Stop();
        _collapseTimer.Stop();
        if (_probeWatcher is not null)
        {
            _probeWatcher.EnableRaisingEvents = false;
            _probeWatcher.Dispose();
        }
        _dictation.Changed -= Dictation_Changed;
        _dictation.RecordingLevelChanged -= Dictation_RecordingLevelChanged;
        App.Settings.Changed -= Settings_Changed;
        Close();
    }

    private void Dictation_Changed(object? sender, EventArgs e) => _dispatcher.TryEnqueue(UpdateState);

    private void Dictation_RecordingLevelChanged(object? sender, float peak) =>
        _dispatcher.TryEnqueue(() => UpdateRecordingBars(peak));

    private void Settings_Changed(object? sender, MuesliSettings settings) => _dispatcher.TryEnqueue(() =>
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _lastShowSetting = settings.ShowFloatingIndicator;

        var anchor = settings.IndicatorAnchor ?? "";
        var anchorChanged = !string.Equals(anchor, _lastAnchor, StringComparison.Ordinal);
        _lastAnchor = anchor;
        _indicatorAnchor = string.IsNullOrWhiteSpace(anchor) ? "Middle Right" : anchor;

        UpdateState();

        // UpdateState repositions on the hidden → shown transition; an anchor change while the
        // pill is already on screen otherwise leaves it in place until the next state change.
        if (anchorChanged && _visible) RepositionPill();
    });

    private void UpdateState()
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        var settings = App.Settings.Load();
        var status = _dictation.Status ?? "";
        var state = FloatingIndicatorStateClassifier.Classify(status, _dictation.IsRecording, _dictation.IsTranscribing);

        // A probe-driven state renders through exactly this path; only the trigger is synthetic.
        if (_probeState is { } probed)
        {
            state = probed;
            status = FloatingIndicatorProbe.StatusFor(probed);
        }

        // An outcome that already had its dwell must not be re-shown by the sticky status text.
        if (FloatingIndicatorStateClassifier.IsAlreadyDismissed(state, status, _dismissedOutcomeStatus))
        {
            state = FloatingIndicatorState.Idle;
        }

        _state = state;

        var hovered = state == FloatingIndicatorState.Idle && _hovered;
        var message = ComposeMessage(state, status, settings);
        var size = FloatingIndicatorLayout.SizeFor(state, hovered, message);
        _widthDip = size.Width;
        _heightDip = size.Height;
        _cornerRadiusDip = size.CornerRadius;

        var shouldShow = state != FloatingIndicatorState.Idle || settings.ShowFloatingIndicator;
        if (!shouldShow && _visible)
        {
            _returnTimer.Stop();
            AppWindow.Hide();
            _visible = false;
            return;
        }

        var highContrast = new AccessibilitySettings().HighContrast;
        var recordingAccent = ParseAccent(settings.RecordingColorHex);

        // The reference pill is dark glass (#1E1E2E) in every app theme, so pin this window to
        // Dark. Left to inherit, the acrylic backdrop resolves Light and the translucent fill
        // composites over a white surface — the pill then renders as a pale box instead of glass.
        // High Contrast keeps the system pairing.
        RootBorder.RequestedTheme = highContrast ? ElementTheme.Default : ElementTheme.Dark;

        RootBorder.CornerRadius = new CornerRadius(_cornerRadiusDip);
        RootBorder.BorderThickness = new Thickness(1);
        RootBorder.Padding = PillPadding(state, hovered);
        RootBorder.Background = GlassBrush(state, hovered, recordingAccent, highContrast);
        RootBorder.BorderBrush = highContrast
            ? ThemeBrush("MuesliTextPrimaryBrush")
            : new SolidColorBrush(Color.FromArgb(
                (byte)(255 * BorderOpacity(state, hovered)), 0xFF, 0xFF, 0xFF));

        BuildContent(state, hovered, message, recordingAccent, highContrast);

        ApplyWindowAlpha(FloatingIndicatorLayout.WindowAlpha(state, hovered));
        ResizeDip(_widthDip, _heightDip);
        if (!_visible)
        {
            RepositionPill();
            AppWindow.Show(false);
            // WinUI re-applies system window chrome when the window is first shown, so the
            // border/corner suppression has to be re-asserted here as well as at construction.
            SuppressSystemWindowChrome();
            _visible = true;
        }
        else
        {
            RepositionPill();
        }

        SetAutomation(state, status);
        UpdateReturnTimer(state, status);
    }

    private string ComposeMessage(FloatingIndicatorState state, string status, MuesliSettings settings)
    {
        switch (state)
        {
            case FloatingIndicatorState.Transcribing:
                return "Transcribing";
            case FloatingIndicatorState.Success:
            case FloatingIndicatorState.Error:
                return string.IsNullOrWhiteSpace(status) ? "" : status;
            case FloatingIndicatorState.Idle:
                var hotkey = string.IsNullOrWhiteSpace(settings.Hotkey) ? "shortcut" : settings.Hotkey;
                return settings.EnableDoubleTapDictation
                    ? $"Hold {hotkey} to dictate, or double-tap for hands-free"
                    : $"Hold {hotkey} to dictate";
            case FloatingIndicatorState.Preparing:
            case FloatingIndicatorState.Recording:
                return "";
            default:
                return "";
        }
    }

    private void BuildContent(FloatingIndicatorState state, bool hovered, string? message, Color recordingAccent, bool highContrast)
    {
        PillContent.Children.Clear();
        _recordingBars.Clear();
        var contentBrush = ContentBrush(highContrast);

        switch (state)
        {
            case FloatingIndicatorState.Idle:
                BuildIdle(hovered, message ?? "", contentBrush, highContrast);
                break;
            case FloatingIndicatorState.Preparing:
                BuildPreparing(contentBrush);
                break;
            case FloatingIndicatorState.Recording:
                BuildRecording(recordingAccent, contentBrush);
                break;
            case FloatingIndicatorState.Transcribing:
                BuildTranscribing(contentBrush);
                break;
            case FloatingIndicatorState.Success:
                BuildStatus(message, isError: false, contentBrush);
                break;
            case FloatingIndicatorState.Error:
                BuildStatus(message, isError: true, contentBrush);
                break;
            default:
                BuildIdle(false, "", contentBrush, highContrast);
                break;
        }
    }

    private void BuildIdle(bool hovered, string message, Brush contentBrush, bool highContrast)
    {
        if (!hovered)
        {
            var glyph = MuesliGlyph(18, new Thickness(0), contentBrush);
            PillContent.Children.Add(glyph);
            return;
        }

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 7
        };
        AutomationProperties.SetName(stack, "Muesli dictation, idle");
        AutomationProperties.SetLiveSetting(stack, AutomationLiveSetting.Polite);
        stack.Children.Add(MuesliGlyph(15, new Thickness(0), contentBrush));
        // The reference draws this hint at 0.75 alpha, dimmer than the glyph beside it.
        stack.Children.Add(Label(
            message, 11, FontWeights.SemiBold, new Thickness(0),
            highContrast ? contentBrush : new SolidColorBrush(Color.FromArgb(191, 0xFF, 0xFF, 0xFF))));
        PillContent.Children.Add(stack);
    }

    private void BuildPreparing(Brush contentBrush)
    {
        var wave = WaveformPanel(contentBrush, PreparingBarHeights());
        foreach (var bar in wave)
        {
            SetBarChrome(bar, contentBrush, WaveformAlphaByte);
            _recordingBars.Add(bar);
        }
        PillContent.Children.Add(WaveformHost(wave));
    }

    private void BuildRecording(Color accent, Brush contentBrush)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });

        // Symmetric 16/…/16 columns keep the waveform centred on the pill; the heavy cross mark at
        // 7pt/45% white matches the macOS recording control.
        var cancel = TransparentButton(
            "Cancel dictation",
            "FloatingDictationCancel",
            FloatingIndicatorLayout.RecordingCancelGlyph,
            FloatingIndicatorLayout.RecordingCancelFontSize);
        if (cancel.Content is FontIcon cancelIcon)
        {
            cancelIcon.Foreground = new SolidColorBrush(Color.FromArgb(
                FloatingIndicatorLayout.RecordingCancelAlpha, 0xFF, 0xFF, 0xFF));
        }
        cancel.Click += (_, _) => _ = _dictation.CancelAsync();
        Grid.SetColumn(cancel, 0);
        grid.Children.Add(cancel);

        var wave = WaveformPanel(contentBrush, RecordingBarHeights());
        for (var index = 0; index < wave.Count; index++)
        {
            SetBarChrome(wave[index], contentBrush, WaveformAlphaByte);
            wave[index].Height = FloatingIndicatorLayout.RecordingBarHeights[index];
            _recordingBars.Add(wave[index]);
        }
        var waveHost = WaveformHost(wave);
        Grid.SetColumn(waveHost, 1);
        grid.Children.Add(waveHost);

        var stop = TransparentButton("Stop and transcribe", "FloatingDictationStop", glyph: null, fontSize: 0);
        stop.Click += (_, _) => _ = _dictation.StopRecordingAsync();
        var square = new Border
        {
            Width = FloatingIndicatorLayout.StopSquareSize,
            Height = FloatingIndicatorLayout.StopSquareSize,
            CornerRadius = new CornerRadius(FloatingIndicatorLayout.StopSquareRadius),
            Background = new SolidColorBrush(Color.FromArgb(WaveformAlphaByte, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, FloatingIndicatorLayout.StopSquareRightMargin, 0)
        };
        stop.Content = square;
        Grid.SetColumn(stop, 2);
        grid.Children.Add(stop);

        PillContent.Children.Add(grid);
    }

    private void BuildTranscribing(Brush contentBrush)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 5
        };
        stack.Children.Add(new FontIcon
        {
            Glyph = "\uE895",
            FontSize = 12,
            Foreground = contentBrush,
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(Label("Transcribing", 11, FontWeights.SemiBold, new Thickness(0), contentBrush));

        var cancel = TransparentButton("Cancel dictation", "FloatingDictationCancel", "\uE711", 10);
        cancel.Click += (_, _) => _ = _dictation.CancelAsync();
        cancel.Margin = new Thickness(6, 0, 0, 0);
        stack.Children.Add(cancel);

        PillContent.Children.Add(stack);
    }

    private void BuildStatus(string? message, bool isError, Brush contentBrush)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(99),
            Background = new SolidColorBrush(isError
                ? Color.FromArgb(0xFF, 0xF8, 0x71, 0x71)
                : Color.FromArgb(0xFF, 0x34, 0xD3, 0x99)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var label = Label(string.IsNullOrWhiteSpace(message) ? (isError ? "Error" : "Done") : message, 11,
            FontWeights.SemiBold, new Thickness(8, 0, 0, 0), contentBrush);
        label.HorizontalAlignment = HorizontalAlignment.Left;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetAutomationId(label, "FloatingDictationStatus");
        AutomationProperties.SetName(label, string.IsNullOrWhiteSpace(message) ? (isError ? "Error" : "Done") : message);
        AutomationProperties.SetLiveSetting(label, AutomationLiveSetting.Assertive);
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        PillContent.Children.Add(grid);
    }

    private static List<Border> WaveformPanel(Brush brush, IReadOnlyList<double> heights)
    {
        var bars = new List<Border>(heights.Count);
        foreach (var height in heights)
        {
            // Design-system waveform geometry: 3pt bars, 4pt apart.
            bars.Add(new Border
            {
                Width = FloatingIndicatorLayout.WaveformBarWidth,
                Height = height,
                Margin = new Thickness(FloatingIndicatorLayout.WaveformBarSpacing / 2, 0,
                                       FloatingIndicatorLayout.WaveformBarSpacing / 2, 0),
                CornerRadius = new CornerRadius(FloatingIndicatorLayout.WaveformBarWidth / 2),
                Background = brush,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        return bars;
    }

    private static StackPanel WaveformHost(IReadOnlyList<Border> bars)
    {
        var wave = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (var bar in bars) wave.Children.Add(bar);
        return wave;
    }

    private static void SetBarChrome(Border bar, Brush brush, byte alpha)
    {
        var solid = brush as SolidColorBrush;
        var baseColor = solid?.Color ?? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        bar.Background = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
    }

    private static Button TransparentButton(string name, string automationId, string? glyph, double fontSize)
    {
        var button = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            UseSystemFocusVisuals = true
        };
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetAutomationId(button, automationId);
        if (glyph is not null)
        {
            button.Content = new FontIcon { Glyph = glyph, FontSize = fontSize };
        }
        return button;
    }

    private static TextBlock Label(string text, double fontSize, FontWeight weight, Thickness margin, Brush brush)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = weight,
            FontFamily = (FontFamily)Application.Current.Resources["MuesliFontFamilySemiBold"],
            Margin = margin,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
    }

    /// <summary>
    /// The MWaveformIcon brand mark: nine rounded bars forming a symmetric "M".
    /// </summary>
    /// <remarks>
    /// The current design system states the dictation states render "animated MWaveformIcon bars
    /// ... not emoji", and the macOS controller draws the idle glyph from the menu-bar icon
    /// (<c>MenuBarIconRenderer</c>), which is this mark. It is drawn code-natively from the
    /// design system's documented small-context preset rather than scaled from the 44x44
    /// menu-bar PNG, so it stays crisp at 18 DIP and at any display scale.
    /// </remarks>
    private static readonly double[] BrandMarkBars = [0.45, 0.65, 0.90, 1.0, 0.45, 1.0, 0.90, 0.65, 0.45];

    private static FrameworkElement MuesliGlyph(double size, Thickness margin, Brush brush)
    {
        var barWidth = size / 13;
        var spacing = size / 30;

        var bars = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = margin,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        foreach (var multiplier in BrandMarkBars)
        {
            bars.Children.Add(new Border
            {
                Width = barWidth,
                Height = Math.Max(barWidth, multiplier * size),
                Margin = new Thickness(spacing / 2, 0, spacing / 2, 0),
                CornerRadius = new CornerRadius(barWidth / 2),
                Background = brush,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        AutomationProperties.SetAccessibilityView(bars, AccessibilityView.Raw);
        return bars;
    }

    private void UpdateRecordingBars(float peak)
    {
        if (_state != FloatingIndicatorState.Recording || _recordingBars.Count == 0) return;
        for (var index = 0; index < _recordingBars.Count; index++)
        {
            _recordingBars[index].Height = FloatingIndicatorLayout.RecordingBarHeight(peak, index);
        }
    }

    private void SetAutomation(FloatingIndicatorState state, string status)
    {
        var stateLabel = state switch
        {
            FloatingIndicatorState.Idle => "idle",
            FloatingIndicatorState.Preparing => "preparing",
            FloatingIndicatorState.Recording => "recording",
            FloatingIndicatorState.Transcribing => "transcribing",
            FloatingIndicatorState.Success => "success",
            FloatingIndicatorState.Error => "error",
            _ => "unknown"
        };
        var message = IsContentStatus(state)
            ? (string.IsNullOrWhiteSpace(status) ? stateLabel : status)
            : string.Empty;
        var exposed = string.IsNullOrWhiteSpace(message) ? $"Muesli dictation, {stateLabel}" : message;
        AutomationProperties.SetName(RootBorder, $"Muesli dictation indicator, {stateLabel}");

        if (string.Equals(_lastAutomationStatus, exposed, StringComparison.Ordinal)) return;
        _lastAutomationStatus = exposed;
        AutomationProperties.SetLiveSetting(RootBorder, AutomationLiveSetting.Assertive);
    }

    private bool IsContentStatus(FloatingIndicatorState state) =>
        state is FloatingIndicatorState.Success or FloatingIndicatorState.Error or FloatingIndicatorState.Transcribing;

    private void UpdateReturnTimer(FloatingIndicatorState state, string status)
    {
        _returnTimer.Stop();
        if (FloatingIndicatorStateClassifier.IsTransientOutcome(state))
        {
            _pendingOutcomeStatus = status;
            _returnTimer.Interval = TimeSpan.FromMilliseconds(FloatingIndicatorLayout.ReturnMilliseconds(state));
            _returnTimer.Start();
        }
        else
        {
            // A live state supersedes any dismissal, so the next outcome always gets its dwell —
            // even when it repeats the message that was dismissed before it.
            if (FloatingIndicatorStateClassifier.IsActive(state)) _dismissedOutcomeStatus = null;
            _pendingOutcomeStatus = null;
            _suppressIdleHoverUntilMouseLeaves = true;
        }
    }

    private void ReturnTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (Volatile.Read(ref _disposed) != 0) return;
        // The pill has had its dwell: remember the status so the sticky text cannot re-enter the
        // same outcome, and consume any probe state so the real return-to-idle path runs.
        _dismissedOutcomeStatus = _pendingOutcomeStatus;
        _pendingOutcomeStatus = null;
        _probeState = null;
        _dispatcher.TryEnqueue(UpdateState);
    }

    /// <summary>
    /// Arms the presentation probe when <c>--indicator-probe</c> was passed. The probe watches a
    /// single file in the active profile for a state name; it drives presentation only and can
    /// never start, stop, or alter a real dictation. Returns <c>null</c> when not armed.
    /// </summary>
    private FileSystemWatcher? TryStartPresentationProbe()
    {
        if (!FloatingIndicatorProbe.IsEnabledForCurrentProcess()) return null;

        try
        {
            var root = MuesliProfilePaths.Current().RootDirectory;
            Directory.CreateDirectory(root);
            var statePath = Path.Combine(root, FloatingIndicatorProbe.StateFileName);
            ApplyProbeFile(statePath);

            var watcher = new FileSystemWatcher(root, FloatingIndicatorProbe.StateFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName
            };
            watcher.Changed += (_, _) => _dispatcher.TryEnqueue(() => ApplyProbeFile(statePath));
            watcher.Created += (_, _) => _dispatcher.TryEnqueue(() => ApplyProbeFile(statePath));
            watcher.Deleted += (_, _) => _dispatcher.TryEnqueue(() =>
            {
                _probeState = null;
                UpdateState();
            });
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception)
        {
            // The probe is test scaffolding; never let it take the indicator down.
            return null;
        }
    }

    private void ApplyProbeFile(string statePath)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            if (!File.Exists(statePath)) return;
            using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var parsed = FloatingIndicatorProbe.ParseState(reader.ReadToEnd());
            if (parsed is null) return;
            _probeState = parsed;
            UpdateState();
        }
        catch (IOException)
        {
            // A half-written file simply leaves the current state alone until the next write.
        }
    }

    private void CollapseTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (Volatile.Read(ref _disposed) != 0) return;
        if (!_visible || _state != FloatingIndicatorState.Idle || _pointerInside) return;
        if (!_hovered) return;
        _hovered = false;
        _dispatcher.TryEnqueue(UpdateState);
    }

    private void RootBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerInside = true;
        if (_state != FloatingIndicatorState.Idle || !_visible || _suppressIdleHoverUntilMouseLeaves) return;
        if (_hovered) return;
        _hovered = true;
        UpdateState();
    }

    private void RootBorder_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerInside = false;
        if (_state != FloatingIndicatorState.Idle) return;
        _suppressIdleHoverUntilMouseLeaves = false;
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void CancelAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Cancel();
    }

    private void RootBorder_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        Cancel();
    }

    private void Cancel()
    {
        if (_state is FloatingIndicatorState.Preparing
            or FloatingIndicatorState.Recording
            or FloatingIndicatorState.Transcribing)
        {
            _ = _dictation.CancelAsync();
        }
    }

    private void RootBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(null).Properties.IsRightButtonPressed)
        {
            Cancel();
            return;
        }

        if (IsInteractive(e.OriginalSource)) return;
        _dragging = RootBorder.CapturePointer(e.Pointer);
        if (!_dragging) return;
        _dragMoved = false;
        _dragStartLocal = e.GetCurrentPoint(null).Position.ToDipPoint();
        _dragWindowDip = DipWindowPosition();
    }

    private void RootBorder_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        var local = e.GetCurrentPoint(null).Position.ToDipPoint();
        var deltaX = local.X - _dragStartLocal.X;
        var deltaY = local.Y - _dragStartLocal.Y;
        if (!_dragMoved && Math.Sqrt(deltaX * deltaX + deltaY * deltaY) < FloatingIndicatorLayout.DragThreshold)
        {
            return;
        }

        _dragMoved = true;
        var size = new DipSize(_widthDip, _heightDip, _cornerRadiusDip);
        var requested = new DipPoint(_dragWindowDip.X + deltaX, _dragWindowDip.Y + deltaY);
        var workArea = CursorWorkAreaDip();
        var clamped = FloatingIndicatorLayout.Clamp(requested, size, workArea);
        MoveToDip(clamped);
    }

    private void RootBorder_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        RootBorder.ReleasePointerCapture(e.Pointer);

        if (_dragMoved)
        {
            _savedLeft = DipWindowPosition().X + _widthDip / 2;
            _savedTop = DipWindowPosition().Y + _heightDip / 2;
            _indicatorAnchor = "Custom";
            SavePosition();
            return;
        }

        var x = e.GetCurrentPoint(null).Position.X;
        switch (FloatingIndicatorLayout.ActionForClick(_state, x))
        {
            case FloatingIndicatorAction.Cancel:
                _ = _dictation.CancelAsync();
                break;
            case FloatingIndicatorAction.Stop:
                _ = _dictation.StopRecordingAsync();
                break;
        }
    }

    private void SavePosition()
    {
        try
        {
            var settings = App.Settings.Load();
            App.Settings.Save(settings with
            {
                IndicatorAnchor = _indicatorAnchor,
                IndicatorLeft = _savedLeft,
                IndicatorTop = _savedTop
            });
        }
        catch (Exception)
        {
            // A settings save that fails (for example under a newer schema) must not break a drag.
        }
    }

    private static bool IsInteractive(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is Button) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void ResizeDip(double widthDip, double heightDip)
    {
        var scale = DpiScale();
        var width = FloatingIndicatorLayout.ToPixels(widthDip, scale);
        var height = FloatingIndicatorLayout.ToPixels(heightDip, scale);
        AppWindow.Resize(new SizeInt32(width, height));
        ApplyRoundedRegion(width, height, scale);
    }

    /// <summary>
    /// Clips the window to the pill's rounded rectangle.
    /// </summary>
    /// <remarks>
    /// The window carries a <c>DesktopAcrylicBackdrop</c> so the pill reads as translucent glass,
    /// but a backdrop paints the whole rectangular window — which shows as a square halo around
    /// the rounded corners. Clipping the window region to the same radius the Border draws means
    /// nothing can render outside the pill, at any state size or DPI.
    /// </remarks>
    private void ApplyRoundedRegion(int widthPixels, int heightPixels, double scale)
    {
        if (widthPixels <= 0 || heightPixels <= 0) return;

        // CreateRoundRectRgn takes ellipse width/height, i.e. twice the corner radius, and its
        // bounds are exclusive. The region is inset by one pixel on every side: the capsule's
        // widest points touch the window rectangle, and that is exactly where Windows paints its
        // own window border — the bright bands that appeared along the pill's straight edges.
        // Clipping that outermost pixel away leaves only the pill's own hairline.
        var diameter = FloatingIndicatorLayout.ToPixels(_cornerRadiusDip * 2, scale);
        var region = CreateRoundRectRgn(0, 0, widthPixels + 1, heightPixels + 1, diameter, diameter);
        if (region == IntPtr.Zero) return;

        // SetWindowRgn takes ownership on success; only free the region when it was rejected.
        if (SetWindowRgn(_hwnd, region, true) == 0)
        {
            DeleteObject(region);
        }
    }

    private void RepositionPill()
    {
        var scale = DpiScale();
        var size = new DipSize(_widthDip, _heightDip, _cornerRadiusDip);
        var workArea = WindowWorkAreaDip();

        if (string.Equals(_indicatorAnchor, "Custom", StringComparison.OrdinalIgnoreCase) &&
            _savedLeft is { } savedLeft &&
            _savedTop is { } savedTop)
        {
            var savedCenter = new DipPoint(savedLeft, savedTop);
            var repaired = FloatingIndicatorLayout.RepairSavedCenter(savedCenter, size, workArea);
            if (repaired is { } fixedCenter)
            {
                _savedLeft = fixedCenter.X;
                _savedTop = fixedCenter.Y;
                SavePosition();
            }
            var topLeft = new DipPoint(_savedLeft.Value - size.Width / 2, _savedTop.Value - size.Height / 2);
            MoveToDip(FloatingIndicatorLayout.Clamp(topLeft, size, workArea));
            return;
        }

        var cursorArea = CursorWorkAreaDip();
        MoveToDip(FloatingIndicatorLayout.AnchorTopLeft(_indicatorAnchor, size, cursorArea));
    }

    private void MoveToDip(DipPoint point)
    {
        var scale = DpiScale();
        AppWindow.Move(new PointInt32(
            FloatingIndicatorLayout.ToPixels(point.X, scale),
            FloatingIndicatorLayout.ToPixels(point.Y, scale)));
    }

    private DipPoint DipWindowPosition()
    {
        var scale = DpiScale();
        var position = AppWindow.Position;
        return new DipPoint(position.X / scale, position.Y / scale);
    }

    private DipRect WindowWorkAreaDip()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        return ToDipRect(area);
    }

    private DipRect CursorWorkAreaDip()
    {
        GetCursorPos(out var cursor);
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary).WorkArea;
        return ToDipRect(area);
    }

    private DipRect ToDipRect(RectInt32 area)
    {
        var scale = DpiScale();
        return new DipRect(
            FloatingIndicatorLayout.ToDips(area.X, scale),
            FloatingIndicatorLayout.ToDips(area.Y, scale),
            FloatingIndicatorLayout.ToDips(area.Width, scale),
            FloatingIndicatorLayout.ToDips(area.Height, scale));
    }

    private double DpiScale() => FloatingIndicatorLayout.DpiScaleFor(GetDpiForWindow(_hwnd));

    /// <summary>
    /// Makes the pill a non-activating overlay: absent from the taskbar and Alt+Tab, and unable to
    /// take the foreground.
    /// </summary>
    /// <remarks>
    /// <c>WS_EX_NOACTIVATE</c> is what guarantees the requirement that showing the pill, changing
    /// its state, or clicking its cancel/stop controls never steals focus from the application
    /// being dictated into — which is also the window the transcript is pasted into. Without it an
    /// always-on-top overlay can become the foreground window and therefore the paste target.
    /// Escape still cancels, because that arrives through the global keyboard hook rather than
    /// through this window's own focus.
    /// </remarks>
    private void HideFromTaskbarAndSwitchers()
    {
        var style = GetWindowLongPtr(_hwnd, GwlExStyle);
        style = (style | WsExToolWindow | WsExNoActivate) & ~WsExAppWindow;
        SetWindowLongPtr(_hwnd, GwlExStyle, style);
    }

    /// <summary>
    /// Turns off the Windows 11 window border and corner rounding for this window. The pill draws
    /// its own 1-px hairline and its own radius; the system chrome sits outside the clipped region
    /// and shows as bright lines along the pill's straight edges.
    /// </summary>
    private void SuppressSystemWindowChrome()
    {
        var none = DwmColorNone;
        DwmSetWindowAttribute(_hwnd, DwmwaBorderColor, ref none, sizeof(int));

        var doNotRound = DwmwcpDoNotRound;
        DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref doNotRound, sizeof(int));
    }

    /// <summary>
    /// The design system's per-state window opacity (0.85 for the compact idle pill) is not
    /// applied on Windows, and deliberately so.
    /// </summary>
    /// <remarks>
    /// Window-level alpha needs <c>WS_EX_LAYERED</c>, and a layered window does not receive the
    /// DWM acrylic backdrop — so setting it makes the pill see-through rather than frosted, and
    /// text behind it becomes readable. The same state is specified as "frost visible", so the two
    /// requirements cannot both hold here. Frost is the one that makes the pill read as glass, so
    /// the fill's own alpha carries the translucency instead.
    /// </remarks>
    private void ApplyWindowAlpha(double alpha)
    {
        var style = GetWindowLongPtr(_hwnd, GwlExStyle);
        if ((style & WsExLayered) != 0)
        {
            SetWindowLongPtr(_hwnd, GwlExStyle, style & ~WsExLayered);
        }
    }

    private static Brush ThemeBrush(string key) => (Brush)Application.Current.Resources[key];

    private static Brush ContentBrush(bool highContrast) =>
        highContrast
            ? ThemeBrush("MuesliTextPrimaryBrush")
            : new SolidColorBrush(Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF));

    /// <summary>
    /// The pill's 1-px hairline opacity, per state, from the WPF reference's
    /// <c>CreatePill(color, opacity, radius, borderOpacity)</c> calls. It is not one fixed value:
    /// the compact idle pill carries the strongest hairline (0.22) because it is the smallest
    /// target, and the wide pills carry the faintest (0.14).
    /// </summary>
    private static double BorderOpacity(FloatingIndicatorState state, bool hovered) =>
        state switch
        {
            FloatingIndicatorState.Idle => hovered ? 0.14 : 0.22,
            FloatingIndicatorState.Preparing or FloatingIndicatorState.Recording => 0.16,
            FloatingIndicatorState.Transcribing => 0.16,
            FloatingIndicatorState.Success or FloatingIndicatorState.Error => 0.14,
            _ => 0.22
        };

    /// <summary>
    /// Interior padding per state, matching the reference: the compact idle pill has none so the
    /// glyph centres in 44x28, the live pills inset 10, and the wide pills inset 12.
    /// </summary>
    private static Thickness PillPadding(FloatingIndicatorState state, bool hovered) =>
        state switch
        {
            FloatingIndicatorState.Idle => hovered ? new Thickness(12, 0, 12, 0) : new Thickness(0),
            FloatingIndicatorState.Preparing or FloatingIndicatorState.Recording => new Thickness(10, 0, 10, 0),
            _ => new Thickness(12, 0, 12, 0)
        };

    private static SolidColorBrush GlassBrush(FloatingIndicatorState state, bool hovered, Color recordingAccent, bool highContrast)
    {
        if (highContrast)
        {
            return (SolidColorBrush)ThemeBrush("MuesliSurfaceBrush");
        }

        var baseColor = state == FloatingIndicatorState.Recording ? recordingAccent : Color.FromArgb(0xFF, 0x1E, 0x1E, 0x2E);
        var alpha = state switch
        {
            FloatingIndicatorState.Idle => hovered ? 0.72 : 0.44,
            FloatingIndicatorState.Preparing => 0.62,
            FloatingIndicatorState.Recording => 0.85,
            FloatingIndicatorState.Transcribing => 0.62,
            FloatingIndicatorState.Success or FloatingIndicatorState.Error => 0.72,
            _ => 0.44
        };

        // These alphas composite against the window's acrylic backdrop, which is what makes the
        // pill read as glass. Without a backdrop the window paints its opaque theme background
        // instead and the pill washes out to near-white in Light.
        return new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), baseColor.R, baseColor.G, baseColor.B));
    }

    private static Color ParseAccent(string? hex)
    {
        var value = (hex ?? "").Trim().TrimStart('#');
        if (value.Length == 6)
        {
            try
            {
                var packed = Convert.ToUInt32(value, 16);
                return Color.FromArgb(
                    0xFF,
                    (byte)((packed >> 16) & 0xFF),
                    (byte)((packed >> 8) & 0xFF),
                    (byte)(packed & 0xFF));
            }
            catch (FormatException)
            {
            }
            catch (OverflowException)
            {
            }
        }
        return Color.FromArgb(0xFF, 0x1E, 0x1E, 0x2E);
    }

    private static IReadOnlyList<double> PreparingBarHeights() => FloatingIndicatorLayout.PreparingBarHeights;
    private static IReadOnlyList<double> RecordingBarHeights() => FloatingIndicatorLayout.RecordingBarHeights;
}

file static class DipPointExtensions
{
    public static DipPoint ToDipPoint(this Point point) => new(point.X, point.Y);
}
