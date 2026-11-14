using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Muesli.Windows.Core.Services;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.ViewModels;
using Windows.System;
using Windows.UI.ViewManagement;

namespace Muesli.Windows.WinUI;

/// <summary>
/// Mac-inspired library shell shared by every WinUI page.
/// </summary>
public sealed partial class MainPage : Page
{
    public MainPageViewModel ViewModel { get; } = new(App.Library);

    /// <summary>The sidebar download row, fed by the app-scoped model lifecycle.</summary>
    public SidebarDownloadStatusViewModel Downloads { get; } = new(() => App.Models, () => App.UiDispatcher);

    private int _tourIndex = -1;
    private bool _meetingFoldersExpanded = true;
    private bool _compactRail;
    /// <summary>
    /// User-requested rail collapse (the macOS sidebar toggle). Session-scoped on purpose: the
    /// shell has no persisted sidebar preference and adding one would change the settings
    /// contract, which is outside this shell change.
    /// </summary>
    private bool _railCollapsed;
    private string _selectedTag = "timeline";

    /// <summary>
    /// Sentinel selection used while search results are shown. It matches no rail button, so the
    /// rail highlights nothing instead of leaving the last page lit under someone else's results
    /// (P2-07).
    /// </summary>
    private const string SearchTag = "search";

    /// <summary>The route to return to when the query is cleared.</summary>
    private string _tagBeforeSearch = "timeline";

    /// <summary>Set while the shell clears the query itself, so clearing does not re-route.</summary>
    private bool _suppressSearchRouting;

    public MainPage()
    {
        InitializeComponent();
        AutomationProperties.SetLiveSetting(TourTitle, AutomationLiveSetting.Polite);
        RefreshGreeting();
        RebuildMeetingFolders();
        RefreshNavigationVisuals();

        // Keep the shell's greeting, selected pill, and icon tint in sync with theme/profile
        // changes. The dispatcher also makes this safe when a settings write completes off-thread.
        App.Settings.Changed += OnSettingsChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ShellRoot.SizeChanged += ShellRoot_SizeChanged;
        ShellRoot.ActualThemeChanged += (_, _) =>
        {
            ApplyTourOverlayChrome();
            RefreshNavigationVisuals();
            DrawBrandMark();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.Settings.Changed -= OnSettingsChanged;
        App.Settings.Changed += OnSettingsChanged;
        ShellRoot.SizeChanged -= ShellRoot_SizeChanged;
        ShellRoot.SizeChanged += ShellRoot_SizeChanged;
        ApplyResponsiveLayout(ShellRoot.ActualWidth);
        Navigate(_selectedTag);
        RefreshNavigationVisuals();
        DrawBrandMark();
        // Queued so it runs after App.OnLaunched has created the model services.
        DispatcherQueue.TryEnqueue(Downloads.Start);
        XamlRoot.Changed -= OnXamlRootChanged;
        XamlRoot.Changed += OnXamlRootChanged;
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => DrawBrandMark();

    /// <summary>
    /// The app icon's mark as tall as the "muesli" title beside it, as on the macOS sidebar,
    /// with each bar and gap a whole number of physical pixels.
    /// </summary>
    /// <summary>
    /// Draws the brand mark as 13 rounded bars on whole pixels. The previous version drew it 22 tall
    /// (wider than its 22-pt column, so it crowded the wordmark), rounded bar and gap separately (so
    /// the mark lost the icon's proportions), and used stroke caps that render as flat half-tone tips.
    /// </summary>
    private void DrawBrandMark()
    {
        var scale = XamlRoot?.RasterizationScale is > 0 and var s ? s : 1;
        var mark = FloatingIndicatorLayout.ProportionalBrandMark(FloatingIndicatorLayout.SidebarBrandMarkHeight, scale);
        var fill = Brush("MuesliAccentBrush");
        BrandMark.Children.Clear();
        BrandMark.Width = mark.Width;
        BrandMark.Height = mark.Height;
        for (var index = 0; index < mark.Heights.Length; index++)
        {
            var bar = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = mark.BarWidth,
                Height = mark.Heights[index],
                RadiusX = mark.BarWidth / 2,
                RadiusY = mark.BarWidth / 2,
                Fill = fill
            };
            Canvas.SetLeft(bar, index * (mark.BarWidth + mark.Gap));
            Canvas.SetTop(bar, (mark.Height - mark.Heights[index]) / 2);
            BrandMark.Children.Add(bar);
        }

        // The greeting starts under the wordmark: mark width plus the 12-pt gap, as on macOS.
        if (!_compactRail && BrandRow.ColumnDefinitions.Count > 0)
        {
            BrandRow.ColumnDefinitions[0].Width = new GridLength(mark.Width);
        }

        GreetingText.Margin = new Thickness(mark.Width + 12, 2, 0, 0);
    }

    private void ShellRoot_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize.Width);

