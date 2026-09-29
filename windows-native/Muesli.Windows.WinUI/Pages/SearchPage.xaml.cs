using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Muesli.Windows.WinUI.ViewModels;
using Windows.UI.ViewManagement;

namespace Muesli.Windows.WinUI.Pages;

public sealed partial class SearchPage : Page
{
    public SearchPageViewModel ViewModel { get; } = new(App.Library, App.Clipboard);

    public SearchPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ViewModel.HasQuery)
                or nameof(ViewModel.DictationCount)
                or nameof(ViewModel.MeetingCount)
                or null)
            {
                ApplyResponsiveLayout();
            }
        };
        Loaded += (_, _) => ApplyResponsiveLayout();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static InfoBarSeverity StatusSeverity(bool isError) =>
        isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    /// <summary>
    /// Outline for one row of the dictation run: sides and bottom always, top only on the first
    /// row, so consecutive rows share a single 1-px rule instead of drawing two.
    /// </summary>
    public static Thickness GroupBorders(bool isGroupStart) =>
        new(1, isGroupStart ? 1 : 0, 1, 1);

    /// <summary>
    /// Rounds only the outer corners of the run. The radius is the shared card radius, not a
    /// number typed here, so a search result card and a Timeline day card keep the same shape.
    /// </summary>
    public static CornerRadius GroupCorners(bool isGroupStart, bool isGroupEnd)
    {
        var radius = Application.Current.Resources["MuesliCardCornerRadiusValue"] is double value ? value : 12d;
        var top = isGroupStart ? radius : 0d;
        var bottom = isGroupEnd ? radius : 0d;
        return new CornerRadius(top, top, bottom, bottom);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        SetQuery(e.Parameter as string);
    }

    /// <summary>
    /// P2-05: the shell's sidebar field owns the query. This page renders whatever it is handed
    /// and no longer keeps a second copy of it in an input of its own.
    /// </summary>
    public void SetQuery(string? query) => ViewModel.Search(query?.Trim() ?? "");

    /// <summary>
    /// Clearing has to go back to the field that holds the text, otherwise the sidebar would keep
    /// showing a query this page had already dropped. The shell then routes back to the page the
    /// user was on before searching.
    /// </summary>
    private void Clear_Click(object sender, RoutedEventArgs e) =>
        (App.Window as MainWindow)?.ShellPage?.ClearSearch();

    private void KindFilter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is { Tag: string tag } && int.TryParse(tag, out var index))
        {
            ViewModel.FilterIndex = index;
        }
    }

    private async void CopyDictation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element &&
            (element.Tag as SearchResultItem ?? element.DataContext as SearchResultItem) is { } item)
        {
            await ViewModel.CopyDictationCommand.ExecuteAsync(item);
        }
    }

    private void ResultList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SearchResultItem item || IsInsideButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        // Both kinds carry their real record id: a meeting opens its detail page, a dictation
        // opens the Dictations page selected and scrolled to that same record. The rail has to
        // follow, or the shell keeps highlighting the search sentinel (i.e. nothing) while a real
        // destination is on screen.
        (App.Window as MainWindow)?.ShellPage?.SyncRailSelection(item.IsMeeting ? "meetings" : "dictations");
        Frame.Navigate(
            item.IsMeeting ? typeof(MeetingDetailPage) : typeof(DictationsPage),
            item.Id);
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void ResultList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        var container = args.ItemContainer;
        container.PointerEntered -= Row_Reveal;
        container.PointerExited -= Row_Hide;
        container.GotFocus -= Row_Reveal;
        container.LostFocus -= Row_Hide;

        if (args.InRecycleQueue || args.Item is not SearchResultItem item)
        {
            return;
        }

        AutomationProperties.SetName(container, item.Title);
        container.Margin = item.IsMeeting
            ? new Thickness(0, 0, 0, 10)
            : item.IsGroupEnd
                ? new Thickness(0, 0, 0, 16)
                : new Thickness(0);

        // Copy is revealed on hover or focus here for the same reason as on the Dictations page
        // (P2-08): a permanently drawn action glyph on every row outweighs the result text.
        container.PointerEntered += Row_Reveal;
        container.PointerExited += Row_Hide;
        container.GotFocus += Row_Reveal;
        container.LostFocus += Row_Hide;
        SetRowActionsVisible(container, false);
    }

    private void Row_Reveal(object sender, RoutedEventArgs e)
    {
        if (sender is ContentControl container)
        {
            SetRowActionsVisible(container, true);
        }
    }

    /// <summary>
    /// Never hides while the row still holds focus: moving focus from the container into its own
    /// copy button raises <c>LostFocus</c> on the container first.
    /// </summary>
    private void Row_Hide(object sender, RoutedEventArgs e)
    {
        if (sender is ContentControl container && !ContainsFocus(container))
        {
            SetRowActionsVisible(container, false);
        }
    }

    private bool ContainsFocus(DependencyObject container)
    {
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (ReferenceEquals(focused, container))
            {
                return true;
            }

            focused = VisualTreeHelper.GetParent(focused);
        }

        return false;
    }

    private static void SetRowActionsVisible(ContentControl container, bool visible)
    {
        if ((container.ContentTemplateRoot as FrameworkElement)?.FindName("RowActions") is not FrameworkElement actions)
        {
            return;
        }

        actions.Opacity = visible ? 1 : 0;
        actions.IsHitTestVisible = visible;
    }

    /// <summary>
    /// Page gutters follow the frozen shell tokens. At the compact 720-DIP window the kind
    /// filter keeps short labels so All / Dictations / Meetings stay on one row; counts remain
    /// in the page subtitle. Meeting timestamps stay on the metadata line.
    /// </summary>
    private void ApplyResponsiveLayout()
    {
        PageRoot.Padding = MuesliPageMetrics.ContentPadding(ActualWidth);
        var compact = MuesliPageMetrics.IsCompact(ActualWidth) || MuesliPageMetrics.IsMedium(ActualWidth);
        var showCounts = ViewModel.HasQuery && !compact;
        DictationsTab.Text = showCounts
            ? "Dictations (" + ViewModel.DictationCount + ")"
            : "Dictations";
        MeetingsTab.Text = showCounts
            ? "Meetings (" + ViewModel.MeetingCount + ")"
            : "Meetings";
    }
}

