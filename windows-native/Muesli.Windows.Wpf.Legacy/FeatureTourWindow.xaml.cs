using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Muesli.Windows.Services;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace Muesli.Windows;

public partial class FeatureTourWindow : Window
{
    public const int CurrentVersion = FeatureTourCatalog.CurrentVersion;
    private readonly Action? _completed;
    private readonly bool _visualVerification;
    private readonly Action<int>? _onStep;
    private int _step;

    private static readonly FeatureTourStep[] Steps = FeatureTourCatalog.Steps.ToArray();

    public FeatureTourWindow(Action? completed = null, bool visualVerification = false, Action<int>? onStep = null)
    {
        _completed = completed;
        _visualVerification = visualVerification;
        _onStep = onStep;
        InitializeComponent();
        WindowStartupLocation = visualVerification
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.Manual;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (Owner is not null)
        {
            Owner.LocationChanged += Owner_LayoutChanged;
            Owner.SizeChanged += Owner_LayoutChanged;
            SyncToOwner();
        }

        Render();
        NextButton.Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (Owner is not null)
        {
            Owner.LocationChanged -= Owner_LayoutChanged;
            Owner.SizeChanged -= Owner_LayoutChanged;
        }

        base.OnClosed(e);
    }

    private void Owner_LayoutChanged(object? sender, EventArgs e) => SyncToOwner();

    private void SyncToOwner()
    {
        if (Owner is null)
            return;

        Left = Owner.Left;
        Top = Owner.Top;
        Width = Owner.ActualWidth > 0 ? Owner.ActualWidth : Owner.Width;
        Height = Owner.ActualHeight > 0 ? Owner.ActualHeight : Owner.Height;
        LayoutSpotlight();
    }

    private void Render()
    {
        var item = Steps[_step];
        PreviewModeText.Visibility = _visualVerification ? Visibility.Visible : Visibility.Collapsed;
        PreviewModeText.Text = _visualVerification ? "VISUAL VERIFICATION MODE · in-memory presentation only" : "";
        ProgressText.Text = $"FEATURE TOUR · {_step + 1} OF {Steps.Length}";
        TitleText.Text = item.Title;
        BodyText.Text = item.Body;
        BackButton.IsEnabled = _step > 0;
        NextButton.Content = _step == Steps.Length - 1 ? "Done" : "Next";
        _onStep?.Invoke(_step);
        Dispatcher.BeginInvoke(
            () =>
            {
                Owner?.UpdateLayout();
                UpdateLayout();
                LayoutSpotlight();
            },
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void LayoutSpotlight()
    {
        var bounds = new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        var spotlight = ResolveSpotlight(bounds);
        SpotlightBorder.Width = spotlight.Width;
        SpotlightBorder.Height = spotlight.Height;
        Canvas.SetLeft(SpotlightBorder, spotlight.X);
        Canvas.SetTop(SpotlightBorder, spotlight.Y);
        DimPath.Data = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(bounds),
            new RectangleGeometry(spotlight, 10, 10));

        CalloutCard.Measure(new WpfSize(double.PositiveInfinity, double.PositiveInfinity));
        var calloutSize = CalloutCard.DesiredSize;
        if (calloutSize.Width < 1)
            calloutSize = new WpfSize(360, 220);

        var gap = 18.0;
        var x = spotlight.Right + gap;
        var y = spotlight.Top;
        if (x + calloutSize.Width > bounds.Width - 20)
            x = Math.Max(20, spotlight.Left - gap - calloutSize.Width);
        if (y + calloutSize.Height > bounds.Height - 20)
            y = Math.Max(20, bounds.Height - calloutSize.Height - 20);
        Canvas.SetLeft(CalloutCard, x);
        Canvas.SetTop(CalloutCard, y);
    }

    private Rect ResolveSpotlight(Rect bounds)
    {
        if (Steps[_step].TargetName == "FloatingIndicator")
        {
            var windows = System.Windows.Application.Current?.Windows;
            if (windows is not null)
            {
                foreach (Window window in windows)
                {
                    if (window.Name == "FloatingIndicator" && window.IsVisible)
                    {
                        try
                        {
                            if (PresentationSource.FromVisual(this) is { CompositionTarget: not null } overlaySource)
                            {
                                var fromDevice = overlaySource.CompositionTarget.TransformFromDevice;
                                var targetOrigin = fromDevice.Transform(window.PointToScreen(new WpfPoint(0, 0)));
                                var overlayOrigin = fromDevice.Transform(PointToScreen(new WpfPoint(0, 0)));
                                return new Rect(
                                    Math.Max(8, targetOrigin.X - overlayOrigin.X - 6),
                                    Math.Max(8, targetOrigin.Y - overlayOrigin.Y - 6),
                                    Math.Max(36, window.ActualWidth + 12),
                                    Math.Max(28, window.ActualHeight + 12));
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }

            return new Rect(Math.Max(8, bounds.Width - 140), Math.Max(8, bounds.Height * 0.45), 76, 28);
        }

        var target = FindTarget(Steps[_step].TargetName);
        if (target is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 })
        {
            try
            {
                if (PresentationSource.FromVisual(this) is { CompositionTarget: not null } overlaySource)
                {
                    var fromDevice = overlaySource.CompositionTarget.TransformFromDevice;
                    var targetOrigin = fromDevice.Transform(target.PointToScreen(new WpfPoint(0, 0)));
                    var overlayOrigin = fromDevice.Transform(PointToScreen(new WpfPoint(0, 0)));
                    return new Rect(
                        Math.Max(8, targetOrigin.X - overlayOrigin.X - 6),
                        Math.Max(8, targetOrigin.Y - overlayOrigin.Y - 6),
                        Math.Max(36, target.ActualWidth + 12),
                        Math.Max(28, target.ActualHeight + 12));
                }

                if (Owner is FrameworkElement owner)
                {
                    var topLeft = target.TransformToAncestor(owner).Transform(new WpfPoint(0, 0));
                    return new Rect(
                        Math.Max(8, topLeft.X - 6),
                        Math.Max(8, topLeft.Y - 6),
                        Math.Max(36, target.ActualWidth + 12),
                        Math.Max(28, target.ActualHeight + 12));
                }
            }
            catch
            {
            }
        }

        return new Rect(bounds.Width * 0.08, bounds.Height * 0.22, Math.Min(240, bounds.Width * 0.28), 44);
    }

    private FrameworkElement? FindTarget(string name)
    {
        name = name switch
        {
            "dictations" => "DictationsNav",
            "models" => "ModelsNav",
            "meetings" => "MeetingsRowChrome",
            "settings" => "SettingsNav",
            "about" => "AboutNav",
            _ => name
        };
        if (name == "FloatingIndicator")
        {
            var windows = System.Windows.Application.Current?.Windows;
            if (windows is not null)
            {
                foreach (Window window in windows)
                {
                    if (window.Name == "FloatingIndicator" && window.IsVisible)
                    {
                        return window;
                    }
                }
            }
        }

        if (Owner is not FrameworkElement owner)
            return null;

        if (owner.FindName(name) is FrameworkElement named)
            return named;

        return FindNamedVisual(owner, name);
    }

    private static FrameworkElement? FindNamedVisual(DependencyObject root, string name)
    {
        if (root is FrameworkElement element && element.Name == name)
            return element;

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var found = FindNamedVisual(VisualTreeHelper.GetChild(root, index), name);
            if (found is not null)
                return found;
        }

        return null;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _step = Math.Max(0, _step - 1);
        Render();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == Steps.Length - 1)
        {
            _completed?.Invoke();
            Close();
            return;
        }

        _step++;
        Render();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }
}
