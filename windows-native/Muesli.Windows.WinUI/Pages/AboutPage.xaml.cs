using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muesli.Windows.Core.Insights;
using Muesli.Windows.WinUI.ViewModels;
using Windows.System;

namespace Muesli.Windows.WinUI.Pages;

public sealed partial class AboutPage : Page
{
    public AboutPageViewModel ViewModel { get; } = new(
        App.Library,
        App.Settings,
        App.Clipboard,
        App.FilePickers,
        App.Dialogs);

    public AboutPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ViewModel.Load();
            ApplyResponsiveLayout(ActualWidth);
        };
        SizeChanged += (_, args) => ApplyResponsiveLayout(args.NewSize.Width);
    }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

    public static InfoBarSeverity StatusSeverity(bool isError) =>
        isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    private void Donate_Click(object sender, RoutedEventArgs e) =>
        _ = Launcher.LaunchUriAsync(new Uri(ContributionShare.DonateUrl));

    private void GitHub_Click(object sender, RoutedEventArgs e) =>
        _ = Launcher.LaunchUriAsync(new Uri(ContributionShare.GitHubUrl));

    private void PostOnX_Click(object sender, RoutedEventArgs e) => _ = ViewModel.ShareAsync(linkedIn: false);

    private void PostOnLinkedIn_Click(object sender, RoutedEventArgs e) => _ = ViewModel.ShareAsync(linkedIn: true);

    private void ApplyResponsiveLayout(double pageWidth)
    {
        if (pageWidth > 0) ContentRoot.Padding = MuesliPageMetrics.ContentPadding(pageWidth);
    }
}
