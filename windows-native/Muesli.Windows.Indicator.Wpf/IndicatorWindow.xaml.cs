using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Muesli.Windows.Core.Services;
using MediaColor = System.Windows.Media.Color;
using MediaBrushes = System.Windows.Media.Brushes;

namespace Muesli.Windows.Indicator.Wpf;

/// <summary>
/// The WPF floating-indicator renderer. It draws exactly one pill from a complete
/// <see cref="IndicatorSnapshot"/> and relays clicks/drags back to the WinUI owner as commands.
/// It performs no product work: no hotkey, no audio, no transcription, no database access, and it
/// never appears in the taskbar or Alt+Tab.
/// </summary>
public partial class IndicatorWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;
    private const int WmNcHitTest = 0x0084;
    private const int HitTestTransparent = -1;

    private readonly Action<IndicatorCommand> _sendCommand;
    private readonly List<Border> _waveBars = [];
    private readonly IndicatorAmplitudeSmoother _smoother = new();
    private readonly DispatcherTimer _waveTimer;

    private FloatingIndicatorState _state = FloatingIndicatorState.Idle;
    private string _owner = IndicatorOwnerKind.None;
    private bool _visible;
    private bool _hovered;
    private bool _suppressHoverUntilMouseLeaves;
    private bool _disposed;
    private double _widthDip = FloatingIndicatorLayout.CompactIdleWidth;
    private double _heightDip = FloatingIndicatorLayout.CompactIdleHeight;
    private double _cornerRadiusDip = FloatingIndicatorLayout.CompactIdleRadius;
    private double _windowOpacity = 1.0;

    private double _targetPeak;
    private DateTime _waveStart = DateTime.UtcNow;

    /// <summary>The last snapshot applied, used to detect amplitude-only frames that must not render.</summary>
    private IndicatorSnapshot? _appliedSnapshot;

    private bool _meetingPaused;
    private string _recordingAccentHex = "1e1e2e";
    private string _hotkeyLabel = "shortcut";
    private bool _handsFree;
    private string _message = "";
    private string _indicatorAnchor = "Middle Right";
    private double? _savedLeft;
    private double? _savedTop;
    private long _sessionId;

    private readonly FloatingIndicatorDragSession _drag = new();

    private bool _highContrast;

    public IndicatorWindow(Action<IndicatorCommand> sendCommand)
    {
        InitializeComponent();
        _sendCommand = sendCommand;
        SourceInitialized += OnSourceInitialized;
        System.Windows.Automation.AutomationProperties.SetAutomationId(this, "FloatingDictationIndicator");
        System.Windows.Automation.AutomationProperties.SetName(this, "Muesli dictation indicator");
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _waveTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / FloatingIndicatorLayout.WaveformUpdateFramesPerSecond)
        };
        _waveTimer.Tick += (_, _) => AnimateWaveform();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_visible && !_disposed) PositionPill();
        });
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(WindowHook);

        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, GwlExStyle);
        SetWindowLongPtr(handle, GwlExStyle, (style | WsExToolWindow | WsExNoActivate));
    }

    private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmNcHitTest)
        {
            handled = true;
            return IsInPill(lParam) ? (IntPtr)HitTestCallbackClient : (IntPtr)HitTestTransparent;
        }
        return IntPtr.Zero;
    }

    private const int HitTestCallbackClient = 0x1; // HTCLIENT

    /// <summary>Hit-tests the screenshot cursor position against the rounded pill, in DIPs.</summary>
    private bool IsInPill(IntPtr lParam)
    {
        try
        {
            var screenX = unchecked((short)(lParam.ToInt64() & 0xFFFF));
            var screenY = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
            var client = PointFromScreen(new Point(screenX, screenY));
            var width = ActualWidth;
            var height = ActualHeight;
            var radius = _cornerRadiusDip;
            return InRoundedRect(client, width, height, radius);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool InRoundedRect(Point p, double width, double height, double radius)
    {
        if (width <= 0 || height <= 0) return false;
        if (p.X < 0 || p.Y < 0 || p.X > width || p.Y > height) return false;
        var r = Math.Min(radius, Math.Min(width, height) / 2);
        if (r <= 0) return true;
        var cx1 = r;
        var cx2 = width - r;
        var cy1 = r;
        var cy2 = height - r;
        // Full capsule or rounded rect: inside the central band or within one of the corner arcs.
        if (p.X >= cx1 && p.X <= cx2) return true; // central horizontal band
        if (p.Y >= cy1 && p.Y <= cy2) return true; // central vertical band
        var cornerX = p.X < cx1 ? cx1 : cx2;
        var cornerY = p.Y < cy1 ? cy1 : cy2;
        var dx = p.X - cornerX;
        var dy = p.Y - cornerY;
        return dx * dx + dy * dy <= r * r;
    }

    // ─── Snapshot application ────────────────────────────────────────────────────────────────

    public void Apply(IndicatorSnapshot snapshot)
    {
        if (_disposed) return;

        _sessionId = snapshot.SessionId;
        if (snapshot.Amplitude >= 0)
        {
            _targetPeak = Math.Clamp(snapshot.Amplitude, 0f, 1f);
        }

        // Amplitude-only frames arrive at up to 30 fps while recording. They only move the live
        // level; they must not rebuild content, reposition the window, or churn the animation
        // timer. Otherwise the bars are destroyed and reset ~30 times a second and a drag is
        // overwritten by the next sample.
        if (_appliedSnapshot is { } previous)
        {
            var structural = IndicatorProtocol.IsStructuralChange(previous, snapshot);
            _appliedSnapshot = snapshot;
            if (!structural) return;
        }
        else
        {
            _appliedSnapshot = snapshot;
        }

        var previousState = _state;
        _state = StateFromName(snapshot.State);
        _owner = snapshot.Owner;
        _visible = snapshot.Visible;
        _meetingPaused = snapshot.MeetingPaused;
        _handsFree = snapshot.HandsFree;
        _hotkeyLabel = string.IsNullOrWhiteSpace(snapshot.HotkeyLabel) ? "shortcut" : snapshot.HotkeyLabel;
        _recordingAccentHex = string.IsNullOrWhiteSpace(snapshot.RecordingColorHex) ? "1e1e2e" : snapshot.RecordingColorHex;
        _message = snapshot.Message ?? "";
        _highContrast = snapshot.HighContrast;
        _indicatorAnchor = string.IsNullOrWhiteSpace(snapshot.IndicatorAnchor) ? "Middle Right" : snapshot.IndicatorAnchor;
        _savedLeft = snapshot.SavedLeft;
        _savedTop = snapshot.SavedTop;

        System.Windows.Automation.AutomationProperties.SetName(
            this, $"Muesli dictation indicator, {_state.ToString().ToLowerInvariant()}");

        if (!_visible)
        {
            _waveTimer.Stop();
            Hide();
            return;
        }

        if (_state != FloatingIndicatorState.Idle)
        {
            _hovered = false;
            _suppressHoverUntilMouseLeaves = true;
        }

        // A new live waveform starts from a clean slate, not from the previous session's level.
        if (_state == FloatingIndicatorState.Recording && previousState != FloatingIndicatorState.Recording)
        {
            _smoother.Reset();
        }
        if (_state == FloatingIndicatorState.Preparing && previousState != FloatingIndicatorState.Preparing)
        {
            _waveStart = DateTime.UtcNow;
        }

        if (!IsVisible)
        {
            // Show before sizing/positioning so a PresentationSource exists for per-monitor DPI
            // and work-area math. Paint fully transparent until Render sets the real state, so
            // the pill never flashes at its provisional top-left location.
            Opacity = 0;
            Show();
        }

        Render();
        UpdateWaveTimer();
    }

    /// <summary>
    /// Keeps the animation timer continuously active while a live waveform is on screen, and stops
    /// it only when the pill leaves Recording/Preparing (or is hidden). Starting/stopping it on
    /// every frame would repeatedly reset its countdown and stall the animation.
    /// </summary>
    private void UpdateWaveTimer()
    {
        var shouldRun = _visible &&
                        _state is FloatingIndicatorState.Recording or FloatingIndicatorState.Preparing;
        if (shouldRun)
        {
            if (!_waveTimer.IsEnabled) _waveTimer.Start();
        }
        else if (_waveTimer.IsEnabled)
        {
            _waveTimer.Stop();
        }
    }

    private void Render()
    {
        var size = FloatingIndicatorLayout.SizeFor(_state, _hovered);
        _widthDip = size.Width;
        _heightDip = size.Height;
        _cornerRadiusDip = size.CornerRadius;
        _windowOpacity = FloatingIndicatorLayout.WindowAlpha(_state, _hovered);

        Width = _widthDip;
        Height = _heightDip;
        Opacity = _windowOpacity;

        Pill.CornerRadius = new CornerRadius(_cornerRadiusDip);
        Pill.Padding = PaddingFor(_state, _hovered);
        Pill.Background = GlassBrush(_state, _hovered);
        Pill.BorderBrush = BorderBrushFor(_state, _hovered);

        BuildContent(_state, _hovered);

        PositionPill();
    }

    private void BuildContent(FloatingIndicatorState state, bool hovered)
    {
        ContentHost.Children.Clear();
        _waveBars.Clear();
        var brush = ContentBrush();

        switch (state)
        {
            case FloatingIndicatorState.Idle:
                BuildIdle(hovered, brush);
                break;
            case FloatingIndicatorState.Preparing:
                BuildWaveform(IndicatorWaveform.BarCountFor(state), brush);
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
                BuildIdle(false, brush);
                break;
        }
    }

    private void BuildIdle(bool hovered, Brush brush)
    {
        if (!hovered)
        {
            ContentHost.Children.Add(MuesliGlyph(18, new Thickness()));
            return;
        }

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(MuesliGlyph(15, new Thickness()));
        stack.Children.Add(new TextBlock
        {
            Text = _handsFree
                ? $"Hold {_hotkeyLabel} to dictate, or double-tap for hands-free"
                : $"Hold {_hotkeyLabel} to dictate",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(MediaColor.FromArgb(0xBF, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0)
        });
        ContentHost.Children.Add(stack);
    }

    private void BuildWaveform(int barCount, Brush brush)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        for (var index = 0; index < barCount; index++)
        {
            var bar = new Border
            {
                Width = FloatingIndicatorLayout.WaveformBarWidth,
                Height = FloatingIndicatorLayout.WaveformMinHeight,
                Margin = new Thickness(FloatingIndicatorLayout.WaveformBarSpacing / 2, 0,
                                       FloatingIndicatorLayout.WaveformBarSpacing / 2, 0),
                CornerRadius = new CornerRadius(FloatingIndicatorLayout.WaveformBarWidth / 2),
                Background = brush,
                VerticalAlignment = VerticalAlignment.Center
            };
            _waveBars.Add(bar);
            stack.Children.Add(bar);
        }
        ContentHost.Children.Add(stack);
    }

    private void BuildRecording(Brush brush)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });

        // Symmetric 16/…/16 columns keep the waveform centred on the pill exactly as the macOS
        // reference centres it across the full width.
        var isMeeting = _owner == IndicatorOwnerKind.Meeting;
        var leftGlyph = isMeeting
            ? (_meetingPaused ? "▶" : "❚❚")
            : FloatingIndicatorLayout.RecordingCancelGlyph;
        var left = new TextBlock
        {
            Text = leftGlyph,
            FontSize = isMeeting
                ? FloatingIndicatorLayout.MeetingControlFontSize
                : FloatingIndicatorLayout.RecordingCancelFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(MediaColor.FromArgb(
                isMeeting ? FloatingIndicatorLayout.MeetingControlAlpha : FloatingIndicatorLayout.RecordingCancelAlpha,
                0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var wave = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var barCount = IndicatorWaveform.BarCountFor(FloatingIndicatorState.Recording);
        for (var index = 0; index < barCount; index++)
        {
            var bar = new Border
            {
                Width = FloatingIndicatorLayout.WaveformBarWidth,
                Height = FloatingIndicatorLayout.WaveformMinHeight,
                Margin = new Thickness(FloatingIndicatorLayout.WaveformBarSpacing / 2, 0,
                                       FloatingIndicatorLayout.WaveformBarSpacing / 2, 0),
                CornerRadius = new CornerRadius(FloatingIndicatorLayout.WaveformBarWidth / 2),
                Background = brush,
                VerticalAlignment = VerticalAlignment.Center
            };
            _waveBars.Add(bar);
            wave.Children.Add(bar);
        }
        Grid.SetColumn(wave, 1);
        grid.Children.Add(wave);

        var stop = new Border
        {
            Width = FloatingIndicatorLayout.StopSquareSize,
            Height = FloatingIndicatorLayout.StopSquareSize,
            CornerRadius = new CornerRadius(FloatingIndicatorLayout.StopSquareRadius),
            Background = brush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, FloatingIndicatorLayout.StopSquareRightMargin, 0)
        };
        Grid.SetColumn(stop, 2);
        grid.Children.Add(stop);

        ContentHost.Children.Add(grid);
    }

    private void BuildTranscribing(Brush brush)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(new TextBlock
        {
            Text = "✎",
            FontSize = 11,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Transcribing",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(MediaColor.FromArgb(0xD1, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        if (_owner != IndicatorOwnerKind.Meeting)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "×",
                FontSize = 10,
                Foreground = new SolidColorBrush(MediaColor.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        ContentHost.Children.Add(stack);
    }

    private void BuildStatus(bool isError, Brush brush)
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
                ? MediaColor.FromRgb(0xF8, 0x71, 0x71)
                : MediaColor.FromRgb(0x34, 0xD3, 0x99)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var label = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_message) ? (isError ? "Error" : "Done") : _message,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(MediaColor.FromArgb(0xD1, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        ContentHost.Children.Add(grid);
    }

    // ─── Waveform animation ───────────────────────────────────────────────────────────────────

    private void AnimateWaveform()
    {
        if (_disposed || !_visible || _waveBars.Count == 0) return;

        if (_state == FloatingIndicatorState.Recording)
        {
            var smoothed = _smoother.Next(_targetPeak);
            for (var index = 0; index < _waveBars.Count; index++)
            {
                _waveBars[index].Height = FloatingIndicatorLayout.RecordingBarHeight((float)smoothed, index);
            }
            return;
        }

        if (_state == FloatingIndicatorState.Preparing)
        {
            var elapsed = (DateTime.UtcNow - _waveStart).TotalSeconds;
            for (var index = 0; index < _waveBars.Count; index++)
            {
                var scale = IndicatorWaveform.PreparingPulseScale(elapsed, index);
                var multiplier = FloatingIndicatorLayout.PreparingBarMultipliers[index];
                var span = (FloatingIndicatorLayout.WaveformMaxHeightFor(FloatingIndicatorState.Preparing)
                            - FloatingIndicatorLayout.WaveformMinHeight) * multiplier;
                _waveBars[index].Height = FloatingIndicatorLayout.WaveformMinHeight + scale * span;
            }
        }
    }

    // ─── Colours ──────────────────────────────────────────────────────────────────────────────

    private Brush ContentBrush() =>
        _highContrast
            ? SystemColors.ControlTextBrush
            : new SolidColorBrush(MediaColor.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));

    private Brush BorderBrushFor(FloatingIndicatorState state, bool hovered)
    {
        var opacity = state switch
        {
            FloatingIndicatorState.Idle => hovered ? 0.14 : 0.22,
            FloatingIndicatorState.Preparing or FloatingIndicatorState.Recording or FloatingIndicatorState.Transcribing => 0.16,
            FloatingIndicatorState.Success or FloatingIndicatorState.Error => 0.14,
            _ => 0.22
        };
        return _highContrast
            ? SystemColors.ControlTextBrush
            : new SolidColorBrush(MediaColor.FromArgb((byte)(255 * opacity), 0xFF, 0xFF, 0xFF));
    }

    private Brush GlassBrush(FloatingIndicatorState state, bool hovered)
    {
        if (_highContrast) return SystemColors.ControlBrush;

        var accent = ParseAccent(_recordingAccentHex);
        var isRecording = state == FloatingIndicatorState.Recording;
        var baseColor = isRecording ? accent : MediaColor.FromRgb(0x1E, 0x1E, 0x2E);
        var alpha = state switch
        {
            FloatingIndicatorState.Idle => hovered ? 0.72 : 0.44,
            FloatingIndicatorState.Preparing => 0.62,
            FloatingIndicatorState.Recording => 0.85,
            FloatingIndicatorState.Transcribing => 0.62,
            FloatingIndicatorState.Success or FloatingIndicatorState.Error => 0.72,
            _ => 0.44
        };
        return new SolidColorBrush(MediaColor.FromArgb((byte)(255 * alpha), baseColor.R, baseColor.G, baseColor.B));
    }

    private static MediaColor ParseAccent(string? hex)
    {
        var value = (hex ?? "").Trim().TrimStart('#');
        if (value.Length == 6)
        {
            try
            {
                var packed = Convert.ToUInt32(value, 16);
                return MediaColor.FromRgb(
                    (byte)((packed >> 16) & 0xFF),
                    (byte)((packed >> 8) & 0xFF),
                    (byte)(packed & 0xFF));
            }
            catch (FormatException) { }
            catch (OverflowException) { }
        }
        return MediaColor.FromRgb(0x1E, 0x1E, 0x2E);
    }

    private static Thickness PaddingFor(FloatingIndicatorState state, bool hovered) =>
        state switch
        {
            FloatingIndicatorState.Idle => hovered ? new Thickness(12, 0, 12, 0) : new Thickness(0),
            FloatingIndicatorState.Preparing or FloatingIndicatorState.Recording => new Thickness(10, 0, 10, 0),
            _ => new Thickness(12, 0, 12, 0)
        };

    private static FrameworkElement MuesliGlyph(double size, Thickness margin)
    {
        var bars = new[] { 0.45, 0.65, 0.90, 1.0, 0.45, 1.0, 0.90, 0.65, 0.45 };
        var barWidth = size / 13;
        var spacing = size / 30;
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = margin,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (var multiplier in bars)
        {
            stack.Children.Add(new Border
            {
                Width = barWidth,
                Height = Math.Max(barWidth, multiplier * size),
                Margin = new Thickness(spacing / 2, 0, spacing / 2, 0),
                CornerRadius = new CornerRadius(barWidth / 2),
                Background = new SolidColorBrush(MediaColor.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        return stack;
    }

    // ─── Positioning ──────────────────────────────────────────────────────────────────────────

    private void PositionPill()
    {
        // While dragging, the pointer owns the position. Automatic anchoring, a display change, or
        // a state snapshot (including a 30 fps amplitude frame) must never yank the pill away from
        // the cursor.
        if (_drag.IsDragging) return;

        var workArea = WorkAreaForCursorDip();
        var size = new DipSize(_widthDip, _heightDip, _cornerRadiusDip);

        if (string.Equals(_indicatorAnchor, "Custom", StringComparison.OrdinalIgnoreCase) &&
            _savedLeft is { } savedLeft &&
            _savedTop is { } savedTop)
        {
            var repaired = FloatingIndicatorLayout.RepairSavedCenter(new DipPoint(savedLeft, savedTop), size, workArea);
            if (repaired is { } fixedCenter)
            {
                _savedLeft = fixedCenter.X;
                _savedTop = fixedCenter.Y;
            }
            var topLeft = new DipPoint(_savedLeft.Value - size.Width / 2, _savedTop.Value - size.Height / 2);
            MoveTo(FloatingIndicatorLayout.Clamp(topLeft, size, workArea));
            return;
        }

        MoveTo(FloatingIndicatorLayout.AnchorTopLeft(_indicatorAnchor, size, workArea));
    }

    private void MoveTo(DipPoint point)
    {
        Left = point.X;
        Top = point.Y;
    }

    private DipRect WorkAreaForCursorDip()
    {
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        return ToDipRect(screen.WorkingArea);
    }

    private DipRect ToDipRect(System.Drawing.Rectangle pixels)
    {
        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformFromDevice;
        if (transform is null)
        {
            return new DipRect(pixels.X, pixels.Y, pixels.Width, pixels.Height);
        }
        var matrix = transform.Value;
        var topLeft = matrix.Transform(new Point(pixels.Left, pixels.Top));
        var bottomRight = matrix.Transform(new Point(pixels.Right, pixels.Bottom));
        return new DipRect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
    }

    /// <summary>
    /// Physical pixels per DIP for the monitor the window is currently on
    /// (<c>1.0</c> at 100%, <c>1.5</c> at 150%). Used to convert a screen-pixel pointer delta to
    /// the DIPs that <see cref="Window.Left"/>/<see cref="Window.Top"/> are expressed in.
    /// </summary>
    private double DpiScale()
    {
        var source = PresentationSource.FromVisual(this);
        var matrix = source?.CompositionTarget?.TransformToDevice;
        return matrix is { M11: > 0 } m ? m.M11 : 1.0;
    }

    // ─── Interaction ──────────────────────────────────────────────────────────────────────────

    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };

    private void Pill_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_state != FloatingIndicatorState.Idle || !_visible || _suppressHoverUntilMouseLeaves) return;
        if (_hovered) return;
        _hovered = true;
        _sendCommand(new IndicatorCommand { SessionId = _sessionId, Type = IndicatorCommandType.HoverEnter });
        Render();
    }

    private void Pill_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_state != FloatingIndicatorState.Idle) return;
        _suppressHoverUntilMouseLeaves = false;
        _sendCommand(new IndicatorCommand { SessionId = _sessionId, Type = IndicatorCommandType.HoverExit });
        _collapseTimer.Stop();
        _collapseTimer.Tick -= CollapseTick;
        _collapseTimer.Tick += CollapseTick;
        _collapseTimer.Start();
    }

    private void CollapseTick(object? sender, EventArgs e)
    {
        _collapseTimer.Stop();
        _collapseTimer.Tick -= CollapseTick;
        if (!_hovered || _state != FloatingIndicatorState.Idle || _visible == false) return;
        if (IsMouseOver) return;
        _hovered = false;
        Render();
    }

    private void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Capture the pointer in absolute screen pixels once. Every move is measured against this
        // fixed origin, so the gesture never feeds on the window's own movement.
        var startScreenPx = PointToScreen(e.GetPosition(this));
        _drag.Begin(new DipPoint(startScreenPx.X, startScreenPx.Y), new DipPoint(Left, Top));
        CaptureMouse();
    }

    private void Pill_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_drag.IsDragging || e.LeftButton != MouseButtonState.Pressed) return;

        var currentScreenPx = PointToScreen(e.GetPosition(this));
        var size = new DipSize(_widthDip, _heightDip, _cornerRadiusDip);
        var target = _drag.Move(
            new DipPoint(currentScreenPx.X, currentScreenPx.Y),
            DpiScale(),
            size,
            WorkAreaForCursorDip());
        if (target is { } point) MoveTo(point);
    }

    /// <summary>
    /// A drag can lose mouse capture without a mouse-up (another app grabs the pointer, Alt+Tab, a
    /// touch transition, or a state change that closes the capture). If the gesture were left
    /// active, <see cref="PositionPill"/> would stay blocked forever and the pill could no longer
    /// be repositioned. Ending the gesture here persists the final position exactly once and
    /// leaves the pill responsive.
    /// </summary>
    private void Pill_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_drag.IsDragging) return;
        PersistDragIfMoved();
    }

    private void Pill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // ReleaseMouseCapture raises LostMouseCapture synchronously; PersistDragIfMoved is
        // idempotent, so the position is persisted exactly once whether the drag ended normally or
        // capture was lost first.
        var persisted = _drag.IsDragging && PersistDragIfMoved();
        ReleaseMouseCapture();

        if (persisted || _drag.ConsumeEndedMovedGesture())
        {
            return;
        }

        var x = e.GetPosition(this).X;
        var action = FloatingIndicatorLayout.ActionForClick(_state, x);
        if (_state is FloatingIndicatorState.Preparing or FloatingIndicatorState.Recording)
        {
            if (_owner == IndicatorOwnerKind.Meeting)
            {
                if (x < FloatingIndicatorLayout.CancelRegionWidthDip)
                {
                    _sendCommand(new IndicatorCommand
                    {
                        SessionId = _sessionId,
                        Type = _meetingPaused ? IndicatorCommandType.Resume : IndicatorCommandType.Pause
                    });
                }
                else
                {
                    _sendCommand(new IndicatorCommand { SessionId = _sessionId, Type = IndicatorCommandType.Stop });
                }
            }
            else
            {
                _sendCommand(new IndicatorCommand
                {
                    SessionId = _sessionId,
                    Type = action == FloatingIndicatorAction.Cancel ? IndicatorCommandType.Cancel : IndicatorCommandType.Stop
                });
            }
            return;
        }

        if (_state == FloatingIndicatorState.Transcribing)
        {
            _sendCommand(new IndicatorCommand { SessionId = _sessionId, Type = IndicatorCommandType.Cancel });
        }
    }

    /// <summary>
    /// Ends a gesture and, when the pill actually moved, records the custom centre and tells the
    /// owner once. Returns true when a persist command was sent.
    /// </summary>
    private bool PersistDragIfMoved()
    {
        var size = new DipSize(_widthDip, _heightDip, _cornerRadiusDip);
        var command = _drag.End(_sessionId, new DipPoint(Left, Top), size);
        if (command is null) return false;

        _savedLeft = command.DragLeft;
        _savedTop = command.DragTop;
        _indicatorAnchor = "Custom";
        _sendCommand(command);
        return true;
    }

    private void Pill_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_state is FloatingIndicatorState.Preparing or FloatingIndicatorState.Recording or FloatingIndicatorState.Transcribing)
        {
            _sendCommand(new IndicatorCommand { SessionId = _sessionId, Type = IndicatorCommandType.Cancel });
        }
    }

    // ─── Win32 ────────────────────────────────────────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    private static extern long GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern long SetWindowLongPtr(IntPtr hWnd, int nIndex, long value);

    protected override void OnClosing(CancelEventArgs e)
    {
        _disposed = true;
        _waveTimer.Stop();
        _collapseTimer.Stop();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _sendCommand(new IndicatorCommand { SessionId = _sessionId, Type = IndicatorCommandType.Exit });
        base.OnClosing(e);
    }

    private static FloatingIndicatorState StateFromName(string name) =>
        (name ?? "idle").Trim().ToLowerInvariant() switch
        {
            "preparing" => FloatingIndicatorState.Preparing,
            "recording" => FloatingIndicatorState.Recording,
            "transcribing" => FloatingIndicatorState.Transcribing,
            "success" => FloatingIndicatorState.Success,
            "error" => FloatingIndicatorState.Error,
            _ => FloatingIndicatorState.Idle
        };
}
