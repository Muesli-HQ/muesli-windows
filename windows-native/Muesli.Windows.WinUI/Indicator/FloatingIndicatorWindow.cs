using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Muesli.Windows.Core.Services;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using WinRT.Interop;
using Color = Windows.UI.Color;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Muesli.Windows.WinUI.Indicator;

/// <summary>
/// The floating dictation/meeting pill, drawn by the Muesli process itself. It renders one
/// <see cref="IndicatorSnapshot"/> and reports clicks, drags and hovers as
/// <see cref="IndicatorCommand"/>s to its owner; it performs no product work.
/// </summary>
/// <remarks>
/// Replaces the separate self-contained WPF companion (527 files, about 223 MB, a second process
/// fed over a named pipe at 30 fps). Geometry, colours, waveform maths and drag rules come from the
/// shared Core types, so the pill looks and behaves as before. The window is created hidden at
/// startup so it appears instantly, never activates, and stays out of the taskbar and Alt+Tab.
/// </remarks>
internal sealed class FloatingIndicatorWindow : Window
{
    private readonly Action<IndicatorCommand> _sendCommand;
    private readonly Func<float> _liveLevel;
    private readonly IntPtr _hwnd;
    private readonly Grid _root = new();
    private readonly Border _pill = new() { BorderThickness = new Thickness(1) };
    private readonly Grid _content = new();
    private readonly List<Border> _waveBars = [];
    private readonly IndicatorAmplitudeSmoother _smoother = new();
    private readonly FloatingIndicatorDragSession _drag = new();
    private readonly DispatcherQueueTimer _waveTimer;
    private readonly DispatcherQueueTimer _collapseTimer;
    private readonly SubclassProc _subclassProc;
    private Storyboard? _transcribingWiggle;

    private IndicatorSnapshot? _applied;
    private FloatingIndicatorState _state = FloatingIndicatorState.Idle;
    private string _owner = IndicatorOwnerKind.None;
    private bool _visible;
    private bool _shown;
    private bool _hovered;
    private bool _pointerInside;
    private bool _suppressHoverUntilPointerLeaves;
    private bool _closed;
    private double _widthDip = FloatingIndicatorLayout.CompactIdleWidth;
    private double _heightDip = FloatingIndicatorLayout.CompactIdleHeight;
    private double _cornerRadiusDip = FloatingIndicatorLayout.CompactIdleRadius;
    private float _snapshotLevel;
    private DateTime _waveStart = DateTime.UtcNow;
    private bool _highContrast;
    private bool _handsFree;
    private bool _minimalStyle;
    private string _recordingAccentHex = "1e1e2e";
    private string _hotkeyLabel = "shortcut";
    private string _message = "";
    private string _indicatorAnchor = "Middle Right";
    private double? _savedLeft;
    private double? _savedTop;
    private long _sessionId;

    public FloatingIndicatorWindow(Action<IndicatorCommand> sendCommand, Func<float> liveLevel)
    {
        _sendCommand = sendCommand;
        _liveLevel = liveLevel;
        Title = "Muesli floating indicator";
        SystemBackdrop = new TransparentBackdrop();

        _pill.Child = _content;
        _root.Children.Add(_pill);
        Content = _root;
        AutomationProperties.SetAutomationId(_root, "FloatingDictationIndicator");
        AutomationProperties.SetName(_root, "Muesli dictation indicator");

        _hwnd = WindowNative.GetWindowHandle(this);
        ConfigureWindowChrome();
        _subclassProc = WindowSubclass;
        SetWindowSubclass(_hwnd, _subclassProc, 1, IntPtr.Zero);
        InvalidateRect(_hwnd, IntPtr.Zero, true);

        var queue = DispatcherQueue.GetForCurrentThread();
        _waveTimer = queue.CreateTimer();
        _waveTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / FloatingIndicatorLayout.WaveformUpdateFramesPerSecond);
        _waveTimer.Tick += (_, _) => AnimateWaveform();
        _collapseTimer = queue.CreateTimer();
        _collapseTimer.Interval = TimeSpan.FromMilliseconds(140);
        _collapseTimer.IsRepeating = false;
        _collapseTimer.Tick += (_, _) => CollapseHover();

