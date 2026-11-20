using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muesli.Windows.Core.Insights;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.Services;
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
            AutoReportToggle.IsOn = App.Settings.Load().EnableAutomaticDiagnosticIssuePrompts;
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

    /// <summary>macOS openManualDiagnosticReport; the support bundle stays one click away.</summary>
    private void OpenReport_Click(object sender, RoutedEventArgs e) =>
        _ = DiagnosticIncidentPrompts.ShowAsync(
            DiagnosticIncident.Create(DiagnosticIncident.ManualReport, DiagnosticIncident.ManualReport),
            "Support Bundle…",
            () => ViewModel.ExportSupportBundleCommand.Execute(null));

    /// <summary>macOS setAutomaticDiagnosticIssuePrompts.</summary>
    private void AutoReportToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var settings = App.Settings.Load();
        if (settings.EnableAutomaticDiagnosticIssuePrompts != AutoReportToggle.IsOn)
            App.Settings.Save(settings with { EnableAutomaticDiagnosticIssuePrompts = AutoReportToggle.IsOn });
    }

    private void PostOnX_Click(object sender, RoutedEventArgs e) => _ = ViewModel.ShareAsync(linkedIn: false);

    private void PostOnLinkedIn_Click(object sender, RoutedEventArgs e) => _ = ViewModel.ShareAsync(linkedIn: true);

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
