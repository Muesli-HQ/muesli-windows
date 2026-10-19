using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Muesli.Windows.Services;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Muesli.Windows.WinUI;

/// <summary>
/// macOS FeatureTourOverlay and FeatureTourInvitationView: each beat opens the page where the
/// feature lives, dims everything except the control, and places the callout beside it.
/// </summary>
public sealed partial class MainPage
{
    private const double SpotlightPadding = 8;
    private FeatureTour? _tour;
    private int _tourIndex = -1;
    private int _tourGeneration;
    private FrameworkElement? _tourTarget;

    /// <summary>
    /// <paramref name="invite"/> asks first, as macOS does when a new release offers the tour by
    /// itself; "What's New in Muesli" starts the walkthrough directly.
    /// </summary>
    public void StartFeatureTour(bool invite)
    {
        var tour = FeatureTourCatalog.Latest;
        if (tour.Steps.Count == 0) return;
        _tour = tour;
        App.Settings.Save(App.Settings.Load() with { FeatureTourLastPresentedVersion = tour.Version });
        if (!invite)
        {
            BeginTour();
            return;
        }

        TourInvitationVersion.Text = $"MUESLI {tour.DisplayVersion}";
        TourInvitationBody.Text =
            $"See {tour.Steps.Count} additions in the places where you’ll actually use them. You can replay this later from What’s New in Muesli in the tray menu.";
        TourInvitation.Visibility = Visibility.Visible;
        _ = TourAcceptButton.Focus(FocusState.Programmatic);
    }

    private void BeginTour()
    {
        TourInvitation.Visibility = Visibility.Collapsed;
        _tourIndex = 0;
        RenderTourStep();
    }

    private void TourAccept_Click(object sender, RoutedEventArgs e) => BeginTour();

    private void TourInvitationSkip_Click(object sender, RoutedEventArgs e) => EndTour();

    private void TourNext_Click(object sender, RoutedEventArgs e)
    {
        if (_tour is null || _tourIndex < 0) return;
        if (_tourIndex >= _tour.Steps.Count - 1)
        {
            EndTour();
            return;
        }
        _tourIndex++;
        RenderTourStep();
    }

    private void TourBack_Click(object sender, RoutedEventArgs e)
    {
        if (_tourIndex <= 0) return;
        _tourIndex--;
        RenderTourStep();
    }

    private void TourSkip_Click(object sender, RoutedEventArgs e) => EndTour();

