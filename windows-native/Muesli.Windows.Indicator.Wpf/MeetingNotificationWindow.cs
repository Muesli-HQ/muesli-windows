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
    // macOS MeetingNotificationController geometry: a 60-point card, 344 wide (a single-action card grows
    // to fit its text, up to 420), a 26-point platform icon, and 126 x 30 buttons inset 12 from the edge.
    private const double CardHeight = 60;
    private const double MinimumCardWidth = 344;
    private const double MaximumCardWidth = 420;
    private const double ButtonWidth = 126;
    private const double ButtonHeight = 30;
    private const double ChevronWidth = 24;
    private const double IconSize = 26;
    private const double CardInset = 12; // the dismiss button straddles the card's top-left corner
    private const double ShadowPad = 16;
    private readonly double _cardWidth;
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
        var highContrast = SystemParameters.HighContrast;
        var white = highContrast ? SystemColors.WindowTextBrush : Brushes.White;
        var muted = highContrast ? SystemColors.WindowTextBrush : Brush(140, 255, 255, 255);
        var icon = PlatformIcon(request, white);
        var textX = icon is null ? 14 : 14 + IconSize + 9;
        _cardWidth = request.HasSplitAction
            ? MinimumCardWidth
            : Math.Min(MaximumCardWidth, Math.Max(MinimumCardWidth, Math.Ceiling(textX
                + Math.Max(TextWidth(request.Title, FontWeights.SemiBold, 13), TextWidth(request.Subtitle, FontWeights.Normal, 11))
                + 8 + ButtonWidth + 12)));
        Width = CardInset + _cardWidth + ShadowPad;
        Height = CardInset + CardHeight + ShadowPad;
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

        var card = new Border
        {
            Width = _cardWidth,
            Height = CardHeight,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = highContrast ? SystemColors.WindowTextBrush : Brush(26, 255, 255, 255),
            Background = highContrast ? SystemColors.WindowBrush : Brush(247, 26, 26, 31),
            Effect = highContrast ? null : new DropShadowEffect
            {
                BlurRadius = 20, ShadowDepth = 3, Opacity = 0.4, Color = Colors.Black
            }
        };

        var content = new Grid { Margin = new Thickness(13, 0, 11, 0) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(icon is null ? 0 : IconSize + 9) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (icon is not null)
        {
            // macOS centres the icon one point above the card's midline.
            icon.Margin = new Thickness(0, 0, 0, 2);
            Grid.SetColumn(icon, 0);
            content.Children.Add(icon);
        }

        // macOS: an 18-point title row from y=10 and a 16-point subtitle row from y=30.
        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 9, 8, 0) };
        var title = new TextBlock
        {
            Text = request.Title, FontFamily = new FontFamily("Segoe UI"), FontSize = 13, Height = 18,
            FontWeight = FontWeights.SemiBold, Foreground = white,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap
        };
        AutomationProperties.SetAutomationId(title, "MeetingNotificationTitle");
        var subtitle = new TextBlock
        {
            Text = request.Subtitle, Margin = new Thickness(0, 2, 0, 0), Height = 16,
            FontFamily = new FontFamily("Segoe UI"), FontSize = 11, Foreground = muted,
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
            Width = _cardWidth - 2, Height = 3, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = highContrast ? SystemColors.HighlightBrush : Brush(204, 77, 153, 255)
        };
        AutomationProperties.SetAutomationId(_progress, "MeetingNotificationCountdown");

        // The card masks its children (macOS masksToBounds), so the countdown bar follows the rounded
        // corners. The clip sits inside the border so it does not clip the drop shadow.
        var inner = new Grid { Clip = new RectangleGeometry(new Rect(0, 0, _cardWidth - 2, CardHeight - 2), 9, 9) };
        inner.Children.Add(content);
        inner.Children.Add(_progress);
        card.Child = inner;
        Canvas.SetLeft(card, CardInset);
        Canvas.SetTop(card, CardInset);

        // macOS NotificationDismissButton: a 22-point circle with a drawn cross, so the mark sits on the
        // circle's centre instead of a text glyph's baseline.
        var close = CreateButton("", 22, 22, highContrast ? SystemColors.WindowBrush : Brush(179, 0, 0, 0),
            white, 10, new CornerRadius(11));
        close.Content = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 L8,8 M8,0 L0,8"),
            Stroke = highContrast ? SystemColors.WindowTextBrush : Brush(219, 255, 255, 255),
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        close.ToolTip = "Dismiss";
        close.BorderBrush = highContrast ? SystemColors.WindowTextBrush : Brush(140, 255, 255, 255);
        close.BorderThickness = new Thickness(1);
        close.Click += (_, _) => Finish(IndicatorCommandType.MeetingNotificationDismiss);
        AutomationProperties.SetAutomationId(close, "MeetingNotificationDismiss");
        AutomationProperties.SetName(close, "Dismiss meeting notification");
        Canvas.SetLeft(close, 1);
        Canvas.SetTop(close, 0);

        var root = new Canvas();
        root.Children.Add(card);
        root.Children.Add(close);
        root.MouseEnter += (_, _) => { _countdown.Pause(); _timer.Stop(); };
        root.MouseLeave += (_, _) => { _countdown.Resume(); _lastTick = Stopwatch.GetTimestamp(); if (!_closing) _timer.Start(); };
        AutomationProperties.SetAutomationId(root, "MeetingNotificationWindow");
        AutomationProperties.SetName(root, $"{request.Title}. {request.Subtitle}");
        Content = root;

        _timer.Tick += (_, _) => Tick();
        _countdown.Start(request.DismissAfterSeconds);
        // macOS orders the panel in at full opacity and only animates the fade-out.
        Loaded += (_, _) =>
        {
            PlaceAtTopRight();
            Opacity = 1;
            _lastTick = Stopwatch.GetTimestamp();
            _timer.Start();
        };
        Opacity = 0; // hidden until placed, so it never flashes at the origin
    }

    private FrameworkElement BuildAction(IndicatorMeetingNotification request, bool highContrast)
    {
        if (!request.HasSplitAction)
        {
            var start = CreateButton(request.ActionLabel, ButtonWidth, ButtonHeight,
                highContrast ? SystemColors.HighlightBrush : Brush(255, 51, 128, 255),
                highContrast ? SystemColors.HighlightTextBrush : Brushes.White, 12, new CornerRadius(6));
            start.Click += (_, _) => Finish(IndicatorCommandType.MeetingNotificationAction, request.SingleAction);
            AutomationProperties.SetAutomationId(start, "MeetingNotificationPrimaryAction");
            AutomationProperties.SetName(start, request.ActionLabel);
            return start;
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var primaryAction = request.DefaultAction;
        var primary = CreateButton(primaryAction.Label(), ButtonWidth, ButtonHeight,
            highContrast ? SystemColors.HighlightBrush : Brush(255, 51, 184, 135),
            highContrast ? SystemColors.HighlightTextBrush : Brushes.White, 11, new CornerRadius(6, 0, 0, 6));
        primary.Click += (_, _) => Finish(IndicatorCommandType.MeetingNotificationAction, ToNotificationAction(primaryAction));
        AutomationProperties.SetAutomationId(primary, "MeetingNotificationPrimaryAction");
        AutomationProperties.SetName(primary, primaryAction.Label());
        var chevron = CreateButton("▾", ChevronWidth, ButtonHeight,
            highContrast ? SystemColors.HighlightBrush : Brush(255, 38, 148, 107),
            highContrast ? SystemColors.HighlightTextBrush : Brush(204, 255, 255, 255), 9, new CornerRadius(0, 6, 6, 0));
        chevron.ToolTip = string.Join(" · ", primaryAction.AvailableAlternatives(true, true).Select(alternative => alternative.Label()));
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

    /// <summary>
    /// macOS shows the bundled logo for Zoom, Meet, and Teams, a video glyph for the other recognised
    /// platforms, and no icon at all (the text moves left) when the platform is unknown.
    /// </summary>
    private static FrameworkElement? PlatformIcon(IndicatorMeetingNotification request, Brush glyphBrush)
    {
        // Exact names: a substring match would give the generic "Meeting" platform the Google Meet logo.
        var asset = request.Platform switch
        {
            MeetingUrlParser.GoogleMeet => "google-meet.png",
            MeetingUrlParser.Zoom => "zoom-app.png",
            MeetingUrlParser.MicrosoftTeams => "teams.png",
            _ => null
        };
        if (asset is not null)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", asset);
            if (File.Exists(path))
            {
                var image = new Image
                {
                    Width = IconSize, Height = IconSize, Stretch = Stretch.Uniform,
                    Source = new BitmapImage(new Uri(path, UriKind.Absolute)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                AutomationProperties.SetName(image, request.Platform);
                return image;
            }
        }

        if (!MeetingPlatformBadges.HasBadge(request.Platform)) return null;
        var glyph = new TextBlock
        {
            Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 20,
            Foreground = glyphBrush, Width = IconSize, TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(glyph, request.Platform);
        return glyph;
    }

    private static double TextWidth(string? text, FontWeight weight, double size) =>
        new FormattedText(text ?? "", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size, Brushes.White, 1.0).WidthIncludingTrailingWhitespace;

    private static Button CreateButton(string text, double width, double height, Brush background, Brush foreground,
        double fontSize, CornerRadius radius)
    {
        var button = new Button
        {
            Content = text, Width = width, Height = height, Background = background, Foreground = foreground,
            FontFamily = new FontFamily("Segoe UI"), FontSize = fontSize, FontWeight = FontWeights.Medium,
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
        // macOS insets the panel 16 points from the work area's top-right: the card's right edge sits on
        // that inset (the shadow pad hangs past it) and its top is 12 lower, below the dismiss gutter.
        var left = Left + bottomRight.X - (CardInset + _cardWidth) - 16;
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
        _progress.Width = (_cardWidth - 2) * _countdown.Progress;
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
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(200));
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
