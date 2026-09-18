using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MediaColor = System.Windows.Media.Color;

namespace Muesli.Windows.Services;

public sealed class DictionarySuggestionPromptService : IDisposable
{
    private Window? _window;
    private DispatcherTimer? _timer;

    public void Show(DictionarySuggestion suggestion, Action accept)
    {
        Close();
        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Width = 360,
            Height = 92,
            ShowActivated = false
        };
        var root = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(MediaColor.FromRgb(30, 30, 46)),
            BorderBrush = new SolidColorBrush(MediaColor.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12, 14, 12)
        };
        var panel = new DockPanel();
        var actions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        DockPanel.SetDock(actions, Dock.Bottom);
        var add = new System.Windows.Controls.Button { Content = "Add to dictionary", Margin = new Thickness(0, 8, 8, 0), Padding = new Thickness(10, 4, 10, 4) };
        var dismiss = new System.Windows.Controls.Button { Content = "Dismiss", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
        add.Click += (_, _) => { accept(); Close(); };
        dismiss.Click += (_, _) => Close();
        actions.Children.Add(add);
        actions.Children.Add(dismiss);
        panel.Children.Add(actions);
        panel.Children.Add(new TextBlock
        {
            Text = $"Add “{suggestion.Observed}” → “{suggestion.Replacement}” to your dictionary?",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 13
        });
        root.Child = panel;
        _window.Content = root;
        _window.Opacity = 0;
        _window.Show();
        var area = WindowPlacementService.GetWorkAreaForCursor(_window);
        _window.Left = area.Right - _window.Width - 18;
        _window.Top = area.Bottom - _window.Height - 18;
        _window.Opacity = 1;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += (_, _) => Close();
        _timer.Start();
    }

    public void Close()
    {
        _timer?.Stop();
        _timer = null;
        _window?.Close();
        _window = null;
    }

    public void Dispose() => Close();
}