        _pill.PointerEntered += OnPointerEntered;
        _pill.PointerExited += OnPointerExited;
        _pill.PointerPressed += OnPointerPressed;
        _pill.PointerMoved += OnPointerMoved;
        _pill.PointerReleased += OnPointerReleased;
        _pill.PointerCaptureLost += OnPointerCaptureLost;
        _pill.RightTapped += OnRightTapped;
        Closed += (_, _) => OnClosed();
    }

    // ─── Window chrome ───────────────────────────────────────────────────────────────────────

    private void ConfigureWindowChrome()
    {
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
        }

        var exStyle = GetWindowLongPtr(_hwnd, GwlExStyle);
        SetWindowLongPtr(_hwnd, GwlExStyle, (exStyle | WsExToolWindow | WsExNoActivate) & ~WsExAppWindow);

        // A frameless popup: without caption/frame styles DWM draws no border or drop shadow around
        // the transparent rectangle.
        var style = GetWindowLongPtr(_hwnd, GwlStyle);
        SetWindowLongPtr(_hwnd, GwlStyle, (style & ~(WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox)) | WsPopup);
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpFrameChanged | SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate);

        // DWM composites the window's content as opaque unless the frame covers the whole client
        // area; with it, the transparent backdrop and the pill's own alpha reach the desktop.
        var sheet = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(_hwnd, ref sheet);

        // The pill draws its own rounded shape; no system border, corner rounding or show/hide fade.
        var none = DwmColorNone;
        DwmSetWindowAttribute(_hwnd, DwmwaBorderColor, ref none, sizeof(int));
        var doNotRound = DwmwcpDoNotRound;
        DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref doNotRound, sizeof(int));
        var disableTransitions = 1;
        DwmSetWindowAttribute(_hwnd, DwmwaTransitionsForceDisabled, ref disableTransitions, sizeof(int));
    }

    private IntPtr WindowSubclass(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        switch (message)
        {
            case WmGetMinMaxInfo:
                // Overlapped windows default to a ~136 px minimum width; the idle pill is 44 px.
                Marshal.WriteInt32(lParam, MinMaxInfoMinTrackOffset, 1);
                Marshal.WriteInt32(lParam, MinMaxInfoMinTrackOffset + 4, 1);
                return IntPtr.Zero;
            case WmMouseActivate:
                return (IntPtr)MaNoActivate;
            case WmEraseBkgnd:
                // The window's GDI surface sits beneath the XAML content. The class brush paints it
                // white; black is fully transparent under the extended DWM frame, so the corners
                // around the pill show the desktop.
                if (GetClientRect(hwnd, out var client))
                {
                    FillRect(wParam, ref client, GetStockObject(BlackBrush));
                }
                return (IntPtr)1;
            case WmDisplayChange:
            case WmDpiChanged:
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_visible && !_closed) Render();
                });
                break;
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    // ─── Snapshot application ────────────────────────────────────────────────────────────────

    public void Apply(IndicatorSnapshot snapshot)
    {
        if (_closed) return;

        _sessionId = snapshot.SessionId;
        _snapshotLevel = Math.Clamp(snapshot.Amplitude, 0f, 1f);

        // Amplitude-only frames must not rebuild content or reposition (it would reset the bars and
        // fight an in-progress drag).
        if (_applied is { } previous && !IndicatorProtocol.IsStructuralChange(previous, snapshot))
        {
            _applied = snapshot;
            return;
        }

        _applied = snapshot;
        var previousState = _state;
        _state = StateFromName(snapshot.State);
        _owner = snapshot.Owner;
        _visible = snapshot.Visible;
        _handsFree = snapshot.HandsFree;
        _minimalStyle = string.Equals(snapshot.IndicatorStyle, "minimal", StringComparison.OrdinalIgnoreCase);
        _hotkeyLabel = string.IsNullOrWhiteSpace(snapshot.HotkeyLabel) ? "shortcut" : snapshot.HotkeyLabel;
        _recordingAccentHex = string.IsNullOrWhiteSpace(snapshot.RecordingColorHex) ? "1e1e2e" : snapshot.RecordingColorHex;
        _message = snapshot.Message ?? "";
        _highContrast = snapshot.HighContrast;
        _indicatorAnchor = string.IsNullOrWhiteSpace(snapshot.IndicatorAnchor) ? "Middle Right" : snapshot.IndicatorAnchor;
        _savedLeft = snapshot.SavedLeft;
        _savedTop = snapshot.SavedTop;
        AutomationProperties.SetName(_root, $"Muesli dictation indicator, {_state.ToString().ToLowerInvariant()}");

        if (!_visible)
        {
            _waveTimer.Stop();
            if (_shown)
            {
                AppWindow.Hide();
                _shown = false;
            }
            return;
        }

        if (_state != FloatingIndicatorState.Idle)
        {
            _hovered = false;
            _suppressHoverUntilPointerLeaves = true;
        }

        if (_state == FloatingIndicatorState.Recording && previousState != FloatingIndicatorState.Recording) _smoother.Reset();
        if (_state == FloatingIndicatorState.Preparing && previousState != FloatingIndicatorState.Preparing) _waveStart = DateTime.UtcNow;

        Render();
        if (!_shown)
        {
            AppWindow.Show(false);
            _shown = true;
        }

        var animate = _state is FloatingIndicatorState.Recording or FloatingIndicatorState.Preparing;
        if (animate && !_waveTimer.IsRunning) _waveTimer.Start();
        else if (!animate && _waveTimer.IsRunning) _waveTimer.Stop();
    }

    private bool MinimalResting =>
        _minimalStyle && !_highContrast && _state == FloatingIndicatorState.Idle && !_hovered;

    private void Render()
    {
        var size = MinimalResting
            ? FloatingIndicatorLayout.MinimalRestingSize(_indicatorAnchor)
            : FloatingIndicatorLayout.SizeFor(_state, _hovered, _message);
        _widthDip = size.Width;
        _heightDip = size.Height;
        _cornerRadiusDip = size.CornerRadius;

        _root.Opacity = FloatingIndicatorLayout.WindowAlpha(_state, _hovered);
        _pill.CornerRadius = new CornerRadius(_cornerRadiusDip);
        _pill.Padding = PaddingFor(_state, _hovered);
        _pill.Background = GlassBrush(_state, _hovered);
        _pill.BorderBrush = BorderBrushFor(_state, _hovered);
        BuildContent();
        PositionPill();
    }

    // ─── Content ─────────────────────────────────────────────────────────────────────────────

    private void BuildContent()
    {
        _transcribingWiggle?.Stop();
        _transcribingWiggle = null;
        _content.Children.Clear();
        _waveBars.Clear();
        var brush = ContentBrush();

        switch (_state)
        {
            case FloatingIndicatorState.Preparing:
                _content.Children.Add(WaveformBars(brush));
                break;
            case FloatingIndicatorState.Recording:
                BuildRecording(brush);
                break;
            case FloatingIndicatorState.Transcribing:
                BuildTranscribing(brush);
                break;
            case FloatingIndicatorState.Success:
                BuildStatus(false, brush);
                break;
            case FloatingIndicatorState.Error:
                BuildStatus(true, brush);
                break;
            default:
                BuildIdle();
                break;
        }
    }

    private void BuildIdle()
    {
        if (MinimalResting)
        {
            // macOS Minimal: a quiet grey grip until hovered.
            var vertical = FloatingIndicatorLayout.MinimalGripIsVertical(_indicatorAnchor);
            _content.Children.Add(new Border
            {
                Width = vertical ? FloatingIndicatorLayout.MinimalGripThickness : FloatingIndicatorLayout.MinimalGripLength,
                Height = vertical ? FloatingIndicatorLayout.MinimalGripLength : FloatingIndicatorLayout.MinimalGripThickness,
                CornerRadius = new CornerRadius(FloatingIndicatorLayout.MinimalGripThickness / 2),
                Background = Solid(0xD1, 0x69, 0x69, 0x69),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            return;
        }

        if (!_hovered)
        {
            _content.Children.Add(BrandMark(FloatingIndicatorLayout.IdleBrandMarkSize));
            return;
        }

        var stack = CenteredRow();
        stack.Children.Add(BrandMark(FloatingIndicatorLayout.HoverBrandMarkSize));
        stack.Children.Add(new TextBlock
        {
            Text = _handsFree
                ? $"Hold {_hotkeyLabel} to dictate, or double-tap for hands-free"
                : $"Hold {_hotkeyLabel} to dictate",
            FontSize = 11,
            Foreground = _highContrast ? ContentBrush() : Solid(0xBF, 0xFF, 0xFF, 0xFF),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0)
        });
        _content.Children.Add(stack);
    }

    private StackPanel WaveformBars(Brush brush)
    {
        var stack = CenteredRow();
        for (var index = 0; index < IndicatorWaveform.BarCount; index++)
        {
            var bar = new Border
            {
                Width = FloatingIndicatorLayout.WaveformBarWidth,
                Height = FloatingIndicatorLayout.WaveformMinHeight,
                Margin = new Thickness(FloatingIndicatorLayout.WaveformBarSpacing / 2, 0, FloatingIndicatorLayout.WaveformBarSpacing / 2, 0),
                CornerRadius = new CornerRadius(FloatingIndicatorLayout.WaveformBarWidth / 2),
                Background = brush,
                VerticalAlignment = VerticalAlignment.Center
            };
            _waveBars.Add(bar);
            stack.Children.Add(bar);
        }

        return stack;
    }

    private void BuildRecording(Brush brush)
    {
        // macOS layout: cancel glyph in a 10-point box at x=7, bars centred on the pill, stop square
        // 8 points from the right edge.
        var isMeeting = _owner == IndicatorOwnerKind.Meeting;
        var grid = new Grid();
        grid.Children.Add(new TextBlock
        {
            Text = FloatingIndicatorLayout.RecordingCancelGlyph,
            FontSize = isMeeting ? FloatingIndicatorLayout.MeetingControlFontSize : FloatingIndicatorLayout.RecordingCancelFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = _highContrast
                ? brush
                : Solid(isMeeting ? FloatingIndicatorLayout.MeetingControlAlpha : FloatingIndicatorLayout.RecordingCancelAlpha, 0xFF, 0xFF, 0xFF),
            Width = 10,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        });
        grid.Children.Add(WaveformBars(brush));
        grid.Children.Add(new Border
        {
            Width = FloatingIndicatorLayout.StopSquareSize,
            Height = FloatingIndicatorLayout.StopSquareSize,
            CornerRadius = new CornerRadius(FloatingIndicatorLayout.StopSquareRadius),
            Background = brush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, FloatingIndicatorLayout.StopSquareRightMargin, 0)
        });
        _content.Children.Add(grid);
    }

    private void BuildTranscribing(Brush brush)
    {
        // macOS: an 18-point icon and "Transcribing" in 11pt at 82% white, 6 points apart.
        var rotation = new RotateTransform();
        var icon = new Path
        {
            Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), FloatingIndicatorLayout.TranscribingIconPathData),
            Fill = brush,
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            RenderTransformOrigin = new global::Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = rotation,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (new UISettings().AnimationsEnabled)
        {
            var wiggle = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            foreach (var (seconds, degrees) in FloatingIndicatorLayout.TranscribingWiggleKeys)
            {
                wiggle.KeyFrames.Add(new LinearDoubleKeyFrame { Value = degrees, KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds)) });
            }

            Storyboard.SetTarget(wiggle, rotation);
            Storyboard.SetTargetProperty(wiggle, nameof(RotateTransform.Angle));
            _transcribingWiggle = new Storyboard();
            _transcribingWiggle.Children.Add(wiggle);
            _transcribingWiggle.Begin();
        }

        var stack = CenteredRow();
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock
        {
            Text = "Transcribing",
            FontSize = 11,
            Foreground = _highContrast ? brush : Solid(0xD1, 0xFF, 0xFF, 0xFF),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        _content.Children.Add(stack);
    }

    private void BuildStatus(bool isError, Brush brush)
    {
        // macOS showNotice: a solid status pill with a bold glyph and 11pt medium text in dark ink.
        var ink = _highContrast ? brush : Solid(0xF2, 0x1A, 0x14, 0x0D);
        var stack = CenteredRow();
        stack.Children.Add(new TextBlock
        {
            Text = isError ? "!" : "✓",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = ink,
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_message) ? (isError ? "Error" : "Done") : _message,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = ink,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        _content.Children.Add(stack);
    }

    private StackPanel BrandMark(double size)
    {
        // The app icon's 13-bar "M" mark, pixel-snapped so the bars stay separate.
        var (barWidth, gap, heights) = FloatingIndicatorLayout.BrandMarkGeometry(size, Scale());
        var stack = CenteredRow();
        var fill = _highContrast ? ContentBrush() : Solid(0xE6, 0xFF, 0xFF, 0xFF);
        for (var index = 0; index < heights.Length; index++)
        {
            stack.Children.Add(new Border
            {
                Width = barWidth,
                Height = heights[index],
                Margin = new Thickness(0, 0, index < heights.Length - 1 ? gap : 0, 0),
                CornerRadius = new CornerRadius(barWidth / 2),
                Background = fill,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        return stack;
    }

    private static StackPanel CenteredRow() => new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    // ─── Waveform ────────────────────────────────────────────────────────────────────────────

    private void AnimateWaveform()
    {
        if (_closed || !_visible || _waveBars.Count == 0) return;

        if (_state == FloatingIndicatorState.Recording)
        {
            // Read the live level directly instead of receiving a UI-thread message per audio buffer.
            var level = Math.Max(_snapshotLevel, Math.Clamp(_liveLevel(), 0f, 1f));
            var smoothed = _smoother.Next(IndicatorWaveform.AmplitudeFromRms(level));
            for (var index = 0; index < _waveBars.Count; index++)
            {
                _waveBars[index].Height = IndicatorWaveform.LevelBarHeight(smoothed, index);
                _waveBars[index].Opacity = IndicatorWaveform.LevelOpacity;
            }
            return;
        }

        if (_state == FloatingIndicatorState.Preparing)
        {
            var elapsed = (DateTime.UtcNow - _waveStart).TotalSeconds;
            for (var index = 0; index < _waveBars.Count; index++)
            {
                var (height, opacity) = IndicatorWaveform.WaitingBar(elapsed, index);
                _waveBars[index].Height = height;
                _waveBars[index].Opacity = opacity;
            }
        }
    }

    // ─── Colours ─────────────────────────────────────────────────────────────────────────────

    private Brush ContentBrush() =>
        _highContrast
            ? new SolidColorBrush(new UISettings().UIElementColor(UIElementType.WindowText))
            : Solid(0xD9, 0xFF, 0xFF, 0xFF);

    private Brush BorderBrushFor(FloatingIndicatorState state, bool hovered)
    {
        if (MinimalResting) return Solid(0, 0, 0, 0);
        if (_highContrast) return ContentBrush();
        var opacity = state switch
        {
            FloatingIndicatorState.Idle => hovered ? 0.14 : 0.22,
            FloatingIndicatorState.Success or FloatingIndicatorState.Error => 0.24,
            _ => 0.16
        };
        return Solid((byte)(255 * opacity), 0xFF, 0xFF, 0xFF);
    }

    private Brush GlassBrush(FloatingIndicatorState state, bool hovered)
    {
        if (_highContrast) return new SolidColorBrush(new UISettings().UIElementColor(UIElementType.Window));
        // Alpha 1, not 0, so the margin around the minimal grip still catches the pointer.
        if (MinimalResting) return Solid(1, 0, 0, 0);

        switch (state)
        {
            case FloatingIndicatorState.Recording:
                var (r, g, b) = ParseAccent(_recordingAccentHex);
                return Solid((byte)(255 * 0.85), r, g, b);
            case FloatingIndicatorState.Success:
                return Solid((byte)(255 * 0.92), 0x34, 0xC7, 0x59);
            case FloatingIndicatorState.Error:
                return Solid((byte)(255 * 0.92), 0xD9, 0x9A, 0x11);
        }

        var alpha = state == FloatingIndicatorState.Idle ? (hovered ? 0.72 : 0.44) : 0.62;
        var (fa, fr, fg, fb) = FloatingIndicatorLayout.GlassFill(FloatingIndicatorLayout.GlassTintRgb, alpha);
        return Solid(fa, fr, fg, fb);
    }

    private static (byte R, byte G, byte B) ParseAccent(string? hex)
    {
        var value = (hex ?? "").Trim().TrimStart('#');
        if (value.Length == 6 && uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var packed))
        {
            return ((byte)((packed >> 16) & 0xFF), (byte)((packed >> 8) & 0xFF), (byte)(packed & 0xFF));
        }

        return (0x1E, 0x1E, 0x2E);
    }

    private static Thickness PaddingFor(FloatingIndicatorState state, bool hovered) => state switch
    {
        FloatingIndicatorState.Idle => hovered ? new Thickness(12, 0, 12, 0) : new Thickness(0),
        FloatingIndicatorState.Preparing or FloatingIndicatorState.Recording => new Thickness(0),
        FloatingIndicatorState.Transcribing => new Thickness(14, 0, 14, 0),
        _ => new Thickness(18, 0, 18, 0)
    };

    private static SolidColorBrush Solid(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));

    // ─── Positioning ─────────────────────────────────────────────────────────────────────────

    private double Scale() => FloatingIndicatorLayout.DpiScaleFor(GetDpiForWindow(_hwnd));

    private void PositionPill()
    {
        // While dragging, the pointer owns the position.
        if (_drag.IsDragging)
        {
            ResizeInPlace();
            return;
        }

        var display = DisplayForPlacement(out var scale);
        var work = display.WorkArea;
        var workArea = new DipRect(work.X / scale, work.Y / scale, work.Width / scale, work.Height / scale);
        var size = new DipSize(_widthDip, _heightDip, _cornerRadiusDip);

        DipPoint topLeft;
        if (IsCustomAnchor && _savedLeft is { } left && _savedTop is { } top)
        {
            if (FloatingIndicatorLayout.RepairSavedCenter(new DipPoint(left, top), size, workArea) is { } repaired)
            {
                _savedLeft = repaired.X;
                _savedTop = repaired.Y;
            }

            topLeft = FloatingIndicatorLayout.Clamp(
                new DipPoint(_savedLeft!.Value - size.Width / 2, _savedTop!.Value - size.Height / 2), size, workArea);
        }
        else
        {
            topLeft = FloatingIndicatorLayout.AnchorTopLeft(_indicatorAnchor, size, workArea);
        }

        // Physical pixels on the target monitor, so a monitor with a different scale lands exactly.
        AppWindow.MoveAndResize(new RectInt32(
            FloatingIndicatorLayout.ToPixels(topLeft.X, scale),
            FloatingIndicatorLayout.ToPixels(topLeft.Y, scale),
            FloatingIndicatorLayout.ToPixels(_widthDip, scale),
            FloatingIndicatorLayout.ToPixels(_heightDip, scale)));
    }

    private void ResizeInPlace()
    {
        var scale = Scale();
        AppWindow.Resize(new SizeInt32(
            FloatingIndicatorLayout.ToPixels(_widthDip, scale),
            FloatingIndicatorLayout.ToPixels(_heightDip, scale)));
    }

    private bool IsCustomAnchor => string.Equals(_indicatorAnchor, "Custom", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The monitor to place on: a dragged position belongs to the monitor it was dropped on (saved
    /// as DIPs of that monitor); otherwise the monitor under the cursor, as on macOS.
    /// </summary>
    private DisplayArea DisplayForPlacement(out double scale)
    {
        if (IsCustomAnchor && _savedLeft is { } left && _savedTop is { } top)
        {
            // Index, don't foreach: enumerating this projected vector view throws InvalidCastException
            // (E_NOINTERFACE) in CsWinRT; Count plus the indexer work.
            var displays = DisplayArea.FindAll();
            for (var index = 0; index < displays.Count; index++)
            {
                var candidate = displays[index];
                var candidateScale = DisplayScale(candidate);
                var bounds = candidate.OuterBounds;
                var x = (int)Math.Round(left * candidateScale);
                var y = (int)Math.Round(top * candidateScale);
                if (x >= bounds.X && x < bounds.X + bounds.Width && y >= bounds.Y && y < bounds.Y + bounds.Height)
                {
                    scale = candidateScale;
                    return candidate;
                }
            }
        }

        GetCursorPos(out var cursor);
        var display = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest);
        scale = DisplayScale(display);
        return display;
    }

    private static double DisplayScale(DisplayArea display)
    {
        var monitor = Win32Interop.GetMonitorFromDisplayId(display.DisplayId);
        return GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0
            ? FloatingIndicatorLayout.DpiScaleFor(dpiX)
            : 1.0;
    }

    private DipPoint WindowTopLeftDip()
    {
        var scale = Scale();
        return new DipPoint(AppWindow.Position.X / scale, AppWindow.Position.Y / scale);
    }

    private DipRect CurrentWorkAreaDip()
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        var scale = DisplayScale(display);
        var work = display.WorkArea;
        return new DipRect(work.X / scale, work.Y / scale, work.Width / scale, work.Height / scale);
    }

    // ─── Interaction ─────────────────────────────────────────────────────────────────────────

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerInside = true;
        if (_visible && _state == FloatingIndicatorState.Recording && _owner == IndicatorOwnerKind.Meeting)
        {
            var position = AppWindow.Position;
            var size = AppWindow.Size;
            _sendCommand(new IndicatorCommand
            {
                SessionId = _sessionId,
                Type = IndicatorCommandType.HoverEnter,
                IndicatorX = position.X,
                IndicatorY = position.Y,
                IndicatorWidth = size.Width,
                IndicatorHeight = size.Height
            });
            return;
        }

        if (_state != FloatingIndicatorState.Idle || !_visible || _suppressHoverUntilPointerLeaves || _hovered) return;
        _hovered = true;
        _collapseTimer.Stop();
        _sendCommand(new IndicatorCommand { SessionId = _sessionId, Type = IndicatorCommandType.HoverEnter });
        Render();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerInside = false;
        if (_state == FloatingIndicatorState.Recording && _owner == IndicatorOwnerKind.Meeting)
        {
            _sendCommand(new IndicatorCommand { SessionId = _sessionId, Type = IndicatorCommandType.HoverExit });
            return;
        }

        if (_state != FloatingIndicatorState.Idle) return;
        _suppressHoverUntilPointerLeaves = false;
        _sendCommand(new IndicatorCommand { SessionId = _sessionId, Type = IndicatorCommandType.HoverExit });
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void CollapseHover()
    {
        if (!_hovered || _state != FloatingIndicatorState.Idle || !_visible || _pointerInside) return;
        _hovered = false;
        Render();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(_pill).Properties.IsLeftButtonPressed) return;
        // Movement is measured from a fixed screen-pixel origin, never from the window's own moves.
        GetCursorPos(out var cursor);
        _drag.Begin(new DipPoint(cursor.X, cursor.Y), WindowTopLeftDip());
        _pill.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_drag.IsDragging) return;
        GetCursorPos(out var cursor);
        var scale = Scale();
        var target = _drag.Move(
            new DipPoint(cursor.X, cursor.Y),
            scale,
            new DipSize(_widthDip, _heightDip, _cornerRadiusDip),
            CurrentWorkAreaDip());
        if (target is { } point)
        {
            AppWindow.Move(new PointInt32(
                FloatingIndicatorLayout.ToPixels(point.X, scale),
                FloatingIndicatorLayout.ToPixels(point.Y, scale)));
        }
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // Losing capture without a release must still end the gesture, or the pill could never be
        // positioned automatically again.
        if (_drag.IsDragging) PersistDragIfMoved();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var persisted = _drag.IsDragging && PersistDragIfMoved();
        _pill.ReleasePointerCapture(e.Pointer);
        if (persisted || _drag.ConsumeEndedMovedGesture() || _state != FloatingIndicatorState.Recording) return;

        var x = e.GetCurrentPoint(_pill).Position.X;
        var action = _owner == IndicatorOwnerKind.Meeting
            ? FloatingIndicatorLayout.MeetingActionForClick(x, _widthDip)
            : FloatingIndicatorLayout.ActionForClick(_state, x);
        if (_owner == IndicatorOwnerKind.Meeting && action == FloatingIndicatorAction.None) return;
        _sendCommand(new IndicatorCommand
        {
            SessionId = _sessionId,
            Type = action == FloatingIndicatorAction.Cancel ? IndicatorCommandType.Cancel : IndicatorCommandType.Stop
        });
    }

    private bool PersistDragIfMoved()
    {
        var command = _drag.End(_sessionId, WindowTopLeftDip(), new DipSize(_widthDip, _heightDip, _cornerRadiusDip));
        if (command is null) return false;
        _savedLeft = command.DragLeft;
        _savedTop = command.DragTop;
        _indicatorAnchor = "Custom";
        _sendCommand(command);
        return true;
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (FloatingIndicatorStateClassifier.IsActive(_state))
        {
            _sendCommand(new IndicatorCommand { SessionId = _sessionId, Type = IndicatorCommandType.Cancel });
        }
    }

    private void OnClosed()
    {
        _closed = true;
        _waveTimer.Stop();
        _collapseTimer.Stop();
        _transcribingWiggle?.Stop();
        RemoveWindowSubclass(_hwnd, _subclassProc, 1);
    }

    private static FloatingIndicatorState StateFromName(string? name) =>
        (name ?? "idle").Trim().ToLowerInvariant() switch
        {
            "preparing" => FloatingIndicatorState.Preparing,
            "recording" => FloatingIndicatorState.Recording,
            "transcribing" => FloatingIndicatorState.Transcribing,
            "success" => FloatingIndicatorState.Success,
            "error" => FloatingIndicatorState.Error,
            _ => FloatingIndicatorState.Idle
        };

    // ─── Win32 ───────────────────────────────────────────────────────────────────────────────

    private const int GwlExStyle = -20;
    private const int GwlStyle = -16;
    private const long WsPopup = 0x80000000L;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsSysMenu = 0x00080000L;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExAppWindow = 0x00040000;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaTransitionsForceDisabled = 3;
    private const int DwmwcpDoNotRound = 1;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmEraseBkgnd = 0x0014;
    private const int BlackBrush = 4;
    private const uint WmMouseActivate = 0x0021;
    private const uint WmDisplayChange = 0x007E;
    private const uint WmDpiChanged = 0x02E0;
    private const int MaNoActivate = 3;
    private const int MinMaxInfoMinTrackOffset = 24; // MINMAXINFO.ptMinTrackSize
    private const int MdtEffectiveDpi = 0;

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id, IntPtr data);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern long GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern long SetWindowLongPtr(IntPtr hwnd, int index, long value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hdc, ref NativeRect rect, IntPtr brush);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int index);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
