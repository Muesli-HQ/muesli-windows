using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.Services;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;

namespace Muesli.Windows.WinUI;

public sealed partial class MeetingLiveTranscriptWindow : Window, IDisposable
{
    private const double WidthDip = 440;
    private const double HeightDip = 420;

    private readonly WinUiMeetingContext _meetings;
    private readonly IUiDispatcher _dispatcher;
    private readonly IClipboardService _clipboard;
    private readonly IntPtr _hwnd;
    private bool _visible;
    private bool _dragging;
    private PointInt32 _dragOrigin;
    private PointInt32 _windowOrigin;
    private string _copyText = "";
    private string _lastTranscriptKey = "\u0001";
    private int _disposed;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    public MeetingLiveTranscriptWindow(
        WinUiMeetingContext meetings,
        IUiDispatcher dispatcher,
        IClipboardService clipboard)
    {
        InitializeComponent();
        _meetings = meetings;
        _dispatcher = dispatcher;
        _clipboard = clipboard;
        Title = "Live meeting transcript";
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ResizeDip(WidthDip, HeightDip);
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = true;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(false, false);
        }

        _meetings.Changed += Meetings_Changed;
        Closed += (_, _) => _visible = false;
        RootBorder.ActualThemeChanged += (_, _) => _dispatcher.TryEnqueue(UpdateContent);
        // This window is constructed at startup (App.xaml.cs:142) and stays hidden until a meeting
        // is recording. The unconditional Focus() that used to live here ran while the window was
        // still hidden, and a programmatic focus inside the app's own foreground thread moved the
        // OS foreground onto this invisible HWND: measured immediately after a clean launch,
        // GetForegroundWindow() returned "Live meeting transcript" (visible=False) instead of the
        // dashboard, so the first keystroke went to a window nobody could see. The overlay is
        // deliberately shown with AppWindow.Show(false) — it must never take activation — so the
        // correct behaviour is not to force focus at all; the Escape/copy accelerators become
        // reachable once the user clicks the overlay.
        RootBorder.Loaded += (_, _) =>
        {
            if (_visible) RootBorder.Focus(FocusState.Programmatic);
        };
    }

    public void ShowForActiveMeeting()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (!_visible)
        {
            PositionNearWorkArea();
            AppWindow.Show(false);
            _visible = true;
        }

        UpdateContent();
    }

    public void HideIfIdle()
    {
        if (_visible && !IsSessionSurfaceActive())
        {
            AppWindow.Hide();
            _visible = false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _meetings.Changed -= Meetings_Changed;
        Close();
    }

    private void Meetings_Changed(object? sender, EventArgs e) => _dispatcher.TryEnqueue(UpdateContent);

    private void UpdateContent()
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        var snapshot = _meetings.LatestLiveSnapshot;
        var capture = _meetings.CaptureSnapshot;
        var status = string.IsNullOrWhiteSpace(_meetings.CaptureStatus)
            ? "Waiting for speech…"
            : _meetings.CaptureStatus;
        if (snapshot is { DroppedPackets: > 0 })
        {
            status = $"Live · recovering {snapshot.DroppedPackets} dropped packet(s) from retained audio";
        }
        else if (snapshot is { QueuedPackets: > 0 })
        {
            status = $"Live · {snapshot.QueuedPackets} queued";
        }

        StatusText.Text = status;
        ToolTipService.SetToolTip(StatusText, status);
        ApplySessionState(capture);

        MicLevel.Value = snapshot?.MicrophoneLevel ?? _meetings.MicrophoneLevel;
        SystemLevel.Value = snapshot?.SystemLevel ?? _meetings.SystemLevel;
        var active = _meetings.IsRecording || _meetings.IsPaused || _meetings.IsBusy;
        WaveformPanel.Visibility = App.Settings.Load().ShowLiveWaveformOnHover || active
            ? Visibility.Visible
            : Visibility.Collapsed;

        RebuildTranscript(snapshot);
        CopyButton.IsEnabled = !string.IsNullOrWhiteSpace(_copyText);

        if (!IsSessionSurfaceActive())
        {
            HideIfIdle();
        }
    }

    private bool IsSessionSurfaceActive()
    {
        var state = _meetings.State;
        return _meetings.IsRecording
            || _meetings.IsPaused
            || _meetings.IsBusy
            || state is MeetingSessionState.Failed or MeetingSessionState.DegradedRecording;
    }

    private void ApplySessionState(MeetingCaptureSnapshot capture)
    {
        var warnings = capture.HealthWarnings;
        var failed = capture.State == MeetingSessionState.Failed
            || StatusText.Text.Contains("fail", StringComparison.OrdinalIgnoreCase);
        var degraded = capture.State == MeetingSessionState.DegradedRecording;
        var paused = _meetings.IsPaused || capture.IsPaused;
        var processing = _meetings.IsBusy && !_meetings.IsRecording && !paused;
        var recording = _meetings.IsRecording && !degraded;

        string label;
        string badgeBackground;
        string badgeForeground;
        string dotBrush;
        if (failed)
        {
            label = "ERROR";
            badgeBackground = "MuesliErrorMutedBrush";
            badgeForeground = "MuesliErrorBrush";
            dotBrush = "MuesliErrorBrush";
            SessionInfoBar.Severity = InfoBarSeverity.Error;
            SessionInfoBar.Title = "Live transcription error";
            SessionInfoBar.Message = CompactStatus(
                string.IsNullOrWhiteSpace(_meetings.CaptureStatus)
                    ? "The live session failed. Committed text below is unchanged."
                    : "Committed text below is unchanged. Hover the status line for the full capture message.");
            SessionInfoBar.IsOpen = true;
        }
        else if (degraded || warnings.Count > 0)
        {
            label = degraded ? "DEGRADED" : "WARNING";
            badgeBackground = "MuesliSurfaceRaisedBrush";
            badgeForeground = "MuesliWarningBrush";
            dotBrush = "MuesliWarningBrush";
            SessionInfoBar.Severity = InfoBarSeverity.Warning;
            SessionInfoBar.Title = degraded ? "Recording with a missing channel" : "Capture warning";
            SessionInfoBar.Message = CompactStatus(
                warnings.Count > 0
                    ? string.Join(" ", warnings)
                    : "Hover the status line for the full capture message.");
            SessionInfoBar.IsOpen = true;
        }
        else if (paused)
        {
            label = "PAUSED";
            badgeBackground = "MuesliSurfaceRaisedBrush";
            badgeForeground = "MuesliWarningBrush";
            dotBrush = "MuesliWarningBrush";
            SessionInfoBar.IsOpen = false;
        }
        else if (recording)
        {
            label = "RECORDING";
            badgeBackground = "MuesliAccentMutedBrush";
            badgeForeground = "MuesliAccentBrush";
            dotBrush = "MuesliAccentBrush";
            SessionInfoBar.IsOpen = false;
        }
        else if (processing)
        {
            label = "PROCESSING";
            badgeBackground = "MuesliSurfaceRaisedBrush";
            badgeForeground = "MuesliTextSecondaryBrush";
            dotBrush = "MuesliTextTertiaryBrush";
            SessionInfoBar.IsOpen = false;
        }
        else
        {
            label = "IDLE";
            badgeBackground = "MuesliSurfaceRaisedBrush";
            badgeForeground = "MuesliTextSecondaryBrush";
            dotBrush = "MuesliTextTertiaryBrush";
            SessionInfoBar.IsOpen = false;
        }

        StateLabel.Text = label;
        StateLabel.Foreground = Brush(badgeForeground);
        StateBadge.Background = Brush(badgeBackground);
        StateDot.Fill = Brush(dotBrush);
        AutomationProperties.SetName(StateBadge, $"Live transcript state: {label}");
    }

    private void RebuildTranscript(LiveTranscriptSnapshot? snapshot)
    {
        var liveText = _meetings.LiveTranscript ?? "";
        TranscriptText.Text = liveText;

        var hasPartials = snapshot is not null
            && (!string.IsNullOrWhiteSpace(snapshot.PartialMicrophone)
                || !string.IsNullOrWhiteSpace(snapshot.PartialSystem));
        var hasCommitted = snapshot is { Committed.Count: > 0 };
        var key = snapshot is null
            ? liveText
            : $"{snapshot.CommittedText}\n{snapshot.PartialMicrophone}\n{snapshot.PartialSystem}";

        YouPartialText.Text = snapshot?.PartialMicrophone ?? "";
        OthersPartialText.Text = snapshot?.PartialSystem ?? "";
        YouPartialBorder.Visibility = string.IsNullOrWhiteSpace(snapshot?.PartialMicrophone)
            ? Visibility.Collapsed
            : Visibility.Visible;
        OthersPartialBorder.Visibility = string.IsNullOrWhiteSpace(snapshot?.PartialSystem)
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (!string.Equals(key, _lastTranscriptKey, StringComparison.Ordinal))
        {
            _lastTranscriptKey = key;
            RebuildBubbles(snapshot);
            TranscriptScroll.UpdateLayout();
            TranscriptScroll.ChangeView(null, TranscriptScroll.ScrollableHeight, null, true);
        }

        var hasVisual = hasCommitted || hasPartials;
        var hasFallback = !hasVisual && !string.IsNullOrWhiteSpace(liveText);
        TranscriptText.Visibility = hasFallback ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hasVisual || hasFallback ? Visibility.Collapsed : Visibility.Visible;
        CommittedHost.Visibility = hasCommitted ? Visibility.Visible : Visibility.Collapsed;

        _copyText = string.Join(
            Environment.NewLine,
            new[]
            {
                snapshot?.CommittedText,
                string.IsNullOrWhiteSpace(snapshot?.PartialSystem) ? null : $"Others (partial): {snapshot!.PartialSystem}",
                string.IsNullOrWhiteSpace(snapshot?.PartialMicrophone) ? null : $"You (partial): {snapshot!.PartialMicrophone}",
                hasFallback ? liveText : null
            }.Where(text => !string.IsNullOrWhiteSpace(text)));
        if (string.IsNullOrWhiteSpace(_copyText))
        {
            _copyText = liveText;
        }
    }

    private void RebuildBubbles(LiveTranscriptSnapshot? snapshot)
    {
        CommittedHost.Children.Clear();
        if (snapshot is null || snapshot.Committed.Count == 0) return;

        foreach (var segment in snapshot.Committed.OrderBy(item => item.StartSample))
        {
            var isYou = segment.Channel == LiveTranscriptChannel.Microphone;
            var bubble = new Border
            {
                Background = Brush(isYou ? "MuesliSelectedBrush" : "MuesliHoverBrush"),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = isYou ? new Thickness(42, 0, 0, 8) : new Thickness(0, 0, 42, 8),
                HorizontalAlignment = isYou ? HorizontalAlignment.Right : HorizontalAlignment.Left
            };

            var copy = new Button
            {
                Content = "Copy",
                Style = (Style)Application.Current.Resources["MuesliGhostButtonStyle"],
                Padding = new Thickness(8, 2, 8, 2),
                MinHeight = 28,
                Tag = segment.Text,
                VerticalAlignment = VerticalAlignment.Top
            };
            ToolTipService.SetToolTip(copy, "Copy this utterance");
            AutomationProperties.SetName(copy, isYou ? "Copy your utterance" : "Copy others utterance");
            copy.Click += CopyUtterance_Click;

            var stack = new StackPanel { Spacing = 3 };
            stack.Children.Add(new TextBlock
            {
                Text = isYou ? "You" : "Others",
                Style = (Style)Application.Current.Resources["MuesliTimestampStyle"],
                FontSize = 10
            });
            stack.Children.Add(new TextBlock
            {
                Text = segment.Text,
                FontSize = 13,
                Foreground = Brush("MuesliTextPrimaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            });

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(copy, 1);
            row.Children.Add(stack);
            row.Children.Add(copy);
            bubble.Child = row;
            CommittedHost.Children.Add(bubble);
        }
    }

    private static string CompactStatus(string? text)
    {
        var value = string.IsNullOrWhiteSpace(text) ? "" : text.Trim();
        return value.Length <= 140 ? value : value[..137] + "…";
    }

    private static Brush Brush(string key) =>
        (Brush)Application.Current.Resources[key];

    private async void CopyUtterance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string text } button && !string.IsNullOrWhiteSpace(text))
        {
            await CopyAsync(button, text);
        }
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_copyText))
        {
            await CopyAsync(sender as FrameworkElement ?? CopyButton, _copyText);
        }
    }

    /// <summary>
    /// Prompt 9 CR-01. A clipboard held open by another process makes Clipboard.SetContent throw;
    /// from these async void handlers that reached App.UnhandledException and ended the shell.
    /// The session InfoBar is owned by capture state, so the failure is shown next to the button.
    /// </summary>
    private async Task CopyAsync(FrameworkElement anchor, string text)
    {
        try
        {
            await _clipboard.SetTextAsync(text);
        }
        catch (Exception exception)
        {
            var message = new TextBlock
            {
                Text = $"Could not copy: {WinUiClipboardService.DescribeFailure(exception)}",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 280,
            };
            AutomationProperties.SetLiveSetting(message, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
            new Flyout { Content = message }.ShowAt(anchor);
        }
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e) => Dismiss();

    private void DismissAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Dismiss();
    }

    private void RootBorder_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        Dismiss();
    }

    private void Dismiss()
    {
        AppWindow.Hide();
        _visible = false;
    }

    private void PositionNearWorkArea()
    {
        ResizeDip(WidthDip, HeightDip);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        var scale = Math.Max(1d, GetDpiForWindow(_hwnd) / 96d);
        var width = (int)Math.Round(WidthDip * scale);
        var height = (int)Math.Round(HeightDip * scale);
        AppWindow.Move(new PointInt32(
            work.X + Math.Max(8, work.Width - width - 20),
            work.Y + Math.Max(8, work.Height - height - 20)));
    }

    private void ResizeDip(double widthDip, double heightDip)
    {
        var scale = Math.Max(1d, GetDpiForWindow(_hwnd) / 96d);
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(widthDip * scale),
            (int)Math.Round(heightDip * scale)));
    }

    private void RootBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsInteractive(e.OriginalSource)) return;
        _dragging = RootBorder.CapturePointer(e.Pointer);
        if (!_dragging) return;
        var point = e.GetCurrentPoint(null).Position;
        _dragOrigin = ToScreen(point);
        _windowOrigin = AppWindow.Position;
    }

    private void RootBorder_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var point = ToScreen(e.GetCurrentPoint(null).Position);
        AppWindow.Move(new PointInt32(
            _windowOrigin.X + point.X - _dragOrigin.X,
            _windowOrigin.Y + point.Y - _dragOrigin.Y));
    }

    private void RootBorder_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        RootBorder.ReleasePointerCapture(e.Pointer);
    }

    private static bool IsInteractive(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is Button or ProgressBar or TextBox or ScrollViewer) return true;
            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private PointInt32 ToScreen(Point point)
    {
        var scale = Math.Max(1d, GetDpiForWindow(_hwnd) / 96d);
        var origin = AppWindow.Position;
        return new PointInt32(
            origin.X + (int)Math.Round(point.X * scale),
            origin.Y + (int)Math.Round(point.Y * scale));
    }
}