    /// <summary>
    /// Keep the shell usable from the requested 720-DIP minimum through wide desktop windows.
    /// The compact rail is icon-first, but all routes remain real buttons with their original
    /// automation names and IDs.
    /// </summary>
    private void ApplyResponsiveLayout(double width)
    {
        // Window chrome can make Page.ActualWidth a few DIPs smaller than the requested
        // outer size. Breakpoints follow the window's effective size so 1008 DIP is medium
        // (labeled rail) rather than collapsing to the 72-DIP compact column.
        MainWindow? hostWindow = App.Window as MainWindow;
        if (hostWindow is not null)
        {
            width = hostWindow.CurrentWidthDip;
        }

        var narrow = width < 1008;
        var compact = narrow || _railCollapsed;
        var medium = !compact && width < 1280;
        var sidebarWidth = compact
            ? Token("MuesliSidebarWidthCompact", 68)
            : medium
                ? Token("MuesliSidebarWidthMedium", 240)
                : Token("MuesliSidebarWidthExpanded", 240);
        var caption = Token("MuesliTitleBarHeight", 32);

        _compactRail = compact;
        NavigationColumn.Width = new GridLength(sidebarWidth);
        ContentHost.Padding = new Thickness(0, caption, 0, 0);
        ContentFrame.Margin = new Thickness(0, 0, 0, 0);
        // The rail's header starts below the caption strip, so the strip drags across the whole
        // window, as the macOS titlebar spans sidebar and content.
        hostWindow?.SetTitleBarInset(0);

        BrandNameText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RefreshGreeting();
        // The narrow rail keeps the download ring (its tooltip carries the title) and drops the text.
        DownloadStatusText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        DownloadStatusRow.HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        DownloadStatusIcon.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        AppearanceToggle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        NavigationHeader.Margin = compact ? new Thickness(8, 40, 8, 8) : new Thickness(16, 40, 16, 8);
        SearchBox.PlaceholderText = compact ? string.Empty : "Search…";
        SearchShortcutHint.Visibility = compact || SearchBox.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        SearchGlyph.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (compact)
        {
            ToolTipService.SetToolTip(SearchWell, "Search dictations and meetings");
        }
        else
        {
            SearchWell.ClearValue(ToolTipService.ToolTipProperty);
        }

        if (BrandRow.ColumnDefinitions.Count > 0)
        {
            BrandRow.ColumnDefinitions[0].Width = compact
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(BrandMark.Width > 0 ? BrandMark.Width : 22);
            BrandRow.ColumnSpacing = compact ? 0 : 12;
            BrandMark.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        }

        // The collapse control only exists where collapsing is a choice. Below 1008 DIP the rail
        // is already compact for want of width, so the toggle would promise a state the window
        // cannot show. When collapsed it drops to its own centred row so the brand mark keeps
        // the top row of the 72-DIP header.
        SidebarCollapseButton.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(SidebarCollapseButton, compact ? 1 : 0);
        Grid.SetColumn(SidebarCollapseButton, compact ? 0 : 2);
        Grid.SetColumnSpan(SidebarCollapseButton, compact ? 3 : 1);
        SidebarCollapseButton.Margin = compact ? new Thickness(0, 6, 0, 0) : new Thickness(0);
        // Segoe Fluent: OpenPane (E89F) when collapsed, ClosePane (E8A0) when expanded.
        SidebarCollapseIcon.Glyph = _railCollapsed ? "\uE89F" : "\uE8A0";
        var collapseLabel = _railCollapsed ? "Expand sidebar" : "Collapse sidebar";
        AutomationProperties.SetName(SidebarCollapseButton, collapseLabel);
        ToolTipService.SetToolTip(SidebarCollapseButton, collapseLabel);

        MeetingChevronButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CreateMeetingFolderButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (MeetingsRow.ColumnDefinitions.Count >= 3)
        {
            MeetingsRow.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(36);
            MeetingsRow.ColumnDefinitions[2].Width = compact ? new GridLength(0) : new GridLength(36);
        }

        MeetingsRow.Margin = compact ? new Thickness(4, 2, 4, 2) : new Thickness(8, 2, 8, 2);
        MeetingChildrenHost.Visibility = compact
            ? Visibility.Collapsed
            : _meetingFoldersExpanded ? Visibility.Visible : Visibility.Collapsed;
        MeetingChildrenHost.Margin = compact ? new Thickness(0, 0, 0, 0) : new Thickness(24, 0, 0, 0);

        foreach (var button in AllNavItems())
        {
            ApplyNavButtonCompact(button, compact);
        }

        ApplyTourOverlayChrome();
    }

