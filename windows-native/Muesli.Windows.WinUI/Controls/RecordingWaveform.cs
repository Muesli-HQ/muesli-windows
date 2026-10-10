using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace Muesli.Windows.WinUI.Controls;

/// <summary>
/// macOS RecordingWaveformView: the waveform is the seek bar. Bars are centred, played bars use the
/// accent and the rest a faint tertiary, a 2-point playhead marks the position, and clicking or
/// dragging seeks. Left/Right step 5% for keyboard users.
/// </summary>
public sealed class RecordingWaveform : UserControl
{
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
        nameof(Peaks), typeof(IReadOnlyList<double>), typeof(RecordingWaveform),
        new PropertyMetadata(null, (d, _) => ((RecordingWaveform)d).Rebuild()));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(RecordingWaveform),
        new PropertyMetadata(0d, (d, _) => ((RecordingWaveform)d).Recolor()));

    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Rectangle _playhead = new() { Width = 2, RadiusX = 1, RadiusY = 1 };
    private readonly List<Rectangle> _bars = [];

    public IReadOnlyList<double>? Peaks { get => (IReadOnlyList<double>?)GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    /// <summary>Raised with the requested position as a 0–1 fraction of the recording.</summary>
    public event EventHandler<double>? Seeked;

    public RecordingWaveform()
    {
        Content = _canvas;
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        AutomationProperties.SetName(this, "Seek recording");
        SizeChanged += (_, _) => Rebuild();
        ActualThemeChanged += (_, _) => Recolor();
        _canvas.PointerPressed += (_, e) => { _canvas.CapturePointer(e.Pointer); SeekTo(e); };
        _canvas.PointerMoved += (_, e) => { if (e.Pointer.IsInContact) SeekTo(e); };
        _canvas.PointerReleased += (_, e) => _canvas.ReleasePointerCapture(e.Pointer);
        KeyDown += (_, e) =>
        {
            var step = e.Key switch { VirtualKey.Left => -0.05, VirtualKey.Right => 0.05, _ => 0 };
            if (step == 0) return;
            e.Handled = true;
            Seeked?.Invoke(this, Math.Clamp(Progress + step, 0, 1));
        };
    }

    private void SeekTo(PointerRoutedEventArgs e)
    {
        if (ActualWidth <= 0) return;
        Seeked?.Invoke(this, Math.Clamp(e.GetCurrentPoint(_canvas).Position.X / ActualWidth, 0, 1));
    }

    private void Rebuild()
    {
        _canvas.Children.Clear();
        _bars.Clear();
        var peaks = Peaks;
        double width = ActualWidth, height = ActualHeight;
        if (peaks is not { Count: > 0 } || width <= 0 || height <= 0) return;

        const double spacing = 2;
        var count = Math.Min(peaks.Count, Math.Max(48, (int)(width / 4)));
        var barWidth = Math.Max(1, (width - spacing * (count - 1)) / count);
        for (var index = 0; index < count; index++)
        {
            // Each visible bar shows the loudest peak in its slice of the source.
            var start = index * peaks.Count / count;
            var end = Math.Max(start + 1, (index + 1) * peaks.Count / count);
            var peak = 0d;
            for (var source = start; source < end; source++) peak = Math.Max(peak, peaks[source]);
            var barHeight = Math.Max(4, height * Math.Clamp(peak, 0, 1));
            var bar = new Rectangle { Width = barWidth, Height = barHeight, RadiusX = barWidth / 2, RadiusY = barWidth / 2 };
            Canvas.SetLeft(bar, index * (barWidth + spacing));
            Canvas.SetTop(bar, (height - barHeight) / 2);
            _bars.Add(bar);
            _canvas.Children.Add(bar);
        }
        _playhead.Height = height;
        _canvas.Children.Add(_playhead);
        Recolor();
    }

    private void Recolor()
    {
        if (_bars.Count == 0) return;
        var accent = (Brush)Application.Current.Resources["MuesliAccentBrush"];
        var rest = (Brush)Application.Current.Resources["MuesliTextTertiaryBrush"];
        var playedX = ActualWidth * Math.Clamp(Progress, 0, 1);
        foreach (var bar in _bars)
        {
            var played = Canvas.GetLeft(bar) <= playedX;
            bar.Fill = played ? accent : rest;
            bar.Opacity = played ? 1 : 0.24 / 0.56; // macOS: textTertiary at 24%; the brush already carries 56%.
        }
        _playhead.Fill = accent;
        Canvas.SetLeft(_playhead, Math.Max(0, Math.Min(playedX - 1, ActualWidth - 2)));
    }
}
