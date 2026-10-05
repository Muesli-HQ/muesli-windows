using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.Controls;
using Muesli.Windows.WinUI.Services;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace Muesli.Windows.WinUI;

public sealed partial class MeetingLiveTranscriptWindow : Window, IDisposable
{
    private readonly WinUiMeetingContext _meetings;
    private readonly IUiDispatcher _dispatcher;
    private readonly IntPtr _hwnd;
    private readonly DispatcherTimer _exit = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private RectInt32? _indicator;
    private bool _visible, _hoverPresentation, _dismissed, _wasPaused;
    private int _disposed;
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointInt32 point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }

    public MeetingLiveTranscriptWindow(WinUiMeetingContext meetings, IUiDispatcher dispatcher, IClipboardService clipboard)
    {
        InitializeComponent();
        _meetings = meetings; _dispatcher = dispatcher;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = presenter.IsMinimizable = presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(false, false);
        }
        TranscriptFeed.OpenNotes = OpenNotes;
        _exit.Tick += (_, _) =>
        {
            if (!_hoverPresentation && !_dismissed) { _exit.Stop(); return; }
            if (PointerInIndicator() || (_visible && PointerInPanel())) return;
            _exit.Stop();
            _dismissed = false;
            if (_hoverPresentation) Hide();
        };
        _meetings.Changed += Meetings_Changed;
        Closed += (_, _) => _visible = false;
    }

    public void ShowForActiveMeeting()
    {
        if (!IsActive || _disposed != 0) return;
        _hoverPresentation = false; _dismissed = false;
        Show();
    }

    public void ShowOnIndicatorHover(RectInt32 frame)
    {
        _indicator = frame;
        _exit.Stop();
        if (_dismissed || !(_meetings.IsRecording || _meetings.IsPaused) || _disposed != 0) return;
        if (_visible && !_hoverPresentation) return;
        _hoverPresentation = true;
        Show();
        _exit.Start();
    }

    public void ScheduleHoverExit() { _exit.Stop(); _exit.Start(); }
    public void HideIfIdle() { if (!IsActive) { Hide(); _dismissed = false; } }
    private bool IsActive => _meetings.IsRecording || _meetings.IsPaused || _meetings.IsBusy;
    private void Show()
    {
        if (!_visible) { Position(); AppWindow.Show(false); _visible = true; }
        UpdateContent();
    }
    private void Hide() { AppWindow.Hide(); _visible = false; }
    private void Meetings_Changed(object? sender, EventArgs e) => _dispatcher.TryEnqueue(UpdateContent);
    private void UpdateContent()
    {
        if (_disposed != 0) return;
        if (!IsActive) { HideIfIdle(); return; }
        if (_hoverPresentation && (_wasPaused != _meetings.IsPaused || !App.Settings.Load().ShowLiveWaveformOnHover)) Hide();
        _wasPaused = _meetings.IsPaused;
        var capture = _meetings.CaptureSnapshot;
        StateLabel.Text = _meetings.IsPaused ? "Paused" : _meetings.IsRecording ? "Live" : "Saving";
        StateDot.Fill = (Brush)Application.Current.Resources[_meetings.IsPaused ? "MuesliTextTertiaryBrush" : "MuesliSuccessBrush"];
        WarningIcon.Visibility = capture.HealthWarnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(WarningIcon, string.Join(" ", capture.HealthWarnings));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(WarningIcon, string.Join(" ", capture.HealthWarnings));
        TranscriptFeed.Update(_meetings.LatestLiveSnapshot, _meetings.LiveTranscript ?? "");
        CopyButton.IsEnabled = TranscriptFeed.CopyText.Length > 0;
    }
    private void Position()
    {
        // Moving first resolves the target monitor's DPI before sizing the panel.
        if (_indicator is { } indicator) AppWindow.Move(new PointInt32(indicator.X, indicator.Y));
        var scale = Math.Max(1d, GetDpiForWindow(_hwnd) / 96d);
        var width = (int)Math.Round(360 * scale); var height = (int)Math.Round(320 * scale);
        var anchor = _indicator;
        var display = anchor is { } rect ? DisplayArea.GetFromPoint(new PointInt32(rect.X, rect.Y), DisplayAreaFallback.Nearest)
            : DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        // Physical pixels cross the companion pipe; only the compact panel's DIP size is scaled.
        var x = anchor is { } a ? (a.X - work.X >= width || a.X - work.X >= work.X + work.Width - a.X - a.Width ? a.X - width : a.X + a.Width)
            : work.X + work.Width - width - 8;
        var y = anchor is { } b ? b.Y + b.Height / 2 - height / 2 : work.Y + work.Height - height - 8;
        AppWindow.MoveAndResize(new RectInt32(Math.Clamp(x, work.X + 8, Math.Max(work.X + 8, work.X + work.Width - width - 8)),
            Math.Clamp(y, work.Y + 8, Math.Max(work.Y + 8, work.Y + work.Height - height - 8)), width, height));
    }
    private bool PointerInIndicator() => _indicator is { } r && GetCursorPos(out var p)
        && p.X >= r.X && p.X < r.X + r.Width && p.Y >= r.Y && p.Y < r.Y + r.Height;
    // Inactive WinUI windows can miss pointer transitions. Match macOS's screen-frame hit test.
    private bool PointerInPanel() => GetWindowRect(_hwnd, out var r) && GetCursorPos(out var p)
        && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    private void Panel_PointerEntered(object sender, PointerRoutedEventArgs e) => _exit.Start();
    private void Panel_PointerExited(object sender, PointerRoutedEventArgs e) => ScheduleHoverExit();
    private void Dismiss_Click(object sender, RoutedEventArgs e) { _dismissed = true; Hide(); ScheduleHoverExit(); }
    private void DismissAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { args.Handled = true; _dismissed = true; Hide(); ScheduleHoverExit(); }
    private void OpenNotes_Click(object sender, RoutedEventArgs e) => OpenNotes();
    private void OpenNotes() { Hide(); if (App.Window is MainWindow main) main.ShowDashboard("meetings"); }
    private async void Copy_Click(object sender, RoutedEventArgs e) => await LiveTranscriptFeed.CopyAsync(CopyButton, TranscriptFeed.CopyText);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _exit.Stop(); _meetings.Changed -= Meetings_Changed; Close();
    }
}