    private void ApplyNavButtonCompact(Button button, bool compact)
    {
        button.HorizontalContentAlignment = compact
            ? HorizontalAlignment.Center
            : HorizontalAlignment.Stretch;
        button.UseSystemFocusVisuals = true;

        if (!ReferenceEquals(button, MeetingsNav))
        {
            if (compact)
            {
                button.Margin = new Thickness(4, 2, 4, 2);
                button.Padding = new Thickness(0, 0, 0, 0);
            }
            else
            {
                button.ClearValue(FrameworkElement.MarginProperty);
                button.ClearValue(Control.PaddingProperty);
            }
        }

        if (button.Content is Grid grid && grid.ColumnDefinitions.Count > 0)
        {
            // 20-pt icon column plus the 12-pt gap to the label (macOS HStack spacing 12).
            grid.ColumnDefinitions[0].Width = compact
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(32);
            foreach (var icon in grid.Children.OfType<IconElement>())
            {
                icon.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            }

            foreach (var text in grid.Children.OfType<TextBlock>())
            {
                text.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        button.KeyDown -= NavButton_KeyDown;
        button.KeyDown += NavButton_KeyDown;
    }

    private void NavButton_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not Button || e.Key is not (VirtualKey.Up or VirtualKey.Down))
        {
            return;
        }

        var items = VisibleNavItems().ToList();
        var index = items.IndexOf((Button)sender);
        if (index < 0)
        {
            return;
        }

        var next = e.Key == VirtualKey.Down ? index + 1 : index - 1;
        if (next < 0 || next >= items.Count)
        {
            return;
        }

        items[next].Focus(FocusState.Keyboard);
        e.Handled = true;
    }

