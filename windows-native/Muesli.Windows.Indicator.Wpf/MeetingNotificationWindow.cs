using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Muesli.Windows.Core.Services;
using Muesli.Windows.Services;
using Color = System.Windows.Media.Color;

namespace Muesli.Windows.Indicator.Wpf;

/// <summary>The meeting prompt rendered by the existing WPF companion; actions stay in WinUI.</summary>
public sealed class MeetingNotificationWindow : Window
{
    private const double CardWidth = 440;
    private const double CardHeight = 76;
    private readonly IndicatorMeetingNotification _request;
    private readonly Action<IndicatorCommand> _send;
    private readonly MeetingNotificationCountdown _countdown = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Border _progress;
    private long _lastTick;
    private bool _closing;

    public string PromptId => _request.PromptId;

    public MeetingNotificationWindow(IndicatorMeetingNotification request, Action<IndicatorCommand> send)
    {
        _request = request;
        _send = send;
        Width = CardWidth + 24;
        Height = CardHeight + 18;
        Left = 0;
        Top = 0;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Title = "Muesli meeting notification";

        var highContrast = SystemParameters.HighContrast;
        var white = highContrast ? SystemColors.WindowTextBrush : Brushes.White;
        var muted = highContrast ? SystemColors.WindowTextBrush : Brush(140, 255, 255, 255);
        var card = new Border
        {
            Width = CardWidth,
            Height = CardHeight,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = highContrast ? SystemColors.WindowTextBrush : Brush(26, 255, 255, 255),
            Background = highContrast ? SystemColors.WindowBrush : Brush(247, 26, 26, 31),
            Effect = highContrast ? null : new DropShadowEffect
            {
                BlurRadius = 20, ShadowDepth = 3, Opacity = 0.4, Color = Colors.Black
            }
        };

        var content = new Grid { Margin = new Thickness(16, 0, 12, 0) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.Child = content;

        var icon = PlatformIcon(request);
        Grid.SetColumn(icon, 0);
        content.Children.Add(icon);

        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var title = new TextBlock
        {
            Text = request.Title, FontFamily = new FontFamily("Segoe UI"), FontSize = 14,
            FontWeight = FontWeights.SemiBold, Foreground = white,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap
        };
        AutomationProperties.SetAutomationId(title, "MeetingNotificationTitle");
        var subtitle = new TextBlock
        {
            Text = request.Subtitle, Margin = new Thickness(0, 4, 0, 0),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 12, Foreground = muted,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap
        };
        AutomationProperties.SetAutomationId(subtitle, "MeetingNotificationSubtitle");
        copy.Children.Add(title);
        copy.Children.Add(subtitle);
        Grid.SetColumn(copy, 1);
        content.Children.Add(copy);

        var action = BuildAction(request, highContrast);
        Grid.SetColumn(action, 2);
        content.Children.Add(action);

        _progress = new Border
        {
            Width = CardWidth - 2, Height = 3, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = highContrast ? SystemColors.HighlightBrush : Brush(204, 77, 153, 255),
            CornerRadius = new CornerRadius(0, 0, 2, 2)
        };
        AutomationProperties.SetAutomationId(_progress, "MeetingNotificationCountdown");

        var cardLayer = new Grid();
        cardLayer.Children.Add(card);
        cardLayer.Children.Add(_progress);
        Canvas.SetLeft(cardLayer, 12);
        Canvas.SetTop(cardLayer, 10);

        var close = CreateButton("×", 28, 28, highContrast ? SystemColors.WindowBrush : Brush(180, 0, 0, 0),
            white, 16, new CornerRadius(14));
        close.BorderBrush = highContrast ? SystemColors.WindowTextBrush : Brush(140, 255, 255, 255);
        close.BorderThickness = new Thickness(1);
        close.Click += (_, _) => Finish(IndicatorCommandType.MeetingNotificationDismiss);
        AutomationProperties.SetAutomationId(close, "MeetingNotificationDismiss");
        AutomationProperties.SetName(close, "Dismiss meeting notification");
        Canvas.SetLeft(close, 0);
        Canvas.SetTop(close, 0);

        var root = new Canvas { Background = Brushes.Transparent };
        root.Children.Add(cardLayer);
        root.Children.Add(close);
        root.MouseEnter += (_, _) => { _countdown.Pause(); _timer.Stop(); };
        root.MouseLeave += (_, _) => { _countdown.Resume(); _lastTick = Stopwatch.GetTimestamp(); if (!_closing) _timer.Start(); };
        AutomationProperties.SetAutomationId(root, "MeetingNotificationWindow");
        AutomationProperties.SetName(root, $"{request.Title}. {request.Subtitle}");
        Content = root;

        _timer.Tick += (_, _) => Tick();
        _countdown.Start(request.DismissAfterSeconds);
        Loaded += (_, _) =>
        {
            PlaceAtTopRight();
            _lastTick = Stopwatch.GetTimestamp();
            _timer.Start();
            if (SystemParameters.ClientAreaAnimation)
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            else Opacity = 1;
        };
        Opacity = 0;
    }

    private FrameworkElement BuildAction(IndicatorMeetingNotification request, bool highContrast)
    {
        if (!request.HasSplitAction)
        {
            var start = CreateButton(request.ActionLabel, 148, 36,
                highContrast ? SystemColors.HighlightBrush : Brush(255, 51, 128, 255),
                highContrast ? SystemColors.HighlightTextBrush : Brushes.White, 12, new CornerRadius(8));
            start.Click += (_, _) => Finish(IndicatorCommandType.MeetingNotificationAction, request.SingleAction);
            AutomationProperties.SetAutomationId(start, "MeetingNotificationPrimaryAction");
            AutomationProperties.SetName(start, request.ActionLabel);
            return start;
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var primaryAction = request.DefaultAction;
        var primary = CreateButton(primaryAction.Label(), 140, 36,
            highContrast ? SystemColors.HighlightBrush : Brush(255, 51, 184, 135),
            highContrast ? SystemColors.HighlightTextBrush : Brushes.White, 11, new CornerRadius(8, 0, 0, 8));
        primary.Click += (_, _) => Finish(IndicatorCommandType.MeetingNotificationAction, ToNotificationAction(primaryAction));
        AutomationProperties.SetAutomationId(primary, "MeetingNotificationPrimaryAction");
        AutomationProperties.SetName(primary, primaryAction.Label());
        var chevron = CreateButton("⌄", 28, 36,
            highContrast ? SystemColors.HighlightBrush : Brush(255, 38, 148, 107),
            highContrast ? SystemColors.HighlightTextBrush : Brushes.White, 15, new CornerRadius(0, 8, 8, 0));
        AutomationProperties.SetAutomationId(chevron, "MeetingNotificationChevron");
        AutomationProperties.SetName(chevron, "Other meeting actions");
        var menu = new ContextMenu();
        foreach (var alternative in primaryAction.AvailableAlternatives(true, true))
        {
            var item = new MenuItem { Header = alternative.Label() };
            item.Click += (_, _) => Finish(IndicatorCommandType.MeetingNotificationAction, ToNotificationAction(alternative));
            menu.Items.Add(item);
        }
        chevron.Click += (_, _) => { menu.PlacementTarget = chevron; menu.IsOpen = true; };
        actions.Children.Add(primary);
        actions.Children.Add(chevron);
        return actions;
    }

    private static MeetingNotificationAction ToNotificationAction(MeetingJoinDefaultAction action) => action switch
    {
        MeetingJoinDefaultAction.JoinAndRecord => MeetingNotificationAction.JoinAndRecord,
        MeetingJoinDefaultAction.JoinOnly => MeetingNotificationAction.JoinOnly,
        _ => MeetingNotificationAction.TranscribeOnly
    };

    private static FrameworkElement PlatformIcon(IndicatorMeetingNotification request)
    {
        var asset = request.Platform.Contains("Meet", StringComparison.OrdinalIgnoreCase) ? "google-meet.png" :
            request.Platform.Contains("Zoom", StringComparison.OrdinalIgnoreCase) ? "zoom-app.png" :
            request.Platform.Contains("Teams", StringComparison.OrdinalIgnoreCase) ? "teams.png" : null;
        if (asset is not null)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", asset);
            if (File.Exists(path))
            {
                var image = new Image
                {
                    Width = 30, Height = 30, Stretch = Stretch.Uniform,
                    Source = new BitmapImage(new Uri(path, UriKind.Absolute)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                AutomationProperties.SetName(image, request.Platform);
                return image;
            }
        }

        var accent = (Color)ColorConverter.ConvertFromString(request.AccentHex);
        return new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(45, accent.R, accent.G, accent.B)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = request.ShortLabel, FontSize = 9, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(accent),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private static Button CreateButton(string text, double width, double height, Brush background, Brush foreground,
        double fontSize, CornerRadius radius)
    {
        var button = new Button
        {
            Content = text, Width = width, Height = height, Background = background, Foreground = foreground,
            FontFamily = new FontFamily("Segoe UI"), FontSize = fontSize, FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, background);
        border.SetValue(Border.CornerRadiusProperty, radius);
        border.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Button.BorderBrush))
        { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        border.SetBinding(Border.BorderThicknessProperty, new Binding(nameof(Button.BorderThickness))
        { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        button.MouseEnter += (_, _) => button.Opacity = 0.85;
        button.MouseLeave += (_, _) => button.Opacity = 1;
        return button;
    }

    private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) =>
        new(Color.FromArgb(a, r, g, b));

    private void PlaceAtTopRight()
    {
        var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        var topLeft = PointFromScreen(new Point(area.Left, area.Top));
        var bottomRight = PointFromScreen(new Point(area.Right, area.Bottom));
        var left = Left + bottomRight.X - Width - 16;
        var top = Top + topLeft.Y + 16;
        Left = Math.Max(Left + topLeft.X, left);
        Top = Math.Min(top, Top + bottomRight.Y - Height);
    }

    private void Tick()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastTick, now);
        _lastTick = now;
        var expired = _countdown.Advance(elapsed);
        _progress.Width = (CardWidth - 2) * _countdown.Progress;
        if (expired) Finish(IndicatorCommandType.MeetingNotificationAutoDismiss);
    }

    private void Finish(string type, MeetingNotificationAction? action = null)
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        var command = new IndicatorCommand
        {
            Type = type,
            NotificationPromptId = PromptId,
            NotificationAction = action?.ToString()
        };
        void CloseAndSend()
        {
            Close();
            _send(command);
        }
        if (!SystemParameters.ClientAreaAnimation) { CloseAndSend(); return; }
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => CloseAndSend();
        BeginAnimation(OpacityProperty, fade);
    }

    public void CloseSilently()
    {
        _closing = true;
        _timer.Stop();
        Close();
    }
}
