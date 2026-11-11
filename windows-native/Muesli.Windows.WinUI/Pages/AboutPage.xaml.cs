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
        App.FilePickers,
        App.Dialogs,
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

    private void ApplyResponsiveLayout(double pageWidth)
    {
        if (pageWidth > 0) ContentRoot.Padding = MuesliPageMetrics.ContentPadding(pageWidth);
    }

    // The share links moved here from the sidebar, as on macOS (AboutView "Spread the Word").
    private void Share_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        var target = string.Equals(tag, "linkedin", StringComparison.OrdinalIgnoreCase)
            ? "https://www.linkedin.com/sharing/share-offsite/?url=https://muesli.app"
            : "https://twitter.com/intent/tweet?text=Meet%20Muesli%20%E2%80%94%20private%20on-device%20dictation&url=https://muesli.app";
        _ = global::Windows.System.Launcher.LaunchUriAsync(new Uri(target));
    }
}
