using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Text;
using Muesli.Windows.WinUI.ViewModels;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.ViewManagement;

namespace Muesli.Windows.WinUI.Pages;

public sealed partial class InsightsPage : Page
{
    public InsightsPageViewModel ViewModel { get; } = new(App.Library, App.Clipboard, App.FilePickers, App.Share);

    public InsightsPage()
    {
        InitializeComponent();
            ViewModel.PropertyChanged += (_, args) =>
            {
                StatusBar.Severity = ViewModel.IsStatusError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
                if (args.PropertyName is nameof(ViewModel.DictationShare) or nameof(ViewModel.MeetingShare))
                {
                    UpdateUsageBar();
                }

                if (args.PropertyName is nameof(ViewModel.Days))
                {
                    ScrollHeatmapToLatest();
                }

                if (args.PropertyName is nameof(ViewModel.HasPreview))
                {
                    BringSharePreviewIntoView();
                }
            };
        ActualThemeChanged += (_, _) => ApplyHeroTint();
        Loaded += (_, _) =>
        {
            ApplyHeroTint();
            UpdateUsageBar();
            ViewModel.Load();
            ScrollHeatmapToLatest();
        };
        SizeChanged += (_, _) => ScrollHeatmapToLatest();
    }

    /// <summary>
    /// Parks the activity grid at its right-hand edge, which is the most recent week.
    /// </summary>
    /// <remarks>
    /// P7-02. A 12-month range is 53 columns at an 18 DIP pitch (954 DIP); the card offers roughly
    /// 880 DIP at a 1280 DIP window, so with the scroller at offset 0 the newest ~6 columns were
    /// clipped. All three fixture meetings fall inside them, which made the Meetings measure look
    /// like an empty grid even though <c>InsightsWordAnalyzer.DailyActivity</c> had populated the
    /// series. Scrolling to the end matches the macOS grid, where the newest week is flush right.
    /// The enqueue is required because <c>ScrollableWidth</c> is still 0 until the repeater has
    /// been measured with the new item count.
    /// </remarks>
    private void ScrollHeatmapToLatest() =>
        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (HeatmapScroller.ScrollableWidth > 0)
            {
                HeatmapScroller.ChangeView(HeatmapScroller.ScrollableWidth, null, null, true);
            }
        });

    /// <summary>
    /// Scrolls the generated share card into view.
    /// </summary>
    /// <remarks>
    /// P9-01. "Preview" renders the card as the last item of a long scrolling stack — below the
    /// breakdown and the most-used-words section Prompt 7 added — so on a 820 DIP window the only
    /// feedback was the status InfoBar and the card itself stayed off-screen.
    /// <c>Insights_share_preview_renders_the_generated_card</c> failed on exactly that
    /// ("exists but is off-screen"). The enqueue is required because the card's
    /// <see cref="Visibility"/> has only just changed and it has not been arranged yet.
    /// </remarks>
    private void BringSharePreviewIntoView() =>
        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (ViewModel.HasPreview)
            {
                SharePreviewCard.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            }
        });

    /// <summary>
    /// x:Bind function binding for the share preview. The view-model deliberately holds a
    /// <see cref="Uri"/> rather than an <see cref="ImageSource"/> so it stays free of UI
    /// framework types, and a null value must clear the image instead of failing conversion.
    /// </summary>
    public ImageSource? ToImageSource(Uri? source) =>
        source is null ? null : new BitmapImage(source);

    public Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public Visibility InvertBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    public static FontWeight WordWeight(bool isTop) =>
        isTop ? FontWeights.Bold : FontWeights.Medium;

    // Heatmap palette (macOS InsightsPalette.intensity): empty = surface 62%; L1/L2 = accent
    // 24%/48%; L3/L4 = cyan 67%/95%. Three themed layers share the cell so Light/Dark/Contrast
    // repaint through ThemeResource without rebuilding the grid.
    public static double HeatEmptyOpacity(int level) => level == 0 ? 0.62 : 0;

    public static double HeatAccentOpacity(int level) => level switch
    {
        1 => 0.24,
        2 => 0.48,
        _ => 0
    };

    public static double HeatCyanOpacity(int level) => level switch
    {
        3 => 0.67,
        4 => 0.95,
        _ => 0
    };

    /// <summary>
    /// Width of the word-balance bar halves. The view-model reports fractions, so both columns
    /// are star-sized by their share to reproduce the macOS bar: dictation fill on the left and
    /// the meeting remainder on the right, with no overlapping fill.
    /// </summary>
    private void UpdateUsageBar()
    {
        var dictation = Math.Clamp(ViewModel.DictationShare, 0, 1);
        UsageBar.ColumnDefinitions[0].Width = new GridLength(dictation, GridUnitType.Star);
        UsageBar.ColumnDefinitions[1].Width = new GridLength(Math.Max(0, 1 - dictation), GridUnitType.Star);
    }

    /// <summary>
    /// The hero carries the macOS blue-tinted gradient over its raised fill. It is rebuilt from
    /// the current theme's accent color so a Light/Dark/Contrast switch repaints it correctly.
    /// </summary>
    private void ApplyHeroTint()
    {
        var themeKey = new AccessibilitySettings().HighContrast
            ? "HighContrast"
            : ActualTheme == ElementTheme.Light ? "Light" : "Dark";

        SolidColorBrush? accent = null;
        foreach (var dictionary in Application.Current.Resources.MergedDictionaries.Reverse())
        {
            if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out var themedResources) &&
                themedResources is ResourceDictionary themeDictionary &&
                themeDictionary.TryGetValue("MuesliAccentBrush", out var themedValue) &&
                themedValue is SolidColorBrush themedBrush)
            {
                accent = themedBrush;
                break;
            }
        }

        if (accent is null &&
            Application.Current.Resources.TryGetValue("MuesliAccentBrush", out var value) &&
            value is SolidColorBrush brush)
        {
            accent = brush;
        }

        if (accent is null)
        {
            return;
        }

        // High Contrast keeps the card's system fill flat; a decorative tint must not reduce the
        // contrast of the hero text.
        if (new AccessibilitySettings().HighContrast)
        {
            HeroGradient.GradientStops = new GradientStopCollection
            {
                new() { Color = Color.FromArgb(0x00, 0x00, 0x00, 0x00), Offset = 0 },
                new() { Color = Color.FromArgb(0x00, 0x00, 0x00, 0x00), Offset = 1 }
            };
            return;
        }

        // macOS: accent ~13% at the top-left, a very faint cyan ~2.5%, transparent to the
        // bottom-right, over the raised card fill.
        var color = accent.Color;
        HeroGradient.GradientStops = new GradientStopCollection
        {
            new() { Color = Color.FromArgb(0x21, color.R, color.G, color.B), Offset = 0 },
            new() { Color = Color.FromArgb(0x06, 0x00, 0xC8, 0xD7), Offset = 0.5 },
            new() { Color = Color.FromArgb(0x00, 0x00, 0xC8, 0xD7), Offset = 1 }
        };
    }

    private void RangeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is SelectorBarItem { Tag: string tag } && int.TryParse(tag, out var index))
        {
            ViewModel.RangeIndex = index;
        }
    }

    private void ActivityModeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is SelectorBarItem { Tag: string tag } && int.TryParse(tag, out var index))
        {
            ViewModel.ActivityModeIndex = index;
        }
    }

    private void BackToTimeline_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Frame?.Navigate(typeof(TimelinePage));
    }
}