/// <summary>
/// Page-local highlight control. WinUI's TextBlock is sealed, so this hosts one and rebuilds
/// inlines when the query changes. Match spans use the product accent.
/// </summary>
public sealed class SearchHighlightTextBlock : UserControl
{
    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(
        nameof(SourceText),
        typeof(string),
        typeof(SearchHighlightTextBlock),
        new PropertyMetadata("", OnContentChanged));

    public static readonly DependencyProperty QueryProperty = DependencyProperty.Register(
        nameof(Query),
        typeof(string),
        typeof(SearchHighlightTextBlock),
        new PropertyMetadata("", OnContentChanged));

    public static readonly DependencyProperty MaxLinesProperty = DependencyProperty.Register(
        nameof(MaxLines),
        typeof(int),
        typeof(SearchHighlightTextBlock),
        new PropertyMetadata(3, OnLayoutChanged));

    public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(
        nameof(TextWrapping),
        typeof(TextWrapping),
        typeof(SearchHighlightTextBlock),
        new PropertyMetadata(TextWrapping.Wrap, OnLayoutChanged));

    private readonly TextBlock _block = new()
    {
        TextTrimming = TextTrimming.CharacterEllipsis,
        IsTextSelectionEnabled = false
    };

    public SearchHighlightTextBlock()
    {
        Content = _block;
        IsTabStop = false;
        Loaded += (_, _) =>
        {
            ApplyTypography();
            Rebuild();
        };
        ActualThemeChanged += (_, _) => Rebuild();
        RegisterPropertyChangedCallback(FontSizeProperty, (_, _) => ApplyTypography());
        RegisterPropertyChangedCallback(FontWeightProperty, (_, _) => ApplyTypography());
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) =>
        {
            ApplyTypography();
            Rebuild();
        });
    }

    public string SourceText
    {
        get => (string)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public string Query
    {
        get => (string)GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    public int MaxLines
    {
        get => (int)GetValue(MaxLinesProperty);
        set => SetValue(MaxLinesProperty, value);
    }

    public TextWrapping TextWrapping
    {
        get => (TextWrapping)GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SearchHighlightTextBlock)d).Rebuild();

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SearchHighlightTextBlock)d).ApplyTypography();

    private void ApplyTypography()
    {
        _block.FontSize = FontSize;
        _block.FontWeight = FontWeight;
        _block.Foreground = Foreground;
        _block.MaxLines = MaxLines;
        _block.TextWrapping = TextWrapping;
    }

    private void Rebuild()
    {
        _block.Inlines.Clear();
        var text = SourceText ?? "";
        var query = (Query ?? "").Trim();
        var unmatched = Foreground ?? TryThemeBrush("MuesliTextSecondaryBrush");
        var accent = TryThemeBrush("MuesliAccentBrush") ?? unmatched;

        if (text.Length == 0)
        {
            return;
        }

        if (query.Length == 0)
        {
            _block.Inlines.Add(new Run { Text = Truncate(text, 160), Foreground = unmatched });
            return;
        }

        var matchIndex = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (matchIndex < 0)
        {
            _block.Inlines.Add(new Run { Text = Truncate(text, 120), Foreground = unmatched });
            return;
        }

        const int context = 60;
        var matchEnd = matchIndex + query.Length;
        var start = Math.Max(0, matchIndex - context);
        var end = Math.Min(text.Length, matchEnd + context);
        var prefix = start > 0 ? "..." : "";
        var suffix = end < text.Length ? "..." : "";

        _block.Inlines.Add(new Run
        {
            Text = prefix + text[start..matchIndex],
            Foreground = unmatched
        });
        _block.Inlines.Add(new Run
        {
            Text = text[matchIndex..matchEnd],
            Foreground = accent,
            FontWeight = FontWeights.SemiBold
        });
        _block.Inlines.Add(new Run
        {
            Text = text[matchEnd..end] + suffix,
            Foreground = unmatched
        });
    }

    private Brush? TryThemeBrush(string key)
    {
        var themeKey = new AccessibilitySettings().HighContrast
            ? "HighContrast"
            : ActualTheme == ElementTheme.Light ? "Light" : "Dark";

        foreach (var dictionary in Application.Current.Resources.MergedDictionaries.Reverse())
        {
            if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out var themedResources) &&
                themedResources is ResourceDictionary themeDictionary &&
                themeDictionary.TryGetValue(key, out var themedValue) &&
                themedValue is Brush themedBrush)
            {
                return themedBrush;
            }
        }

        return Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : null;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
}
