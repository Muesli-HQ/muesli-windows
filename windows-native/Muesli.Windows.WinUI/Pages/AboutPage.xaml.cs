using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muesli.Windows.WinUI.ViewModels;

namespace Muesli.Windows.WinUI.Pages;

public sealed partial class AboutPage : Page
{
    public AboutPageViewModel ViewModel { get; } = new(
        App.Library,
        App.Settings,
        App.Clipboard,
        () => App.ShowOnboarding(explicitResume: true),
        App.ShowFeatureTour);

    public AboutPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await ViewModel.LoadAsync();
            ApplyResponsiveLayout(ActualWidth);
        };
        SizeChanged += (_, args) => ApplyResponsiveLayout(args.NewSize.Width);
    }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

    public static InfoBarSeverity StatusSeverity(bool isError) =>
        isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    /// <summary>
    /// 720-wide windows use the 72 DIP icon rail, so this page is ~600 DIP of content.
    /// Stack the brand block, wrap path strings in-card, and stack diagnostic actions
    /// instead of clipping mono paths or overflowing the action row.
    /// </summary>
    private void ApplyResponsiveLayout(double pageWidth)
    {
        if (pageWidth <= 0)
        {
            return;
        }

        var windowWidth = App.Window is MainWindow host ? host.CurrentWidthDip : pageWidth;
        var tight = windowWidth < 840;
        var compact = windowWidth < 1008;
        // Gutter and vertical rhythm come from the shell (P1-04); `tight`/`compact` below are
        // this page's own stacking decisions and stay keyed on the window width.
        ContentRoot.Padding = MuesliPageMetrics.ContentPadding(pageWidth);
        // P7-07: the reading-width cap is the shell's MuesliPageContentMaxWidth, declared once in
        // the XAML. This page used to narrow it further to 720/760 from code, which left a wide
        // dead zone at 1280 and put About on a different column width from every other page.
        ContentRoot.Spacing = tight ? 10 : 12;

        BrandMark.Width = tight ? 56 : 72;
        BrandMark.Height = tight ? 56 : 72;
        BrandMark.CornerRadius = new CornerRadius(tight ? 14 : 18);
        if (tight)
        {
            Grid.SetRow(BrandMark, 0);
            Grid.SetColumn(BrandMark, 0);
            Grid.SetColumnSpan(BrandMark, 2);
            Grid.SetRow(BrandCopy, 1);
            Grid.SetColumn(BrandCopy, 0);
            Grid.SetColumnSpan(BrandCopy, 2);
            BrandMark.Margin = new Thickness(0, 0, 0, 12);
            BrandMark.HorizontalAlignment = HorizontalAlignment.Left;
        }
        else
        {
            Grid.SetRow(BrandMark, 0);
            Grid.SetColumn(BrandMark, 0);
            Grid.SetColumnSpan(BrandMark, 1);
            Grid.SetRow(BrandCopy, 0);
            Grid.SetColumn(BrandCopy, 1);
            Grid.SetColumnSpan(BrandCopy, 1);
            BrandMark.Margin = new Thickness(0);
            BrandMark.HorizontalAlignment = HorizontalAlignment.Left;
        }

        PlaceActionRow(ResumeSetupCopy, ResumeSetupButton, tight);
        PlaceActionRow(ReplayTourCopy, ReplayFeatureTourButton, tight);
        PlacePathRow(ProfileLabel, ProfileValue, tight);
        PlacePathRow(LogsLabel, LogsValue, tight);
        PlacePathRow(DataLabel, DataValue, tight);
        PlacePathRow(SettingsLabel, SettingsValue, tight);
        PlacePathRow(CacheLabel, CacheValue, tight);

        DiagnosticActions.Orientation = tight ? Orientation.Vertical : Orientation.Horizontal;
        foreach (var button in new[] { RefreshDiagnosticsButton, CopyDiagnosticsButton, OpenLogsButton, OpenModelCacheButton })
        {
            button.HorizontalAlignment = tight ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        }
    }

    private static void PlaceActionRow(FrameworkElement copy, FrameworkElement action, bool tight)
    {
        if (tight)
        {
            Grid.SetRow(copy, 0);
            Grid.SetColumn(copy, 0);
            Grid.SetColumnSpan(copy, 2);
            Grid.SetRow(action, 1);
            Grid.SetColumn(action, 0);
            Grid.SetColumnSpan(action, 2);
            action.HorizontalAlignment = HorizontalAlignment.Left;
            action.Margin = new Thickness(0, 10, 0, 0);
        }
        else
        {
            Grid.SetRow(copy, 0);
            Grid.SetColumn(copy, 0);
            Grid.SetColumnSpan(copy, 1);
            Grid.SetRow(action, 0);
            Grid.SetColumn(action, 1);
            Grid.SetColumnSpan(action, 1);
            action.HorizontalAlignment = HorizontalAlignment.Right;
            action.Margin = new Thickness(0);
        }
    }

    private static void PlacePathRow(FrameworkElement label, FrameworkElement value, bool tight)
    {
        if (tight)
        {
            Grid.SetRow(label, 0);
            Grid.SetColumn(label, 0);
            Grid.SetColumnSpan(label, 2);
            Grid.SetRow(value, 1);
            Grid.SetColumn(value, 0);
            Grid.SetColumnSpan(value, 2);
            value.Margin = new Thickness(0, 6, 0, 0);
        }
        else
        {
            Grid.SetRow(label, 0);
            Grid.SetColumn(label, 0);
            Grid.SetColumnSpan(label, 1);
            Grid.SetRow(value, 0);
            Grid.SetColumn(value, 1);
            Grid.SetColumnSpan(value, 1);
            value.Margin = new Thickness(0);
        }
    }
}
