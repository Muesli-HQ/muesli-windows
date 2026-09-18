using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Muesli.Windows.Features;
using Muesli.Windows.Services;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;

namespace Muesli.Windows;

/// <summary>
/// Application shell.  Feature state and workflows live in FeatureRuntime; this type owns the
/// window chrome, the content host, and the composition/lifecycle boundary only.
/// </summary>
public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly AppServices _appServices;
    private readonly FeatureShellContext _shellContext;
    private readonly FeatureRuntime _featureRuntime;
    private readonly bool _isVisualPreview;
    private bool _isParkedForBackground;
    private bool _isWorkAreaMaximized;
    private Rect _restoreBounds;
    private bool _disposed;
    private bool _isCompactLayout;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsCompactLayout
    {
        get => _isCompactLayout;
        private set
        {
            if (_isCompactLayout == value)
                return;

            _isCompactLayout = value;
            if (SidebarColumn is not null)
                SidebarColumn.Width = new GridLength(value ? 72 : 260);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCompactLayout)));
        }
    }

    public bool OpenDashboardOnLaunch => _featureRuntime.OpenDashboardOnLaunch;

    public MainWindow()
    {
        _isVisualPreview = false;
        _appServices = AppServices.ForWindow(this);
        InitializeComponent();
        _shellContext = CreateShellContext();
        _featureRuntime = new FeatureRuntime(_shellContext, _appServices);
        DataContext = _featureRuntime;
        HookShellLifecycle();
    }

    private MainWindow(Phase12PreviewMode mode)
    {
        _isVisualPreview = true;
        _appServices = AppServices.ForPreview();
        InitializeComponent();
        _shellContext = CreateShellContext();
        _featureRuntime = FeatureRuntime.CreateVisualPreview(_shellContext, mode);
        DataContext = _featureRuntime;
        HookShellLifecycle();
    }

    internal static MainWindow CreateVisualPreview(Phase12PreviewMode mode) => new(mode);

    private FeatureShellContext CreateShellContext()
    {
        var views = new FeatureViewRegistry(
            DictationsView,
            SearchView,
            MeetingsView,
            MeetingDetailViewControl,
            DictionaryView,
            ModelsView,
            ShortcutsView,
            SettingsView,
            AboutView,
            DictationsNav,
            MeetingsNav,
            AllMeetingsNav,
            DictionaryNav,
            ModelsNav,
            ShortcutsNav,
            SettingsNav,
            AboutNav,
            MeetingsChildren,
            LightThemeButton,
            DarkThemeButton,
            VisualVerificationBanner,
            VisualVerificationBannerText);

        return new FeatureShellContext(this, views, ShowPage, SetCompactLayout);
    }

    private void HookShellLifecycle()
    {
        Loaded += (_, _) =>
        {
            if (!_isVisualPreview)
                _featureRuntime.StartRuntime(showOnboarding: !_isParkedForBackground);
        };
        Closing += (_, _) => DisposeRuntime();
    }

    private void DisposeRuntime()
    {
        if (_disposed)
            return;

        _disposed = true;
        _featureRuntime.Dispose();
        _appServices.Dispose();
    }

    private void SetCompactLayout(bool isCompact)
    {
        IsCompactLayout = isCompact;
    }

    private void ShowPage(AppPage page)
    {
        var views = _shellContext.Views;
        var active = page switch
        {
            AppPage.Search => (UIElement)views.Search,
            AppPage.Meetings => views.Meetings,
            AppPage.MeetingDetail => views.MeetingDetail,
            AppPage.Dictionary => views.Dictionary,
            AppPage.Models => views.Models,
            AppPage.Shortcuts => views.Shortcuts,
            AppPage.Settings => views.Settings,
            AppPage.About => views.About,
            _ => views.Dictations
        };

        foreach (var content in new UIElement[]
                 {
                     views.Dictations,
                     views.Search,
                     views.Meetings,
                     views.MeetingDetail,
                     views.Dictionary,
                     views.Models,
                     views.Shortcuts,
                     views.Settings,
                     views.About
                 })
        {
            content.Visibility = content == active ? Visibility.Visible : Visibility.Collapsed;
        }

        if (page == AppPage.Meetings)
        {
            views.Meetings.BrowserRoot.Visibility = Visibility.Visible;
            views.MeetingDetail.PageRoot.Visibility = Visibility.Collapsed;
        }
        else if (page == AppPage.MeetingDetail)
        {
            views.Meetings.BrowserRoot.Visibility = Visibility.Collapsed;
            views.MeetingDetail.PageRoot.Visibility = Visibility.Visible;
        }

        var activeNavigationPage = page switch
        {
            AppPage.MeetingDetail => AppPage.Meetings,
            AppPage.Search => _appServices.Navigation.State.LastContentPage,
            _ => page
        };
        var activeNav = activeNavigationPage switch
        {
            AppPage.Meetings => views.MeetingsNav,
            AppPage.Dictionary => views.DictionaryNav,
            AppPage.Models => views.ModelsNav,
            AppPage.Shortcuts => views.ShortcutsNav,
            AppPage.Settings => views.SettingsNav,
            AppPage.About => views.AboutNav,
            _ => views.DictationsNav
        };

        foreach (var button in new[]
                 {
                     views.DictationsNav,
                     views.MeetingsNav,
                     views.AllMeetingsNav,
                     views.DictionaryNav,
                     views.ModelsNav,
                     views.ShortcutsNav,
                     views.SettingsNav,
                     views.AboutNav
                 })
        {
            var selected = button == activeNav ||
                           (activeNav == views.MeetingsNav && button == views.AllMeetingsNav);
            button.Background = selected
                ? (WpfBrush)FindResource("SurfaceSelectedBrush")
                : WpfBrushes.Transparent;
            button.Foreground = selected
                ? (WpfBrush)FindResource("TextPrimaryBrush")
                : (WpfBrush)FindResource("TextSecondaryBrush");
            if (FindNavIcon(button) is TextBlock icon)
            {
                icon.Foreground = selected
                    ? (WpfBrush)FindResource("AccentBlueBrush")
                    : (WpfBrush)FindResource("TextSecondaryBrush");
            }
        }

        MeetingsRowChrome.Background = activeNav == views.MeetingsNav
            ? (WpfBrush)FindResource("SurfaceSelectedBrush")
            : WpfBrushes.Transparent;
    }

    private static TextBlock? FindNavIcon(DependencyObject? node)
    {
        switch (node)
        {
            case TextBlock text:
                return text;
            case System.Windows.Controls.Button button:
                return FindNavIcon(button.Content as DependencyObject);
            case null:
                return null;
            default:
                foreach (var child in LogicalTreeHelper.GetChildren(node))
                {
                    if (child is DependencyObject childNode && FindNavIcon(childNode) is { } found)
                        return found;
                }
                return null;
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_isWorkAreaMaximized)
        {
            WindowState = WindowState.Normal;
            var restored = WindowPlacementService.ClampToVisibleWorkArea(this, _restoreBounds);
            Left = restored.Left;
            Top = restored.Top;
            Width = restored.Width;
            Height = restored.Height;
            _isWorkAreaMaximized = false;
            return;
        }

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        _restoreBounds = new Rect(Left, Top, Width, Height);
        var area = WindowPlacementService.GetWorkAreaForWindow(this);
        WindowState = WindowState.Normal;
        Left = area.Left;
        Top = area.Top;
        Width = area.Width;
        Height = area.Height;
        _isWorkAreaMaximized = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_isVisualPreview)
        {
            Close();
            return;
        }

        Hide();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.GetPosition(this).Y < 48)
            DragMove();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        IsCompactLayout = e.NewSize.Width < 900;
        _featureRuntime.UpdateCompactLayout(IsCompactLayout);
    }

    private void Window_Activated(object? sender, EventArgs e)
    {
        if (!_isVisualPreview)
            _featureRuntime.OnWindowActivated();
    }

    private void Window_PreviewKeyDown(object sender, WpfKeyEventArgs e) =>
        _featureRuntime.HandleShellAction(nameof(Window_PreviewKeyDown), sender, e);

    private void ShowDictations_Click(object sender, RoutedEventArgs e) => _featureRuntime.Navigate(AppPage.Dashboard);
    private void ShowMeetings_Click(object sender, RoutedEventArgs e) => _featureRuntime.Navigate(AppPage.Meetings);
    private void ShowDictionary_Click(object sender, RoutedEventArgs e) => _featureRuntime.Navigate(AppPage.Dictionary);
    private void ShowModels_Click(object sender, RoutedEventArgs e) => _featureRuntime.Navigate(AppPage.Models);
    private void ShowShortcuts_Click(object sender, RoutedEventArgs e) => _featureRuntime.Navigate(AppPage.Shortcuts);
    private void ShowSettings_Click(object sender, RoutedEventArgs e) => _featureRuntime.Navigate(AppPage.Settings);
    private void ShowAbout_Click(object sender, RoutedEventArgs e) => _featureRuntime.Navigate(AppPage.About);
    private void ClearSearch_Click(object sender, RoutedEventArgs e) => _featureRuntime.ClearSearch();

    private void ToggleMeetings_Click(object sender, RoutedEventArgs e) => ForwardShellAction(nameof(ToggleMeetings_Click), sender, e);
    private void AddMeetingFolder_Click(object sender, RoutedEventArgs e) => ForwardShellAction(nameof(AddMeetingFolder_Click), sender, e);
    private void SelectMeetingFolder_Click(object sender, RoutedEventArgs e) => ForwardShellAction(nameof(SelectMeetingFolder_Click), sender, e);
    private void RenameMeetingFolder_Click(object sender, RoutedEventArgs e) => ForwardShellAction(nameof(RenameMeetingFolder_Click), sender, e);
    private void MoveMeetingFolderMenu_SubmenuOpened(object sender, RoutedEventArgs e) => ForwardShellAction(nameof(MoveMeetingFolderMenu_SubmenuOpened), sender, e);
    private void MoveMeetingFolderUp_Click(object sender, RoutedEventArgs e) => ForwardShellAction(nameof(MoveMeetingFolderUp_Click), sender, e);
    private void MoveMeetingFolderDown_Click(object sender, RoutedEventArgs e) => ForwardShellAction(nameof(MoveMeetingFolderDown_Click), sender, e);
    private void DeleteMeetingFolder_Click(object sender, RoutedEventArgs e) => ForwardShellAction(nameof(DeleteMeetingFolder_Click), sender, e);
    private void OpenSearchFromCompactRail_Click(object sender, RoutedEventArgs e) => ForwardShellAction(nameof(OpenSearchFromCompactRail_Click), sender, e);
    private void FolderNameBox_KeyDown(object sender, WpfKeyEventArgs e) => ForwardShellAction(nameof(FolderNameBox_KeyDown), sender, e);
    private void FolderNameBox_LostFocus(object sender, RoutedEventArgs e) => ForwardShellAction(nameof(FolderNameBox_LostFocus), sender, e);

    private void SetLightTheme_Click(object sender, RoutedEventArgs e) => _featureRuntime.SetTheme("light");
    private void SetDarkTheme_Click(object sender, RoutedEventArgs e) => _featureRuntime.SetTheme("dark");

    private void ForwardShellAction(string actionName, object sender, RoutedEventArgs e) =>
        _featureRuntime.HandleShellAction(actionName, sender, e);

    public void StartRuntime(bool showOnboarding) => _featureRuntime.StartRuntime(showOnboarding);
    public void SetBackgroundStatus() => _featureRuntime.SetBackgroundStatus();
    public void ParkForBackgroundLaunch()
    {
        _isParkedForBackground = true;
        _featureRuntime.ParkForBackgroundLaunch();
    }

    public void ShowDashboardFromBackground() => _featureRuntime.ShowDashboardFromBackground();

    protected override void OnClosed(EventArgs e)
    {
        DisposeRuntime();
        base.OnClosed(e);
    }

}