    private static double Token(string key, double fallback) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is double parsed
            ? parsed
            : fallback;

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.Settings.Changed -= OnSettingsChanged;
        ShellRoot.SizeChanged -= ShellRoot_SizeChanged;
        Downloads.Stop();
    }

    private void OnSettingsChanged(object? sender, Muesli.Windows.Services.MuesliSettings settings)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshGreeting();
            // App.TrackTheme queues the RequestedTheme change from the same settings event.
            // Refresh on the following dispatcher turn so resource lookup sees the new Light,
            // Dark, or High Contrast dictionary instead of retaining the previous theme tint.
            DispatcherQueue.TryEnqueue(() =>
            {
                RebuildMeetingFolders();
                ApplyResponsiveLayout(ShellRoot.ActualWidth);
                RefreshNavigationVisuals();
                DrawBrandMark();
            });
        });
    }

    public void StartFeatureTour()
    {
        _tourIndex = 0;
        RenderTourStep();
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tag)
        {
            DismissSearchQuery();
            _selectedTag = tag;
            RefreshNavigationVisuals();
            Navigate(tag);
        }
    }

    private void Navigate(string tag)
    {
        if (tag.StartsWith("meetings", StringComparison.OrdinalIgnoreCase))
        {
            ContentFrame.Navigate(typeof(Pages.MeetingsPage), tag);
            return;
        }

        var pageType = tag switch
        {
            "timeline" => typeof(Pages.TimelinePage),
            "dictations" => typeof(Pages.DictationsPage),
            "dictionary" => typeof(Pages.DictionaryPage),
            "models" => typeof(Pages.ModelsPage),
            "settings" => typeof(Pages.SettingsPage),
            "insights" => typeof(Pages.InsightsPage),
            "shortcuts" => typeof(Pages.ShortcutsPage),
            "about" => typeof(Pages.AboutPage),
            // P7-03. This used to fall back to LibraryInfoPage, a placeholder that re-derived its
            // own library metrics for tags that all now have real pages. No nav item produces an
            // unmatched tag, so it was an unreachable dead route publishing a second, divergent
            // source of the Insights numbers. The page is gone; an unknown tag lands on the shell's
            // home route instead of a page that contradicts the rest of the app.
            _ => typeof(Pages.TimelinePage)
        };

        ContentFrame.Navigate(pageType, tag);
    }

    public void NavigateTo(string tag)
    {
        DismissSearchQuery();

        // A folder tag is created dynamically from the local profile. Selecting it is still a
        // normal button invocation, so automation and feature-tour navigation behave identically.
        foreach (var candidate in AllNavItems())
        {
            if (string.Equals(candidate.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                _selectedTag = tag;
                RefreshNavigationVisuals();
                Navigate(tag);
                return;
            }
        }

        _selectedTag = tag;
        RefreshNavigationVisuals();
        Navigate(tag);
    }

    /// <summary>
    /// The shell's search field is the app's single search entry point (P2-05): pages no longer
    /// carry their own. While results are shown the rail selection moves to a sentinel so no
    /// destination is falsely highlighted (P2-07), and clearing the query returns to whichever
    /// route the user searched from.
    /// </summary>
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        SearchClearButton.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        SearchShortcutHint.Visibility = query.Length == 0 && !_compactRail && SearchBox.FocusState == FocusState.Unfocused ? Visibility.Visible : Visibility.Collapsed;

        if (_suppressSearchRouting)
        {
            return;
        }

        if (query.Length == 0)
        {
            if (ContentFrame.Content is Pages.SearchPage)
            {
                var restore = _tagBeforeSearch.StartsWith("meetings:", StringComparison.OrdinalIgnoreCase)
                    ? "meetings"
                    : _tagBeforeSearch;
                _selectedTag = restore;
                RefreshNavigationVisuals();
                Navigate(restore);
            }

            return;
        }

        if (ContentFrame.Content is Pages.SearchPage page)
        {
            page.SetQuery(query);
            return;
        }

        _tagBeforeSearch = _selectedTag;
        _selectedTag = SearchTag;
        RefreshNavigationVisuals();
        ContentFrame.Navigate(typeof(Pages.SearchPage), query);
    }

    /// <summary>
    /// Moves the rail highlight to <paramref name="tag"/> without navigating, for the case where a
    /// page has already navigated the content frame itself — activating a Timeline row or a search
    /// result. Without it the rail keeps highlighting the route the user activated *from* (or, out
    /// of search, nothing at all) while a different destination is on screen. Leaving search this
    /// way drops the query, exactly as picking a rail destination does.
    /// </summary>
    public void SyncRailSelection(string tag)
    {
        DismissSearchQuery();
        _selectedTag = tag;
        RefreshNavigationVisuals();
    }

    /// <summary>Clears the query and routes back, from the sidebar button or the Search page.</summary>
    public void ClearSearch()
    {
        SearchBox.Text = string.Empty;
        SearchBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Drops a stale query when the user leaves search by picking a destination instead of
    /// clearing. Routing is suppressed so this does not fight the navigation already under way.
    /// </summary>
    private void DismissSearchQuery()
    {
        if (SearchBox.Text.Length == 0)
        {
            return;
        }

        _suppressSearchRouting = true;
        try
        {
            SearchBox.Text = string.Empty;
        }
        finally
        {
            _suppressSearchRouting = false;
        }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) => ClearSearch();

    /// <summary>
    /// P1-01: the search field no longer draws the platform focus underline inside the well
    /// (see <c>MuesliSearchTextBoxStyle</c>), so the well itself carries the focus signal. This
    /// is the shell's own well; the in-page search wells are owned by their pages.
    /// </summary>
    private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        // macOS keyboard focus: a 2-pt accentText ring on the control's own shape.
        SearchWell.Background = Brush("MuesliHoverBrush");
        SearchWell.BorderBrush = Brush("MuesliAccentTextBrush");
        SearchWell.BorderThickness = new Thickness(2);
        SearchShortcutHint.Visibility = Visibility.Collapsed;
    }

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        SearchWell.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        SearchWell.ClearValue(Border.BorderBrushProperty);
        SearchWell.ClearValue(Border.BorderThicknessProperty);
        SearchShortcutHint.Visibility = _compactRail || SearchBox.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // The quiet launcher's hover: textPrimary at 6% over the rail, as macOS .quiet buttons.
    private void SearchWell_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (SearchBox.FocusState == FocusState.Unfocused) SearchWell.Background = Brush("MuesliStateHoverOverlayBrush");
    }

    private void SearchWell_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (SearchBox.FocusState == FocusState.Unfocused) SearchWell.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void FocusSearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_railCollapsed)
        {
            _railCollapsed = false;
            ApplyResponsiveLayout(ShellRoot.ActualWidth);
        }

        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
        args.Handled = true;
    }

    private void AppearanceSegment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string theme }) return;
        var current = App.Settings.Load();
        if (string.Equals(current.Theme, theme, StringComparison.OrdinalIgnoreCase)) return;
        App.Settings.Save(current with { Theme = theme });
    }

    /// <summary>The selected appearance segment is tinted (accent 15%, accentText, outline).</summary>
    private void RefreshAppearanceToggle()
    {
        var theme = (App.Settings.Load().Theme ?? "system").Trim().ToLowerInvariant();
        if (theme is not ("light" or "dark")) theme = "system";
        foreach (var segment in new[] { AppearanceLight, AppearanceSystem, AppearanceDark })
        {
            var selected = string.Equals(segment.Tag as string, theme, StringComparison.Ordinal);
            segment.Background = selected ? Brush("MuesliAccentMutedBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            segment.Foreground = selected ? Brush("MuesliAccentTextBrush") : Brush("MuesliTextSecondaryBrush");
            segment.BorderBrush = selected ? Brush("MuesliTextSecondaryBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private void ToggleSidebarCollapse_Click(object sender, RoutedEventArgs e)
    {
        _railCollapsed = !_railCollapsed;
        ApplyResponsiveLayout(ShellRoot.ActualWidth);
        SidebarCollapseButton.Focus(FocusState.Programmatic);
    }

    private void ToggleMeetingFolders_Click(object sender, RoutedEventArgs e)
    {
        _meetingFoldersExpanded = !_meetingFoldersExpanded;
        if (!_compactRail)
        {
            MeetingChildrenHost.Visibility = _meetingFoldersExpanded ? Visibility.Visible : Visibility.Collapsed;
        }

        // Segoe Fluent ChevronDownSmall / ChevronRightSmall (macOS chevron.down / chevron.right 11).
        MeetingChevronIcon.Glyph = _meetingFoldersExpanded ? "\uE96E" : "\uE970";
    }

    private void CreateMeetingFolder_Click(object sender, RoutedEventArgs e)
    {
        // Folder create/rename/validation lives on the Meetings page. The rail control opens that
        // page instead of inventing a second persistence path with no name prompt, and then opens
        // the folder editor on it — the editor is collapsed by default since P3-02, so navigating
        // alone would leave the "+" pointing at nothing the user can see.
        NavigateTo("meetings");
        if (ContentFrame.Content is Pages.MeetingsPage page)
        {
            page.RevealFolderEditor();
        }
    }

    private void TourNext_Click(object sender, RoutedEventArgs e)
    {
        if (_tourIndex < 0) return;
        if (_tourIndex >= FeatureTourCatalog.Steps.Count - 1)
        {
            CompleteTour();
            return;
        }

        _tourIndex++;
        RenderTourStep();
    }

    private void TourSkip_Click(object sender, RoutedEventArgs e) => CompleteTour();

    private void TourEscape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_tourIndex < 0)
        {
            return;
        }

        CompleteTour();
        args.Handled = true;
    }

    private void RenderTourStep()
    {
        if (_tourIndex < 0 || _tourIndex >= FeatureTourCatalog.Steps.Count)
        {
            TourOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        var step = FeatureTourCatalog.Steps[_tourIndex];
        TourStepLabel.Text = $"TOUR · {_tourIndex + 1} OF {FeatureTourCatalog.Steps.Count}";
        TourTitle.Text = step.Title;
        TourBody.Text = step.Body;
        TourNextButton.Content = _tourIndex == FeatureTourCatalog.Steps.Count - 1 ? "Done" : "Next";
        TourOverlay.Visibility = Visibility.Visible;
        ApplyTourOverlayChrome();
        if (!string.Equals(step.TargetName, "FloatingIndicator", StringComparison.Ordinal))
        {
            NavigateTo(step.TargetName);
        }

        _ = TourNextButton.Focus(FocusState.Programmatic);
    }

    private void ApplyTourOverlayChrome()
    {
        if (new AccessibilitySettings().HighContrast)
        {
            // High Contrast dictionaries may only alias SystemColor* brushes, and those brushes
            // cannot take Opacity. An opaque window-text scrim would hide the page, so the tour
            // card uses a transparent overlay and a strong system border instead.
            TourOverlay.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            TourCard.BorderThickness = new Thickness(2, 2, 2, 2);
            TourCard.BorderBrush = Brush("MuesliBorderStrongBrush");
            return;
        }

        TourOverlay.ClearValue(Panel.BackgroundProperty);
        TourCard.ClearValue(Border.BorderThicknessProperty);
        TourCard.ClearValue(Border.BorderBrushProperty);
    }

    private void CompleteTour()
    {
        _tourIndex = -1;
        TourOverlay.Visibility = Visibility.Collapsed;
        var current = App.Settings.Load() with
        {
            LastCompletedFeatureTourVersion = FeatureTourCatalog.CurrentVersion
        };
        App.Settings.Save(current);

        VisibleNavItems()
            .FirstOrDefault(button =>
                string.Equals(button.Tag?.ToString(), _selectedTag, StringComparison.OrdinalIgnoreCase))
            ?.Focus(FocusState.Programmatic);
    }

    private void RefreshGreeting()
    {
        // macOS shows "Hi, name" only when there is a name.
        var name = App.Settings.Load().UserName?.Trim() ?? string.Empty;
        GreetingText.Text = string.IsNullOrWhiteSpace(name) ? string.Empty : $"Hi, {name}";
        GreetingText.Visibility = _compactRail || string.IsNullOrWhiteSpace(name) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Rebuilds the rail's folder rows from the library and optionally moves the rail highlight.
    /// The Meetings page calls this after a folder is created, renamed, moved or deleted: the rail
    /// was previously rebuilt only when *settings* changed, so a folder saved on the page stayed
    /// missing (or kept its old name and nesting) in the sidebar until the app was restarted.
    /// Unlike <see cref="NavigateTo"/> this neither navigates nor clears the shell search query.
    /// </summary>
    public void RefreshMeetingFolders(string? selectedTag = null)
    {
        RebuildMeetingFolders();
        if (selectedTag is not null)
        {
            _selectedTag = selectedTag;
        }

        RefreshNavigationVisuals();
    }

    /// <summary>
    /// The rail's folder rows come from <see cref="MeetingFolderListItem.BuildTree"/>, the same
    /// builder the Meetings page uses, so depth, labels and counts are one calculation shown twice
    /// rather than two loops that can disagree (P3-04, P3-05).
    /// </summary>
    private void RebuildMeetingFolders()
    {
        MeetingChildrenHost.Children.Clear();

        foreach (var folder in MeetingFolderListItem.BuildTree(App.Library.ReadSnapshot()))
        {
            var tag = folder.IsAllMeetings ? "meetings" : $"meetings:{folder.Id}";
            MeetingChildrenHost.Children.Add(CreateFolderButton(folder, tag));
        }

        foreach (var child in MeetingChildrenHost.Children.OfType<Button>())
        {
            ApplyNavButtonCompact(child, _compactRail);
        }
    }

    private Button CreateFolderButton(MeetingFolderListItem folder, string tag)
    {
        var name = folder.Name;
        var count = folder.Count;
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["MuesliSidebarChildButtonStyle"],
            Tag = tag,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            UseSystemFocusVisuals = true,
        };
        button.Click += NavButton_Click;
        // P3-01/P3-04: the rail announces the same sentence the in-page folder list does, count
        // rule included, instead of just the folder name with an unexplained number beside it.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Show {folder.AccessibleName}");
        ToolTipService.SetToolTip(button, folder.AccessibleName);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, tag.StartsWith("meetings:", StringComparison.Ordinal)
            ? $"MeetingFolder_{tag["meetings:".Length..]}"
            : "MeetingFolder_All");

        // Nesting depth is applied to the button's CONTENT, not to the button. ApplyNavButtonCompact
        // calls ClearValue(MarginProperty) on every rail button on each layout pass, which silently
        // flattened the whole folder tree to one indent level while the in-page list stayed nested.
        // macOS: 16 per nesting level, icon column 20 + 12, count centred in a 24-min column.
        var content = new Grid
        {
            Margin = new Thickness(Math.Min(folder.Depth, 8) * 16, 0, 0, 0)
        };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 24 });

        // All Meetings is macOS tray.2 (Segoe MapLayers, stacked trays); folders are folder.
        var icon = new FontIcon
        {
            Glyph = folder.IsAllMeetings ? "\uE81E" : "\uE8B7",
            Style = (Style)Application.Current.Resources["MuesliSidebarIconStyle"]
        };
        Grid.SetColumn(icon, 0);
        content.Children.Add(icon);

        var label = new TextBlock
        {
            Text = name,
            FontFamily = (FontFamily)Application.Current.Resources["MuesliFontFamily"],
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);
        content.Children.Add(label);

        // P3-05. This used to assign Brush("MuesliTextTertiaryBrush") — a *snapshot* of whichever
        // theme dictionary was current when the row was built. RebuildMeetingFolders runs from the
        // settings-changed handler, one dispatcher turn before ActualTheme flips, so switching to
        // Light rebuilt the rows holding the Dark brush (#66FFFFFF) and painted white-on-white:
        // the counts vanished while the identical numbers stayed visible on the Meetings page.
        // Nothing re-resolved them, because RefreshNavigationVisuals only retouches the button and
        // its icon. MuesliSidebarCountStyle carries {ThemeResource MuesliTextTertiaryBrush}
        // (App.xaml:291-297), which re-resolves on every theme change on its own.
        var countLabel = new TextBlock
        {
            Text = CompactFolderCount(count),
            Style = (Style)Application.Current.Resources["MuesliSidebarCountStyle"],
            HorizontalAlignment = HorizontalAlignment.Center
        };
        AutomationProperties.SetAccessibilityView(countLabel, AccessibilityView.Raw);
        Grid.SetColumn(countLabel, 2);
        content.Children.Add(countLabel);

        button.Content = content;
        return button;
    }

    /// <summary>macOS sidebar folder counts: as-is under 1000, "1.2k" under 10000, then "12k".</summary>
    internal static string CompactFolderCount(int count) => count switch
    {
        < 1000 => count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        < 10000 => (Math.Floor(count / 100d) / 10).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "k",
        _ => (count / 1000).ToString(System.Globalization.CultureInfo.InvariantCulture) + "k"
    };

    /// <summary>
    /// macOS row states: selected rows get surfaceSelected, a semibold primary label and an
    /// accentText icon; others are medium, textSecondary, with folder icons in textTertiary.
    /// </summary>
    private void RefreshNavigationVisuals()
    {
        var selectedBackground = Brush("MuesliSelectedBrush");
        var primary = Brush("MuesliTextPrimaryBrush");
        var secondary = Brush("MuesliTextSecondaryBrush");
        var tertiary = Brush("MuesliTextTertiaryBrush");
        var accentText = Brush("MuesliAccentTextBrush");
        var medium = (FontFamily)Application.Current.Resources["MuesliFontFamilyMedium"];
        var semiBold = (FontFamily)Application.Current.Resources["MuesliFontFamilySemiBold"];

        foreach (var button in AllNavItems())
        {
            var tag = button.Tag?.ToString() ?? string.Empty;
            var selected = string.Equals(tag, _selectedTag, StringComparison.OrdinalIgnoreCase);
            var folderRow = MeetingChildrenHost.Children.Contains(button);
            button.Background = selected ? selectedBackground : null;
            button.Foreground = selected ? primary : secondary;
            if (!folderRow)
            {
                button.FontFamily = selected ? semiBold : medium;
                button.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Medium;
            }

            if (button.Content is Grid grid)
            {
                foreach (var icon in grid.Children.OfType<IconElement>())
                {
                    icon.Foreground = selected ? accentText : folderRow ? tertiary : secondary;
                }
            }
        }

        // The Meetings row's action glyphs read secondary while Meetings is selected.
        var meetingsSelected = _selectedTag.StartsWith("meetings", StringComparison.OrdinalIgnoreCase);
        MeetingChevronButton.Foreground = meetingsSelected ? secondary : tertiary;
        CreateMeetingFolderButton.Foreground = meetingsSelected ? secondary : tertiary;
        RefreshAppearanceToggle();
    }

    private SolidColorBrush Brush(string key) =>
        Themes.ThemeBrushes.Get(key, ActualTheme) as SolidColorBrush ?? new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private IEnumerable<Button> VisibleNavItems()
    {
        foreach (var button in AllNavItems())
        {
            if (button.Visibility != Visibility.Visible)
            {
                continue;
            }

            if (MeetingChildrenHost.Children.Contains(button) &&
                MeetingChildrenHost.Visibility != Visibility.Visible)
            {
                continue;
            }

            yield return button;
        }
    }

    private IEnumerable<Button> AllNavItems()
    {
        yield return TimelineNav;
        yield return DictationsNav;
        yield return MeetingsNav;
        foreach (var child in MeetingChildrenHost.Children.OfType<Button>())
        {
            yield return child;
        }

        // Insights is not a sidebar destination on macOS; the Timeline stat cards open it.
        yield return DictionaryNav;
        yield return ModelsNav;
        yield return ShortcutsNav;
        yield return SettingsNav;
        yield return AboutNav;
    }
}
