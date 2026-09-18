using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Muesli.Windows.WinUI.ViewModels;

namespace Muesli.Windows.WinUI.Pages;

public sealed partial class SettingsPage : Page
{
    /// <summary>Value controls that carry the responsive right-hand width, cached after first layout.</summary>
    private List<FrameworkElement>? _valueControls;

    public SettingsPageViewModel ViewModel { get; } = new(
        App.Settings,
        App.Library,
        App.Startup,
        App.Dialogs,
        App.ComputerUse,
        App.Meetings,
        App.Dictation,
        App.MeetingDetection,
        App.ApplyMeetingDetection);

    public SettingsPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => ViewModel.Dispose();
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ViewModel.ShowGeneral) or
                nameof(ViewModel.ShowDictation) or
                nameof(ViewModel.ShowComputerUse) or
                nameof(ViewModel.ShowMeetings) or
                nameof(ViewModel.ShowAppearance) or
                nameof(ViewModel.IsStatusError))
            {
                UpdateSections();
            }
        };
        Loaded += async (_, _) =>
        {
            await ViewModel.LoadAsync();
            ViewModel.ReapplyPickerSelections();
            ViewModel.MarkSaved();
            // Rebuild once the whole template is up, in case a SizeChanged fired against a
            // partially realised tree before Loaded and cached an incomplete list.
            _valueControls = null;
            ApplyResponsiveLayout(ActualWidth);
            UpdateSections();
        };
        SizeChanged += (_, args) => ApplyResponsiveLayout(args.NewSize.Width);
        ActualThemeChanged += (_, _) => ViewModel.RefreshPermissionPresentation();
    }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility InvertBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    private void SectionBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        ViewModel.SectionIndex = sender.SelectedItem is null ? 0 : sender.Items.IndexOf(sender.SelectedItem);
        UpdateSections();
        // With the header pinned (P5-10), a retained scroll offset would open the next tab
        // mid-page with no way to tell that you are not at its top.
        ContentScroller.ChangeView(null, 0, null, disableAnimation: true);
        if (ViewModel.ShowDictation || ViewModel.ShowMeetings)
        {
            ViewModel.ReapplyPickerSelections();
        }
    }

    private void UpdateSections()
    {
        GeneralPanel.Visibility = ViewModel.ShowGeneral ? Visibility.Visible : Visibility.Collapsed;
        DictationPanel.Visibility = ViewModel.ShowDictation ? Visibility.Visible : Visibility.Collapsed;
        ComputerUsePanel.Visibility = ViewModel.ShowComputerUse ? Visibility.Visible : Visibility.Collapsed;
        MeetingsPanel.Visibility = ViewModel.ShowMeetings ? Visibility.Visible : Visibility.Collapsed;
        AppearancePanel.Visibility = ViewModel.ShowAppearance ? Visibility.Visible : Visibility.Collapsed;
        StatusBar.Severity = ViewModel.IsStatusError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
    }

    private async void OpenPermissionSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not SettingsPermissionItem item || !item.CanOpenSystemSettings)
        {
            return;
        }

        _ = await global::Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone"));
    }

    /// <summary>
    /// P5-01. The Dictation row routes to the page that actually owns dictionary suggestions
    /// rather than showing a switch with no persisted setting behind it.
    /// </summary>
    private void OpenDictionaryPage_Click(object sender, RoutedEventArgs e) =>
        (App.Window as MainWindow)?.ShellPage?.NavigateTo("dictionary");

    /// <summary>
    /// P5-11. The export folder used to be an unexplained empty field with no way to pick one.
    /// </summary>
    private async void BrowseExportFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = await App.FilePickers.PickFolderAsync("Choose the meeting notes export folder");
            if (!string.IsNullOrWhiteSpace(folder))
            {
                ViewModel.AutoExportMarkdownDirectory = folder;
            }
        }
        catch (Exception exception)
        {
            ViewModel.ReportFolderPickerFailure(exception.Message);
        }
    }

    /// <summary>
    /// Gutter, reading-width column and value-control widths for the current *content* width.
    /// </summary>
    /// <remarks>
    /// Two things are deliberate here.
    /// <para>
    /// The padding comes verbatim from <see cref="MuesliPageMetrics.ContentPadding"/> and the
    /// header's horizontal margin from <see cref="MuesliPageMetrics.Gutter"/>, which App.xaml
    /// documents as the sibling-alignment half of the same token. This page previously keyed its
    /// own 840/1008/1280 breakpoints off the *window* width, which is why its gutter drifted from
    /// the other pages (P1-04).
    /// </para>
    /// <para>
    /// P5-03: the header and the scroll column are pinned to one computed width, so the tab strip
    /// and Save no longer track the widest card of whichever tab happens to be visible.
    /// </para>
    /// </remarks>
    private void ApplyResponsiveLayout(double contentWidth)
    {
        if (contentWidth <= 0)
        {
            return;
        }

        var padding = MuesliPageMetrics.ContentPadding(contentWidth);
        SettingsContent.Padding = padding;

        var maxWidth = Token("MuesliPageContentMaxWidth", 1040d);
        var columnWidth = Math.Max(0, Math.Min(contentWidth - padding.Left - padding.Right, maxWidth));
        SettingsContent.MinWidth = columnWidth;
        SettingsHeader.Width = columnWidth;
        SettingsHeader.Margin = new Thickness(
            MuesliPageMetrics.Gutter(contentWidth),
            HeaderTopMargin,
            MuesliPageMetrics.Gutter(contentWidth),
            HeaderBottomMargin);

        var compact = MuesliPageMetrics.IsCompact(contentWidth);
        var medium = MuesliPageMetrics.IsMedium(contentWidth);
        var valueWidth = compact ? 196d : medium ? 240d : 300d;
        foreach (var control in ValueControls())
        {
            control.MinWidth = valueWidth;
            control.MaxWidth = Math.Max(valueWidth, 360);
        }

        DataActions.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
        ComputerUseLimitsHost.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
        ComputerUseActions.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
        PushToTalkHost.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
        ExportFolderHost.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
        ExportFolderHost.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
    }

    private const double HeaderTopMargin = 22;
    private const double HeaderBottomMargin = 14;

    /// <summary>
    /// Every <see cref="ComboBox"/> plus the text boxes tagged as value controls. All six section
    /// panels are declared inline and only collapsed, so one walk sees them all; the lazily
    /// realised permission rows contain neither kind of control.
    /// </summary>
    private List<FrameworkElement> ValueControls()
    {
        if (_valueControls is { Count: > 0 })
        {
            return _valueControls;
        }

        _valueControls = [];
        foreach (var element in Descendants(this))
        {
            switch (element)
            {
                case ComboBox picker:
                    _valueControls.Add(picker);
                    break;
                case TextBox { Tag: "value" } box:
                    _valueControls.Add(box);
                    break;
            }
        }

        return _valueControls;
    }

    private static double Token(string key, double fallback) =>
        Application.Current?.Resources is { } resources &&
        resources.TryGetValue(key, out var value) &&
        value is double typed
            ? typed
            : fallback;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
