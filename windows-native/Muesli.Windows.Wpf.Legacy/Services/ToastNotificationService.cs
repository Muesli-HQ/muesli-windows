using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace Muesli.Windows.Services;

public sealed class ToastNotificationService : IDisposable
{
    private const double CompactIdleWidth = 44;
    private const double CompactIdleHeight = 28;
    private const double DragThreshold = 4;
    private const double ScreenEdgeMargin = 8;
    private static readonly MediaColor GlassColor = MediaColor.FromRgb(30, 30, 46);
    private static readonly MediaColor DefaultRecordingAccent = MediaColor.FromRgb(30, 30, 46);
    private readonly DispatcherTimer _timer = new();
    private Window? _window;
    private bool _disposed;
    private string _idleHotkey = "F8";
    private bool _idleVisible;
    private bool _showIdleIndicator = true;
    private bool _hovered;
    private bool _suppressIdleHoverUntilMouseLeaves;
    private System.Windows.Point? _dragStart;
    private double _dragWindowLeft;
    private double _dragWindowTop;
    private bool _dragged;
    private string _indicatorAnchor = "Middle Right";
    private ToastState _currentState = ToastState.Idle;
    private IndicatorOwner _liveOwner = IndicatorOwner.None;
    private MediaColor _recordingAccent = DefaultRecordingAccent;
    private bool _meetingPaused;
    private Func<Task>? _stopRecording;
    private Func<Task>? _cancelRecording;
    private Func<Task>? _pauseOrResumeRecording;
    private double? _savedLeft;
    private double? _savedTop;
    private readonly List<Border> _recordingBars = [];
    private static ImageSource? _muesliGlyph;

    public event EventHandler<IndicatorPositionChangedEventArgs>? PositionChanged;
    public event EventHandler<bool>? HoverChanged;

