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
}
