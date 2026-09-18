using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;

namespace Muesli.Windows.Services;

public sealed class MeetingPromptService
{
    private const double PromptWidth = 360;
    private const double PromptHeight = 70;
    private const double ScreenMargin = 16;
    private static readonly TimeSpan DismissDuration = TimeSpan.FromSeconds(15);

    private readonly AppLogService _log = new();
    private Window? _window;
    private Action? _autoDismiss;

    public bool IsVisible => _window?.IsVisible == true;

    public void Show(DetectedMeeting meeting, Action record, Action ignore)
    {
        if (IsVisible)
        {
            _log.Info("MeetingPromptService.Show called while visible — replacing with the new meeting.");
            Reset();
        }

        _autoDismiss = null;
        _window = CreateWindow();
        _window.Content = CreateContent(meeting, () =>
        {
            _autoDismiss = null;
            Dismiss(record);
        }, () =>
        {
            _autoDismiss = null;
            Dismiss(ignore);
        });
        // Showing at zero opacity first creates a real PresentationSource/HWND.  Placement
        // then uses the cursor monitor with finite WPF screen conversion on mixed-DPI desktops.
        _window.Opacity = 0;
        _window.Show();
        PositionWindow(_window);
        AnimateIn(_window);
    }

    public void Close()
    {
        _window?.Close();
        _window = null;
        _autoDismiss = null;
    }

    public void Reset()
    {
        try
        {
            if (_window is not null)
            {
                _window.Close();
            }
        }
        catch
        {
        }

        _window = null;
        _autoDismiss = null;
    }

    private void Dismiss(Action? afterClose = null)
    {
        if (_window is null)
        {
            afterClose?.Invoke();
            return;
        }

        var window = _window;
        _window = null;
        if (!AnimationsEnabled)
        {
            window.Close();
            afterClose?.Invoke();
            return;
        }
        window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation { From = window.Opacity, To = 0, Duration = TimeSpan.FromMilliseconds(160) });

