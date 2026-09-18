using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Muesli.Windows.Features.About;
using Muesli.Windows.Features.Dictionary;
using Muesli.Windows.Features.Dictations;
using Muesli.Windows.Features.Meetings;
using Muesli.Windows.Features.Models;
using Muesli.Windows.Features.Search;
using Muesli.Windows.Features.Settings;
using Muesli.Windows.Features.Shortcuts;
using Muesli.Windows.Services;
using WpfButton = System.Windows.Controls.Button;

namespace Muesli.Windows.Features;

/// <summary>
/// The controls and shell handles a feature runtime may use.  The runtime deliberately knows
/// about a Window, but never about a concrete shell type, so the shell can be replaced by preview
/// composition without bringing in the production window type.
/// </summary>
public sealed class FeatureViewRegistry
{
    public FeatureViewRegistry(
        DictationsView dictations,
        SearchView search,
        MeetingsView meetings,
        MeetingDetailView meetingDetail,
        DictionaryView dictionary,
        ModelsView models,
        ShortcutsView shortcuts,
        SettingsView settings,
        AboutView about,
        WpfButton dictationsNav,
        WpfButton meetingsNav,
        WpfButton allMeetingsNav,
        WpfButton dictionaryNav,
        WpfButton modelsNav,
        WpfButton shortcutsNav,
        WpfButton settingsNav,
        WpfButton aboutNav,
        StackPanel meetingsChildren,
        WpfButton lightThemeButton,
        WpfButton darkThemeButton,
        Border visualVerificationBanner,
        TextBlock visualVerificationBannerText)
    {
        Dictations = dictations;
        Search = search;
        Meetings = meetings;
        MeetingDetail = meetingDetail;
        Dictionary = dictionary;
        Models = models;
        Shortcuts = shortcuts;
        Settings = settings;
        About = about;
        DictationsNav = dictationsNav;
        MeetingsNav = meetingsNav;
        AllMeetingsNav = allMeetingsNav;
        DictionaryNav = dictionaryNav;
        ModelsNav = modelsNav;
        ShortcutsNav = shortcutsNav;
        SettingsNav = settingsNav;
        AboutNav = aboutNav;
        MeetingsChildren = meetingsChildren;
        LightThemeButton = lightThemeButton;
        DarkThemeButton = darkThemeButton;
        VisualVerificationBanner = visualVerificationBanner;
        VisualVerificationBannerText = visualVerificationBannerText;
    }

    public DictationsView Dictations { get; }
    public SearchView Search { get; }
    public MeetingsView Meetings { get; }
    public MeetingDetailView MeetingDetail { get; }
    public DictionaryView Dictionary { get; }
    public ModelsView Models { get; }
    public ShortcutsView Shortcuts { get; }
    public SettingsView Settings { get; }
    public AboutView About { get; }
    public WpfButton DictationsNav { get; }
    public WpfButton MeetingsNav { get; }
    public WpfButton AllMeetingsNav { get; }
    public WpfButton DictionaryNav { get; }
    public WpfButton ModelsNav { get; }
    public WpfButton ShortcutsNav { get; }
    public WpfButton SettingsNav { get; }
    public WpfButton AboutNav { get; }
    public StackPanel MeetingsChildren { get; }
    public WpfButton LightThemeButton { get; }
    public WpfButton DarkThemeButton { get; }
    public Border VisualVerificationBanner { get; }
    public TextBlock VisualVerificationBannerText { get; }
}

public interface IFeatureShellContext
{
    Window Window { get; }
    Dispatcher Dispatcher { get; }
    FeatureViewRegistry Views { get; }
    object FindResource(object key);
    void ShowPage(AppPage page);
    void SetCompactLayout(bool isCompact);
    void FocusWindow();
}

/// <summary>
/// Small adapter used by the application shell and by isolated preview composition.  It owns no feature
/// state; it only exposes shell capabilities to the feature runtime.
/// </summary>
public sealed class FeatureShellContext : IFeatureShellContext
{
    private readonly Action<AppPage> _showPage;
    private readonly Action<bool> _setCompactLayout;

    public FeatureShellContext(
        Window window,
        FeatureViewRegistry views,
        Action<AppPage> showPage,
        Action<bool> setCompactLayout)
    {
        Window = window ?? throw new ArgumentNullException(nameof(window));
        Views = views ?? throw new ArgumentNullException(nameof(views));
        _showPage = showPage ?? throw new ArgumentNullException(nameof(showPage));
        _setCompactLayout = setCompactLayout ?? throw new ArgumentNullException(nameof(setCompactLayout));
    }

    public Window Window { get; }
    public Dispatcher Dispatcher => Window.Dispatcher;
    public FeatureViewRegistry Views { get; }
    public object FindResource(object key) => Window.FindResource(key);
    public void ShowPage(AppPage page) => _showPage(page);
    public void SetCompactLayout(bool isCompact) => _setCompactLayout(isCompact);
    public void FocusWindow() => Window.Focus();
}