    public ToastNotificationService()
    {
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            if (_disposed)
            {
                return;
            }
            if (_liveOwner is IndicatorOwner.Dictation or IndicatorOwner.Meeting or IndicatorOwner.ComputerUse)
            {
                return;
            }

            _liveOwner = IndicatorOwner.None;
            if (_idleVisible)
            {
                ShowIdle(_idleHotkey);
            }
            else
            {
                _window?.Hide();
            }
        };
    }

    public void ShowIdle(string hotkey)
    {
        _idleHotkey = string.IsNullOrWhiteSpace(hotkey) ? "F8" : hotkey;
        if (!_showIdleIndicator)
        {
            _idleVisible = false;
            _timer.Stop();
            if (_currentState == ToastState.Idle)
            {
                _window?.Hide();
            }
            return;
        }

        _idleVisible = true;
        _timer.Stop();
        _hovered = false;
        _suppressIdleHoverUntilMouseLeaves = true;
        ShowIndicator(ToastState.Idle, "Muesli", $"Hold {_idleHotkey} to dictate", 0);
    }

    public void SetIdleIndicatorVisible(bool visible, bool showNow = true)
    {
        _showIdleIndicator = visible;
        if (visible)
        {
            if (showNow)
            {
                ShowIdle(_idleHotkey);
            }
            return;
        }

        _idleVisible = false;
        if (_currentState == ToastState.Idle)
        {
            _timer.Stop();
            _window?.Hide();
        }
    }

    public void ConfigureActions(
        Func<Task> stopRecording,
        Func<Task> cancelRecording,
        Func<Task>? pauseOrResumeRecording = null)
    {
        _stopRecording = stopRecording;
        _cancelRecording = cancelRecording;
        _pauseOrResumeRecording = pauseOrResumeRecording;
    }

    public void SetRecordingAccent(string? hex)
    {
        _recordingAccent = ParseAccent(hex) ?? DefaultRecordingAccent;
        if (_window?.IsVisible == true && _currentState == ToastState.Recording)
        {
            ShowLive(_liveOwner == IndicatorOwner.None ? IndicatorOwner.Dictation : _liveOwner, "Recording", "", ToastState.Recording);
        }
    }

    public void SetMeetingPaused(bool paused) => _meetingPaused = paused;

    public Rect IndicatorScreenBounds()
    {
        if (_window is null || !_window.IsVisible)
        {
            return Rect.Empty;
        }

        return new Rect(_window.Left, _window.Top, _window.Width, _window.Height);
    }

    public IndicatorOwner LiveOwner => _liveOwner;
    public ToastState CurrentState => _currentState;

    public void ShowLive(IndicatorOwner owner, string title, string message, ToastState state)
    {
        if (owner is IndicatorOwner.None or IndicatorOwner.Transient)
        {
            Show(title, message, state);
            return;
        }

        if (!CanTakeLive(owner))
        {
            return;
        }

        _liveOwner = owner;
        ShowIndicator(state, title, message, 0);
    }

    public void ReleaseLive(IndicatorOwner owner)
    {
        if (_liveOwner != owner && _liveOwner != IndicatorOwner.None)
        {
            return;
        }

        _liveOwner = IndicatorOwner.None;
        _meetingPaused = false;
        if (_idleVisible)
        {
            ShowIdle(_idleHotkey);
            return;
        }

        _timer.Stop();
        _window?.Hide();
        _currentState = ToastState.Idle;
    }

    private bool CanTakeLive(IndicatorOwner owner) =>
        owner == _liveOwner
        || _liveOwner == IndicatorOwner.None
        || Priority(owner) >= Priority(_liveOwner);

    private static int Priority(IndicatorOwner owner) => owner switch
    {
        IndicatorOwner.Dictation or IndicatorOwner.Meeting or IndicatorOwner.ComputerUse => 3,
        IndicatorOwner.Background => 2,
        IndicatorOwner.Transient => 1,
        _ => 0
    };

    public void SetSavedPosition(double? left, double? top)
    {
        _savedLeft = left is not null && double.IsFinite(left.Value) ? left : null;
        _savedTop = top is not null && double.IsFinite(top.Value) ? top : null;
    }

    public void SetIndicatorAnchor(string anchor, bool clearCustomPosition)
    {
        _indicatorAnchor = string.IsNullOrWhiteSpace(anchor) ? "Middle Right" : anchor;
        if (clearCustomPosition)
        {
            _savedLeft = null;
            _savedTop = null;
        }

        if (_window?.IsVisible == true)
        {
            PositionWindow(_window);
        }
    }

    public void Show(string title, string message, ToastState state, int durationMs = 2200)
    {
        var dispatcher = _window?.Dispatcher ?? System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => Show(title, message, state, durationMs), DispatcherPriority.Send);
            return;
        }

        if (state is ToastState.Preparing or ToastState.Recording or ToastState.Transcribing)
        {
            if (durationMs <= 0)
            {
                ShowLive(IndicatorOwner.Background, title, message, state);
                return;
            }

            if (_liveOwner is IndicatorOwner.Dictation or IndicatorOwner.Meeting or IndicatorOwner.ComputerUse)
            {
                return;
            }

            _timer.Stop();
            ShowIndicator(state, title, message, durationMs);
            return;
        }

        if (_liveOwner is IndicatorOwner.Dictation or IndicatorOwner.Meeting or IndicatorOwner.ComputerUse)
        {
            return;
        }

        if (state is ToastState.Success or ToastState.Error)
        {
            if (_liveOwner == IndicatorOwner.Background)
            {
                _liveOwner = IndicatorOwner.None;
            }

            durationMs = durationMs <= 0
                ? (state == ToastState.Error ? 3600 : 2200)
                : durationMs;
        }

        _timer.Stop();
        ShowIndicator(state, title, message, durationMs);
    }

    public void Hide()
    {
        _timer.Stop();
        if (_idleVisible)
        {
            ShowIdle(_idleHotkey);
            return;
        }

        _window?.Hide();
    }

    public void UpdateRecordingLevel(float peak)
    {
        if (_window is null || _currentState != ToastState.Recording || _recordingBars.Count == 0)
        {
            return;
        }

        if (!_window.Dispatcher.CheckAccess())
        {
            _window.Dispatcher.BeginInvoke(() => UpdateRecordingLevel(peak));
            return;
        }

        var normalized = Math.Clamp(Math.Sqrt(Math.Max(0, peak) * 8), 0.12, 1.0);
        var weights = new[] { 0.62, 1.0, 0.84, 0.52 };
        for (var index = 0; index < _recordingBars.Count; index++)
        {
            _recordingBars[index].Height = 3 + normalized * 9 * weights[index];
        }
    }

    public void Dispose()
    {
        // Set before stopping the timer: Stop() does not cancel a tick already queued on the
        // dispatcher, so the handler can still run once after this point and must no-op.
        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _timer.Stop();
        _recordingBars.Clear();
        if (_window is not null)
        {
            _window.Closed -= OnIndicatorWindowClosed;
            _window.Close();
        }
        _window = null;
    }

    private void OnIndicatorWindowClosed(object? sender, EventArgs e)
    {
        if (sender is Window closed)
        {
            closed.Closed -= OnIndicatorWindowClosed;
        }
        if (ReferenceEquals(_window, sender))
        {
            _window = null;
        }
    }

    private void ShowIndicator(ToastState state, string title, string message, int durationMs)
    {
        if (state == ToastState.Idle && !_showIdleIndicator)
        {
            return;
        }

        if (_disposed)
        {
            return;
        }

        if (_window is null)
        {
            _window = CreateWindow();
            // WPF closes every window during application shutdown, which would otherwise leave this
            // field pointing at a closed instance. Show() on a closed Window throws, and from a
            // DispatcherTimer tick that surfaces as an unhandled UI exception.
            _window.Closed += OnIndicatorWindowClosed;
        }
        EnsureMouseHandlers(_window);
        if (state != ToastState.Idle)
        {
            _hovered = false;
            _suppressIdleHoverUntilMouseLeaves = true;
        }

        _currentState = state;

        var size = IndicatorSizeFor(state, _hovered);
        _window.Width = size.Width;
        _window.Height = size.Height;
        _window.Content = CreateContent(state, title, message, _hovered);
        _window.UpdateLayout();
        if (!_window.IsVisible)
        {
            // Establish the Window's own PresentationSource before any mixed-DPI screen conversion.
            // Zero opacity prevents a one-frame flash at WPF's provisional location.
            _window.Opacity = 0;
            _window.Show();
            PositionWindow(_window);
            _window.Opacity = 1;
        }
        else
        {
            PositionWindow(_window);
        }

        if (durationMs > 0)
        {
            _timer.Interval = TimeSpan.FromMilliseconds(durationMs);
            _timer.Start();
        }
    }

    private static Window CreateWindow()
    {
        return new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = MediaBrushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Focusable = false,
            ShowActivated = false,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Name = "FloatingIndicator",
            Title = "Muesli indicator",
            Left = 0,
            Top = 0
        };
    }

    private void EnsureMouseHandlers(Window window)
    {
        if (window.Tag as string == "muesli-indicator-handlers")
        {
            return;
        }

        window.Tag = "muesli-indicator-handlers";
        window.MouseEnter += (_, _) =>
        {
            HoverChanged?.Invoke(this, true);
            if (!_idleVisible || _currentState != ToastState.Idle || _suppressIdleHoverUntilMouseLeaves)
            {
                return;
            }

            _hovered = true;
            ShowIndicator(ToastState.Idle, "Muesli", $"Hold {_idleHotkey} to dictate", 0);
        };
        window.MouseLeave += (_, _) =>
        {
            HoverChanged?.Invoke(this, false);
            if (!_idleVisible)
            {
                return;
            }

            _suppressIdleHoverUntilMouseLeaves = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_window?.IsMouseOver == true)
                {
                    return;
                }

                _hovered = false;
                ShowIndicator(ToastState.Idle, "Muesli", $"Hold {_idleHotkey} to dictate", 0);
            };
            timer.Start();
        };
        window.MouseLeftButtonDown += (_, args) =>
        {
            _dragStart = ScreenPixelsToDesktopDips(window, window.PointToScreen(args.GetPosition(window)));
            _dragWindowLeft = window.Left;
            _dragWindowTop = window.Top;
            _dragged = false;
            window.CaptureMouse();
        };
        window.MouseMove += (_, args) =>
        {
            if (_dragStart is null || args.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var screenPosition = window.PointToScreen(args.GetPosition(window));
            var position = ScreenPixelsToDesktopDips(window, screenPosition);
            var dx = position.X - _dragStart.Value.X;
            var dy = position.Y - _dragStart.Value.Y;
            if (!_dragged && Math.Sqrt(dx * dx + dy * dy) < DragThreshold)
            {
                return;
            }

            _dragged = true;
            var workArea = WorkAreaForScreenPoint(window, screenPosition);
            var clamped = ClampWindowPosition(
                workArea,
                new System.Windows.Size(window.Width, window.Height),
                new System.Windows.Point(_dragWindowLeft + dx, _dragWindowTop + dy));
            window.Left = Math.Round(clamped.X);
            window.Top = Math.Round(clamped.Y);
        };
        window.MouseLeftButtonUp += (_, args) =>
        {
            var x = args.GetPosition(window).X;
            _dragStart = null;
            window.ReleaseMouseCapture();
            if (_dragged)
            {
                _savedLeft = window.Left + window.Width / 2;
                _savedTop = window.Top + window.Height / 2;
                PositionChanged?.Invoke(this, new IndicatorPositionChangedEventArgs(_savedLeft.Value, _savedTop.Value));
                return;
            }

            if (_currentState is ToastState.Recording or ToastState.Preparing)
            {
                if (_liveOwner == IndicatorOwner.Meeting)
                {
                    _ = x < 30
                        ? (_pauseOrResumeRecording?.Invoke() ?? _cancelRecording?.Invoke() ?? Task.CompletedTask)
                        : (_stopRecording?.Invoke() ?? Task.CompletedTask);
                    return;
                }

                _ = x < 30
                    ? (_cancelRecording?.Invoke() ?? Task.CompletedTask)
                    : (_stopRecording?.Invoke() ?? Task.CompletedTask);
                return;
            }

            if (_currentState == ToastState.Transcribing)
            {
                _ = _cancelRecording?.Invoke() ?? Task.CompletedTask;
            }
        };
        window.MouseRightButtonUp += (_, _) =>
        {
            if (_currentState is ToastState.Recording or ToastState.Preparing or ToastState.Transcribing)
            {
                _ = _cancelRecording?.Invoke() ?? Task.CompletedTask;
            }
        };
    }

    private static System.Windows.Size IndicatorSizeFor(ToastState state, bool hovered)
    {
        return state switch
        {
            ToastState.Idle => hovered ? new System.Windows.Size(220, 36) : new System.Windows.Size(CompactIdleWidth, CompactIdleHeight),
            ToastState.Preparing or ToastState.Recording => new System.Windows.Size(76, 22),
            ToastState.Transcribing => new System.Windows.Size(120, 32),
            ToastState.Success => new System.Windows.Size(220, 36),
            ToastState.Error => new System.Windows.Size(260, 36),
            _ => new System.Windows.Size(44, 28)
        };
    }

    private FrameworkElement CreateContent(ToastState state, string title, string message, bool hovered)
    {
        return state switch
        {
            ToastState.Idle => CreateIdlePill(message, hovered),
            ToastState.Preparing => CreatePreparingPill(),
            ToastState.Recording => CreateRecordingPill(),
            ToastState.Transcribing => CreateTranscribingPill(string.IsNullOrWhiteSpace(title) ? "Transcribing" : title),
            ToastState.Success => CreateStatusPill(title, MediaColor.FromRgb(52, 211, 153), 18),
            ToastState.Error => CreateStatusPill(title, MediaColor.FromRgb(248, 113, 113), 18),
            _ => CreateIdlePill(message, hovered)
        };
    }

    private static FrameworkElement CreateIdlePill(string message, bool hovered)
    {
        var root = CreatePill(GlassColor, hovered ? 0.72 : 0.44, hovered ? 18 : 14, hovered ? 0.14 : 0.22);
        root.Width = hovered ? 220 : CompactIdleWidth;
        root.Height = hovered ? 36 : CompactIdleHeight;
        root.Padding = new Thickness(hovered ? 12 : 0, 0, hovered ? 12 : 0, 0);

        if (!hovered)
        {
            root.Child = CreateMuesliGlyph(18, new Thickness(0));
            return root;
        }

        root.Child = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                CreateMuesliGlyph(15, new Thickness(0, 0, 7, 0)),
                CreateInlineText(message, 11, FontWeights.SemiBold, new Thickness(0), 0.75)
            }
        };
        return root;
    }

    private FrameworkElement CreatePreparingPill()
    {
        var root = CreatePill(GlassColor, 0.62, 11, 0.16);
        root.Width = 76;
        root.Height = 22;
        root.Padding = new Thickness(10, 0, 10, 0);
        var waveform = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _recordingBars.Clear();
        var heights = new[] { 4d, 8d, 11d, 7d, 5d };
        for (var i = 0; i < heights.Length; i++)
        {
            var bar = new Border
            {
                Width = 2.5,
                Height = heights[i],
                Margin = new Thickness(1.5, 0, 1.5, 0),
                CornerRadius = new CornerRadius(1.25),
                Background = new SolidColorBrush(MediaColor.FromArgb(180, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };
            _recordingBars.Add(bar);
            waveform.Children.Add(bar);
        }

        root.Child = waveform;
        return root;
    }

    private FrameworkElement CreateRecordingPill()
    {
        var root = CreatePill(_recordingAccent, 0.88, 11, 0.16);
        root.Width = 76;
        root.Height = 22;
        root.Padding = new Thickness(7, 0, 8, 0);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });

        var leftGlyph = _liveOwner == IndicatorOwner.Meeting
            ? (_meetingPaused ? "▶" : "❚❚")
            : "x";
        var cancel = CreateInlineText(leftGlyph, leftGlyph.Length > 1 ? 7 : 8, FontWeights.SemiBold, new Thickness(0, -1, 0, 0), 0.7);
        Grid.SetColumn(cancel, 0);
        grid.Children.Add(cancel);

        var waveform = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _recordingBars.Clear();
        var heights = new[] { 5d, 10d, 10d, 5d };
        for (var i = 0; i < heights.Length; i++)
        {
            var bar = new Border
            {
                Width = 2.5,
                Height = heights[i],
                Margin = new Thickness(1.5, 0, 1.5, 0),
                CornerRadius = new CornerRadius(1.25),
                Background = new SolidColorBrush(MediaColor.FromArgb(220, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };
            _recordingBars.Add(bar);
            waveform.Children.Add(bar);
        }

        Grid.SetColumn(waveform, 1);
        grid.Children.Add(waveform);

        var stop = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(1),
            Background = new SolidColorBrush(MediaColor.FromArgb(217, 255, 255, 255)),
            HorizontalAlignment = WpfHorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(stop, 2);
        grid.Children.Add(stop);

        root.Child = grid;
        return root;
    }

    private static FrameworkElement CreateTranscribingPill(string title)
    {
        var root = CreatePill(GlassColor, 0.62, 16, 0.16);
        root.Width = 120;
        root.Height = 32;
        root.Padding = new Thickness(12, 0, 12, 0);
        root.Child = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                CreateInlineText("\uE895", 12, FontWeights.SemiBold, new Thickness(0, 0, 5, 0)),
                CreateInlineText(title, 11, FontWeights.SemiBold, new Thickness(0), 0.82)
            }
        };
        return root;
    }

    private static FrameworkElement CreateStatusPill(string title, MediaColor accent, double radius)
    {
        var root = CreatePill(GlassColor, 0.72, radius, 0.14);
        root.Width = title.Length > 24 ? 260 : 220;
        root.Height = 36;
        root.Padding = new Thickness(12, 0, 12, 0);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(99),
            Background = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var label = CreateInlineText(title, 11, FontWeights.SemiBold, new Thickness(8, 0, 0, 0), 0.82);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        root.Child = grid;
        return root;
    }

    private static TextBlock CreateCenteredText(string text, double fontSize, FontWeight weight)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = weight,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static FrameworkElement CreateMuesliGlyph(double size, Thickness margin)
    {
        var source = GetMuesliGlyph();
        if (source is not null)
        {
            return new System.Windows.Controls.Image
            {
                Source = source,
                Width = size,
                Height = size,
                Margin = margin,
                Stretch = Stretch.Uniform,
                Opacity = 0.9,
                HorizontalAlignment = WpfHorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        return CreateInlineText("m", 13, FontWeights.Bold, margin, 0.9);
    }

    private static ImageSource? GetMuesliGlyph()
    {
        if (_muesliGlyph is not null)
        {
            return _muesliGlyph;
        }

        var assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", "menu_m_template@2x.png");
        if (!File.Exists(assetPath))
        {
            return null;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(assetPath, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        _muesliGlyph = image;
        return _muesliGlyph;
    }

    private static TextBlock CreateInlineText(string text, double fontSize, FontWeight weight, Thickness margin, double alpha = 1)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = weight,
            Margin = margin,
            Foreground = new SolidColorBrush(MediaColor.FromArgb((byte)(255 * alpha), 255, 255, 255)),
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static Border CreatePill(MediaColor color, double opacity, double radius, double borderOpacity)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(radius),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(MediaColor.FromArgb((byte)(255 * borderOpacity), 255, 255, 255)),
            Background = new SolidColorBrush(MediaColor.FromArgb((byte)(255 * opacity), color.R, color.G, color.B))
        };
    }

    private void PositionWindow(Window window)
    {
        if (_indicatorAnchor.Equals("Custom", StringComparison.OrdinalIgnoreCase) &&
            _savedLeft is not null &&
            _savedTop is not null)
        {
            var savedCenterInPixels = DesktopDipsToScreenPixels(window, new System.Windows.Point(_savedLeft.Value, _savedTop.Value));
            var workArea = WorkAreaForScreenPoint(window, savedCenterInPixels);
            var clamped = ClampWindowPosition(
                workArea,
                new System.Windows.Size(window.Width, window.Height),
                new System.Windows.Point(_savedLeft.Value - window.Width / 2, _savedTop.Value - window.Height / 2));
            window.Left = Math.Round(clamped.X);
            window.Top = Math.Round(clamped.Y);
            var repairedLeft = window.Left + window.Width / 2;
            var repairedTop = window.Top + window.Height / 2;
            if (Math.Abs(repairedLeft - _savedLeft.Value) > 0.5 || Math.Abs(repairedTop - _savedTop.Value) > 0.5)
            {
                _savedLeft = repairedLeft;
                _savedTop = repairedTop;
                PositionChanged?.Invoke(this, new IndicatorPositionChangedEventArgs(repairedLeft, repairedTop));
            }
            return;
        }

        // Anchored indicators follow the monitor containing the cursor; custom positions retain their saved-monitor clamp above.
        var area = WindowPlacementService.GetWorkAreaForCursor(window);
        var anchor = _indicatorAnchor.Trim();
        var left = anchor switch
        {
            "Top Left" or "Bottom Left" or "Middle Left" => area.Left + 18,
            "Top Right" or "Bottom Right" or "Middle Right" => area.Right - window.Width - 18,
            "Top Center" or "Bottom Center" => CenteredLeft(area, window.Width),
            _ => area.Right - window.Width - 18
        };
        var top = anchor switch
        {
            "Bottom Left" or "Bottom Center" or "Bottom Right" => area.Bottom - window.Height - 18,
            "Middle Left" or "Middle Right" => area.Top + (area.Height - window.Height) / 2,
            _ => area.Top + 18
        };

        window.Left = Math.Round(left);
        window.Top = Math.Round(top);
    }

    internal static System.Windows.Point ClampWindowPosition(
        Rect workArea,
        System.Windows.Size windowSize,
        System.Windows.Point requested,
        double margin = ScreenEdgeMargin)
    {
        var minimumLeft = workArea.Left + margin;
        var maximumLeft = workArea.Right - windowSize.Width - margin;
        var minimumTop = workArea.Top + margin;
        var maximumTop = workArea.Bottom - windowSize.Height - margin;
        var left = maximumLeft >= minimumLeft
            ? Math.Clamp(requested.X, minimumLeft, maximumLeft)
            : workArea.Left + Math.Max(0, (workArea.Width - windowSize.Width) / 2);
        var top = maximumTop >= minimumTop
            ? Math.Clamp(requested.Y, minimumTop, maximumTop)
            : workArea.Top + Math.Max(0, (workArea.Height - windowSize.Height) / 2);
        return new System.Windows.Point(left, top);
    }

    private static Rect WorkAreaForScreenPoint(Window window, System.Windows.Point screenPointPixels)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(
            (int)Math.Round(screenPointPixels.X),
            (int)Math.Round(screenPointPixels.Y)));
        var area = screen.WorkingArea;
        var topLeft = ScreenPixelsToDesktopDips(window, new System.Windows.Point(area.Left, area.Top));
        var bottomRight = ScreenPixelsToDesktopDips(window, new System.Windows.Point(area.Right, area.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private static System.Windows.Point ScreenPixelsToDesktopDips(Window window, System.Windows.Point screenPointPixels)
    {
        var relative = window.PointFromScreen(screenPointPixels);
        return new System.Windows.Point(window.Left + relative.X, window.Top + relative.Y);
    }

    private static System.Windows.Point DesktopDipsToScreenPixels(Window window, System.Windows.Point desktopPoint)
    {
        return window.PointToScreen(new System.Windows.Point(desktopPoint.X - window.Left, desktopPoint.Y - window.Top));
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs eventArgs)
    {
        var window = _window;
        if (window is null) return;
        window.Dispatcher.BeginInvoke(() =>
        {
            if (window.IsVisible) PositionWindow(window);
        });
    }

    private static double CenteredLeft(Rect area, double width)
    {
        return area.Left + area.Width / 2 - width / 2;
    }

    private static MediaColor? ParseAccent(string? hex)
    {
        var value = (hex ?? "").Trim().TrimStart('#');
        if (value.Length != 6)
        {
            return null;
        }

        try
        {
            var packed = Convert.ToUInt32(value, 16);
            return MediaColor.FromRgb(
                (byte)((packed >> 16) & 0xFF),
                (byte)((packed >> 8) & 0xFF),
                (byte)(packed & 0xFF));
        }
        catch (FormatException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}

public sealed class IndicatorPositionChangedEventArgs(double left, double top) : EventArgs
{
    public double Left { get; } = left;
    public double Top { get; } = top;
}

public enum ToastState
{
    Idle,
    Preparing,
    Recording,
    Transcribing,
    Success,
    Error
}

public enum IndicatorOwner
{
    None,
    Transient,
    Background,
    Dictation,
    Meeting,
    ComputerUse
}