        var closeTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(170)
        };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            window.Close();
            afterClose?.Invoke();
        };
        closeTimer.Start();
    }

    private static Window CreateWindow()
    {
        return new Window
        {
            Width = PromptWidth,
            Height = PromptHeight,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = MediaBrushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Focusable = false,
            ShowActivated = false
        };
    }

    private FrameworkElement CreateContent(DetectedMeeting meeting, Action record, Action dismiss)
    {
        var root = new Grid
        {
            ClipToBounds = true
        };

        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(MediaColor.FromArgb(26, 255, 255, 255)),
            Background = new SolidColorBrush(MediaColor.FromArgb(247, 26, 26, 31)),
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 3,
                Opacity = 0.35,
                Color = MediaColor.FromRgb(0, 0, 0)
            }
        };
        root.Children.Add(card);

        var progress = new Border
        {
            Width = PromptWidth,
            Height = 3,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(MediaColor.FromArgb(204, 77, 153, 255))
        };
        root.Children.Add(progress);

        var layout = new Grid
        {
            Margin = new Thickness(14, 0, 10, 0)
        };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.Child = layout;

        var icon = CreatePlatformIcon(meeting.Platform);
        Grid.SetColumn(icon, 0);
        layout.Children.Add(icon);

        var copy = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0)
        };
        Grid.SetColumn(copy, 1);
        layout.Children.Add(copy);

        copy.Children.Add(new TextBlock
        {
            Text = $"{meeting.Platform} detected",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(MediaColor.FromArgb(235, 255, 255, 255)),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        copy.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(meeting.Title)
                ? "Meeting ready to record"
                : meeting.Title,
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = 11,
            Foreground = new SolidColorBrush(MediaColor.FromArgb(140, 255, 255, 255)),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var hasJoinButton = !string.IsNullOrWhiteSpace(meeting.BrowserUrl);
        var actions = new Grid
        {
            Width = hasJoinButton ? 148 : 138,
            Height = 40,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(actions, 2);
        layout.Children.Add(actions);

        if (hasJoinButton)
        {
            var split = CreateJoinSplitButton(
                () =>
                {
                    OpenMeetingUrl(meeting.BrowserUrl);
                    record();
                },
                () =>
                {
                    OpenMeetingUrl(meeting.BrowserUrl);
                    dismiss();
                },
                // Record Only: the user is already in the meeting and wants capture without
                // Muesli opening a second copy of it in the browser.
                record);
            split.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            split.VerticalAlignment = VerticalAlignment.Center;
            actions.Children.Add(split);
        }
        else
        {
            var recordButton = CreateActionButton("Start Recording", record);
            recordButton.Width = 120;
            recordButton.Height = 30;
            recordButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            recordButton.VerticalAlignment = VerticalAlignment.Center;
            actions.Children.Add(recordButton);
        }

        var closeButton = CreateCloseButton(dismiss);
        closeButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        closeButton.VerticalAlignment = VerticalAlignment.Top;
        actions.Children.Add(closeButton);

        if (!AnimationsEnabled)
        {
            var dismissTimer = new System.Windows.Threading.DispatcherTimer { Interval = DismissDuration };
            dismissTimer.Tick += (_, _) =>
            {
                dismissTimer.Stop();
                var action = _autoDismiss;
                _autoDismiss = null;
                Dismiss(action);
            };
            dismissTimer.Start();
        }
        else
        {
            var shrink = new DoubleAnimation { From = PromptWidth, To = 0, Duration = new Duration(DismissDuration), FillBehavior = FillBehavior.Stop };
            shrink.Completed += (_, _) =>
            {
                var action = _autoDismiss;
                _autoDismiss = null;
                Dismiss(action);
            };
            progress.BeginAnimation(FrameworkElement.WidthProperty, shrink);
        }

        return root;
    }

    private static FrameworkElement CreatePlatformIcon(string platform)
    {
        var assetName = PlatformAssetName(platform);
        if (assetName is not null)
        {
            var imagePath = Path.Combine(AppContext.BaseDirectory, "Assets", assetName);
            if (File.Exists(imagePath))
            {
                return new System.Windows.Controls.Image
                {
                    Width = 28,
                    Height = 28,
                    VerticalAlignment = VerticalAlignment.Center,
                    Source = new BitmapImage(new Uri(imagePath, UriKind.Absolute)),
                    Stretch = Stretch.Uniform
                };
            }
        }

        // Every supported platform gets its own accent and glyph from the shared badge registry,
        // so Zoom, Webex, Chime, and FaceTime are distinguishable rather than sharing one blue.
        var badge = MeetingPlatformBadges.For(platform);
        var accent = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(badge.AccentHex);

        return new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(7),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(MediaColor.FromArgb(32, accent.R, accent.G, accent.B)),
            ToolTip = badge.Platform,
            Child = new TextBlock
            {
                Text = badge.Glyph,
                FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = new SolidColorBrush(accent),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private static string? PlatformAssetName(string platform)
    {
        if (platform.Contains("Meet", StringComparison.OrdinalIgnoreCase))
        {
            return "google-meet.png";
        }

        if (platform.Contains("Zoom", StringComparison.OrdinalIgnoreCase))
        {
            return "zoom-app.png";
        }

        if (platform.Contains("Teams", StringComparison.OrdinalIgnoreCase))
        {
            return "teams.png";
        }

        return null;
    }

    private static string PlatformGlyph(string platform)
    {
        if (platform.Contains("Teams", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE716";
        }

        return "\uE714";
    }

    private static Border CreateActionButton(string text, Action click)
    {
        var button = new Border
        {
            Background = new SolidColorBrush(MediaColor.FromRgb(51, 128, 255)),
            CornerRadius = new CornerRadius(6),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                Foreground = MediaBrushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        button.MouseLeftButtonUp += (_, _) => click();
        return button;
    }

    private static Grid CreateJoinSplitButton(Action joinAndRecord, Action joinOnly, Action recordOnly)
    {
        var root = new Grid
        {
            Width = 134,
            Height = 28
        };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });

        var main = new Border
        {
            Background = new SolidColorBrush(MediaColor.FromRgb(51, 184, 135)),
            CornerRadius = new CornerRadius(6, 0, 0, 6),
            Cursor = WpfCursors.Hand,
            Child = new TextBlock
            {
                Text = "Join & Record",
                Foreground = MediaBrushes.White,
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        main.MouseLeftButtonUp += (_, _) => joinAndRecord();
        Grid.SetColumn(main, 0);
        root.Children.Add(main);

        var chevron = new Border
        {
            Background = new SolidColorBrush(MediaColor.FromRgb(38, 148, 107)),
            CornerRadius = new CornerRadius(0, 6, 6, 0),
            Cursor = WpfCursors.Hand,
            Child = new TextBlock
            {
                Text = "v",
                Foreground = new SolidColorBrush(MediaColor.FromArgb(204, 255, 255, 255)),
                FontSize = 9,
                FontWeight = FontWeights.Medium,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        chevron.MouseLeftButtonUp += (_, _) =>
        {
            var menu = new ContextMenu();
            var joinOnlyItem = new MenuItem { Header = "Join Only" };
            joinOnlyItem.Click += (_, _) => joinOnly();
            menu.Items.Add(joinOnlyItem);
            var recordOnlyItem = new MenuItem { Header = "Record Only" };
            recordOnlyItem.Click += (_, _) => recordOnly();
            menu.Items.Add(recordOnlyItem);
            menu.PlacementTarget = chevron;
            menu.IsOpen = true;
        };
        Grid.SetColumn(chevron, 1);
        root.Children.Add(chevron);
        return root;
    }

    private static Border CreateCloseButton(Action click)
    {
        var button = new Border
        {
            Width = 16,
            Height = 16,
            Background = MediaBrushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = "x",
                Foreground = new SolidColorBrush(MediaColor.FromArgb(90, 255, 255, 255)),
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        button.MouseLeftButtonUp += (_, _) => click();
        return button;
    }

    private static void PositionWindow(Window window)
    {
        var area = WindowPlacementService.GetWorkAreaForCursor(window);
        window.Left = Math.Round(area.Right - PromptWidth - ScreenMargin);
        window.Top = Math.Round(area.Top + ScreenMargin);
    }

    private static void OpenMeetingUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Joining is best-effort; recording should still work if opening the URL fails.
        }
    }

    internal static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    private static void AnimateIn(Window window)
    {
        if (!AnimationsEnabled)
        {
            window.Opacity = 1;
            return;
        }
        window.Opacity = 0;
        window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(250)
        });
    }
}