    private void TourEscape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TourOverlay.Visibility != Visibility.Visible && TourInvitation.Visibility != Visibility.Visible) return;
        EndTour();
        args.Handled = true;
    }

    private void TourOverlay_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutTour();

    private void TourCard_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutTour();

    private void RenderTourStep()
    {
        if (_tour is null || _tourIndex < 0 || _tourIndex >= _tour.Steps.Count)
        {
            EndTour();
            return;
        }

        var step = _tour.Steps[_tourIndex];
        TourGlyph.Glyph = step.Glyph;
        TourEyebrow.Text = step.Eyebrow;
        TourTitle.Text = step.Title;
        TourBody.Text = step.Message;
        TourStepLabel.Text = $"{_tourIndex + 1} of {_tour.Steps.Count}";
        TourBackButton.Visibility = _tourIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
        TourNextButton.Content = _tourIndex == _tour.Steps.Count - 1 ? "Done" : "Next";
        SetTourTarget(null);
        TourOverlay.Visibility = Visibility.Visible;
        ApplyTourOverlayChrome();

        var generation = ++_tourGeneration;
        var targetId = OpenTourRoute(step) ? step.TargetId : null;
        _ = SpotlightWhenReadyAsync(targetId, generation);
        _ = TourNextButton.Focus(FocusState.Programmatic);
    }

    /// <summary>Opens the step's page. False when the page it needs cannot be shown.</summary>
    private bool OpenTourRoute(FeatureTourStep step)
    {
        switch (step.Route)
        {
            case null:
                return true;
            case "retranscribe":
                // Only a meeting whose audio is still on disk can be re-transcribed.
                var meeting = App.Library.History.LoadMeetings()
                    .Where(item => new[] { item.MicrophoneAudioPath, item.SystemAudioPath, item.SourcePath }
                        .Any(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)))
                    .OrderByDescending(item => item.CreatedAt)
                    .FirstOrDefault();
                NavigateTo("meetings");
                if (meeting is null) return false;
                ContentFrame.Navigate(typeof(Pages.MeetingDetailPage), meeting.Id);
                return true;
            case var route when route.StartsWith("settings:", StringComparison.Ordinal):
                NavigateTo("settings");
                ContentFrame.Navigate(typeof(Pages.SettingsPage), route["settings:".Length..]);
                return true;
            default:
                NavigateTo(step.Route);
                return true;
        }
    }

    /// <summary>The page builds after navigation (Settings loads asynchronously), so look for up to two seconds.</summary>
    private async Task SpotlightWhenReadyAsync(string? targetId, int generation)
    {
        if (targetId is not null)
        {
            for (var attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(50);
                if (generation != _tourGeneration) return;
                if (FindByAutomationId(ContentFrame, targetId) is not { ActualWidth: > 0, ActualHeight: > 0 } element) continue;
                element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.4 });
                await Task.Delay(80);
                if (generation != _tourGeneration) return;
                SetTourTarget(element);
                return;
            }
        }
        LayoutTour();
    }

    private void SetTourTarget(FrameworkElement? element)
    {
        if (_tourTarget is not null) _tourTarget.SizeChanged -= TourTarget_SizeChanged;
        _tourTarget = element;
        if (element is not null) element.SizeChanged += TourTarget_SizeChanged;
        LayoutTour();
    }

    private void TourTarget_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutTour();

    private void LayoutTour()
    {
        if (TourOverlay.Visibility != Visibility.Visible) return;
        var size = new Size(TourOverlay.ActualWidth, TourOverlay.ActualHeight);
        TourDimBounds.Rect = new Rect(0, 0, size.Width, size.Height);

        Rect? spotlight = null;
        if (_tourTarget is { IsLoaded: true, ActualWidth: > 0 } target)
        {
            var bounds = target.TransformToVisual(TourOverlay).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
            spotlight = new Rect(bounds.X - SpotlightPadding, bounds.Y - SpotlightPadding,
                bounds.Width + SpotlightPadding * 2, bounds.Height + SpotlightPadding * 2);
        }

        TourDimHole.Rect = spotlight ?? new Rect(0, 0, 0, 0);
        TourSpotlightRing.Visibility = spotlight is null ? Visibility.Collapsed : Visibility.Visible;
        if (spotlight is { } ring)
        {
            Canvas.SetLeft(TourSpotlightRing, ring.X);
            Canvas.SetTop(TourSpotlightRing, ring.Y);
            TourSpotlightRing.Width = ring.Width;
            TourSpotlightRing.Height = ring.Height;
        }

        var card = new Size(TourCard.ActualWidth, TourCard.ActualHeight);
        var preferAbove = _tour is not null && _tourIndex >= 0 && _tour.Steps[_tourIndex].TargetId == "DictionarySuggestionsToggle";
        var center = CalloutCenter(spotlight, size, card, preferAbove);
        Canvas.SetLeft(TourCard, center.X - card.Width / 2);
        Canvas.SetTop(TourCard, center.Y - card.Height / 2);
    }

    /// <summary>
    /// macOS FeatureTourCalloutLayout: the first side of the spotlight where the callout fits,
    /// otherwise the clamped side that covers the least of the spotlight.
    /// </summary>
    internal static Point CalloutCenter(Rect? spotlight, Size container, Size callout, bool preferAbove, double margin = 20, double gap = 24)
    {
        var middle = new Point(container.Width / 2, container.Height / 2);
        if (spotlight is not { } spot) return middle;
        var bounds = new Rect(margin, margin, Math.Max(0, container.Width - margin * 2), Math.Max(0, container.Height - margin * 2));
        var above = new Point(spot.X + spot.Width / 2, spot.Y - gap - callout.Height / 2);
        var below = new Point(spot.X + spot.Width / 2, spot.Bottom + gap + callout.Height / 2);
        var leading = new Point(spot.X - gap - callout.Width / 2, spot.Y + spot.Height / 2);
        var trailing = new Point(spot.Right + gap + callout.Width / 2, spot.Y + spot.Height / 2);
        Point[] candidates = preferAbove ? [above, below, trailing, leading] : [below, above, trailing, leading];

        foreach (var candidate in candidates)
        {
            var frame = Frame(candidate, callout);
            if (frame.X >= bounds.X && frame.Y >= bounds.Y && frame.Right <= bounds.Right && frame.Bottom <= bounds.Bottom) return candidate;
        }

        return candidates
            .Select(candidate => new Point(
                Clamp(candidate.X, bounds.X + callout.Width / 2, bounds.Right - callout.Width / 2, bounds.X + bounds.Width / 2),
                Clamp(candidate.Y, bounds.Y + callout.Height / 2, bounds.Bottom - callout.Height / 2, bounds.Y + bounds.Height / 2)))
            .OrderBy(candidate => Overlap(Frame(candidate, callout), spot))
            .First();

        static Rect Frame(Point center, Size size) => new(center.X - size.Width / 2, center.Y - size.Height / 2, size.Width, size.Height);
        static double Clamp(double value, double lower, double upper, double fallback) => lower <= upper ? Math.Clamp(value, lower, upper) : fallback;
        static double Overlap(Rect a, Rect b)
        {
            var width = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
            var height = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
            return width > 0 && height > 0 ? width * height : 0;
        }
    }

    private static FrameworkElement? FindByAutomationId(DependencyObject root, string automationId)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement element && AutomationProperties.GetAutomationId(element) == automationId) return element;
            if (FindByAutomationId(child, automationId) is { } found) return found;
        }
        return null;
    }

    private void ApplyTourOverlayChrome()
    {
        // High Contrast brushes cannot take opacity, so the scrim is dropped there and the strong
        // border alone marks the callout; the dim path still catches clicks outside the spotlight.
        var highContrast = new AccessibilitySettings().HighContrast;
        TourDim.Opacity = highContrast ? 0 : 1;
        TourInvitation.Background = highContrast
            ? new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(0xA8, 0, 0, 0));
        TourCard.BorderThickness = new Thickness(highContrast ? 2 : 1);
        TourCard.BorderBrush = Brush(highContrast ? "MuesliBorderStrongBrush" : "MuesliBorderBrush");
    }

    private void EndTour()
    {
        _tourIndex = -1;
        _tourGeneration++;
        SetTourTarget(null);
        TourOverlay.Visibility = Visibility.Collapsed;
        TourInvitation.Visibility = Visibility.Collapsed;
        VisibleNavItems()
            .FirstOrDefault(button => string.Equals(button.Tag?.ToString(), _selectedTag, StringComparison.OrdinalIgnoreCase))
            ?.Focus(FocusState.Programmatic);
    }
}
