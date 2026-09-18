using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Muesli.Windows.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Muesli.Windows;

public partial class MeetingLiveTranscriptWindow : Window
{
    private readonly bool _showWaveformOnHover;
    private string _copyText = "";

    public MeetingLiveTranscriptWindow(bool showWaveformOnHover)
    {
        InitializeComponent();
        _showWaveformOnHover = showWaveformOnHover;
        Loaded += (_, _) => PositionOnActiveMonitor();
    }

    private void PositionOnActiveMonitor()
    {
        var area = WindowPlacementService.GetWorkAreaForCursor(this);
        Left = Math.Max(area.Left + 8, area.Right - ActualWidth - 20);
        Top = Math.Max(area.Top + 8, area.Bottom - ActualHeight - 20);
    }

    public void Update(LiveTranscriptSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Update(snapshot));
            return;
        }

        RebuildBubbles(snapshot);
        OthersPartialText.Text = snapshot.PartialSystem;
        YouPartialText.Text = snapshot.PartialMicrophone;
        OthersPartialBorder.Visibility = string.IsNullOrWhiteSpace(snapshot.PartialSystem) ? Visibility.Collapsed : Visibility.Visible;
        YouPartialBorder.Visibility = string.IsNullOrWhiteSpace(snapshot.PartialMicrophone) ? Visibility.Collapsed : Visibility.Visible;
        MicLevel.Value = snapshot.MicrophoneLevel;
        SystemLevel.Value = snapshot.SystemLevel;
        StatusText.Text = snapshot.DroppedPackets > 0
            ? $"Live · recovering {snapshot.DroppedPackets} dropped packet(s) from retained audio"
            : snapshot.QueuedPackets > 0 ? $"Live · {snapshot.QueuedPackets} queued" : "Live · speech-boundary commits";
        _copyText = string.Join(Environment.NewLine, new[]
        {
            snapshot.CommittedText,
            string.IsNullOrWhiteSpace(snapshot.PartialSystem) ? "" : $"Others (partial): {snapshot.PartialSystem}",
            string.IsNullOrWhiteSpace(snapshot.PartialMicrophone) ? "" : $"You (partial): {snapshot.PartialMicrophone}"
        }.Where(text => !string.IsNullOrWhiteSpace(text)));
        TranscriptScroll.ScrollToEnd();
    }

    private void RebuildBubbles(LiveTranscriptSnapshot snapshot)
    {
        CommittedHost.Children.Clear();
        var youFill = TryBrush("SurfaceSelectedBrush");
        var othersFill = TryBrush("BackgroundHoverBrush");
        var primary = TryBrush("TextPrimaryBrush");
        var tertiary = TryBrush("TextTertiaryBrush");

        foreach (var segment in snapshot.Committed.OrderBy(item => item.StartSample))
        {
            var isYou = segment.Channel == LiveTranscriptChannel.Microphone;
            var bubble = new Border
            {
                Background = isYou ? youFill : othersFill,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = isYou ? new Thickness(42, 0, 0, 8) : new Thickness(0, 0, 42, 8),
                HorizontalAlignment = isYou ? WpfHorizontalAlignment.Right : WpfHorizontalAlignment.Left
            };
            var row = new DockPanel { LastChildFill = true };
            var copy = new WpfButton
            {
                Content = "Copy",
                Style = TryFindResource("GhostButton") as Style,
                Padding = new Thickness(6, 0, 6, 0),
                Height = 22,
                Margin = new Thickness(8, 0, 0, 0),
                Tag = segment.Text,
                ToolTip = "Copy this utterance"
            };
            DockPanel.SetDock(copy, Dock.Right);
            copy.Click += CopyUtterance_Click;
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = isYou ? "You" : "Others",
                FontSize = 10,
                Foreground = tertiary,
                Margin = new Thickness(0, 0, 0, 3)
            });
            stack.Children.Add(new TextBlock
            {
                Text = segment.Text,
                FontSize = 13,
                Foreground = primary,
                TextWrapping = TextWrapping.Wrap
            });
            row.Children.Add(copy);
            row.Children.Add(stack);
            bubble.Child = row;
            CommittedHost.Children.Add(bubble);
        }
    }

    private System.Windows.Media.Brush? TryBrush(string key) =>
        TryFindResource(key) as System.Windows.Media.Brush
        ?? System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush;

    private void CopyUtterance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton { Tag: string text } && !string.IsNullOrWhiteSpace(text))
            System.Windows.Clipboard.SetText(text);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_copyText)) System.Windows.Clipboard.SetText(_copyText);
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e) => Hide();
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) { if (_showWaveformOnHover) WaveformPanel.Visibility = Visibility.Visible; }
    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) { if (_showWaveformOnHover) WaveformPanel.Visibility = Visibility.Collapsed; }
}
