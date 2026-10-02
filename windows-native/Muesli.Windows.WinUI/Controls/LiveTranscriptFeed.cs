using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Collections.ObjectModel;
using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.Controls;

/// <summary>The dashboard and floating panel share the macOS speaker-bubble presentation.</summary>
public sealed class LiveTranscriptFeed : UserControl
{
    private ScrollViewer? _scroll;
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 8, 12, 8), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly StackPanel _feed = new() { Spacing = 6 };
    private readonly ObservableCollection<UIElement> _committed = [];
    private readonly TextBlock _waiting = new() { Text = "Waiting for speech…", FontSize = 13 };
    private readonly Grid _others;
    private readonly Grid _you;
    private TranscriptChatMessage[] _segments = [];
    private string _partialYou = "", _partialOthers = "", _text = "";
    public string CopyText { get; private set; } = "";
    public Action? OpenNotes { get; set; }

    public LiveTranscriptFeed()
    {
        Content = _list;
        _list.ItemsSource = _committed;
        _list.Header = _waiting;
        _list.Footer = _feed;
        _list.ContainerContentChanging += (_, args) =>
        {
            if (args.InRecycleQueue) return;
            if (args.Item is FrameworkElement { Tag: TranscriptChatMessage message })
            {
                AutomationProperties.SetName(args.ItemContainer, $"{message.Speaker}: {message.Text}");
                args.ItemContainer.Padding = new Thickness(0);
                args.ItemContainer.Margin = new Thickness(0, 0, 0, 6);
            }
        };
        _list.Loaded += (_, _) => _scroll = FindScroll(_list);
        Theme(_waiting, "Foreground", "MuesliTextTertiaryBrush");
        _others = Bubble("", false, true);
        _you = Bubble("", true, true);
        _feed.Children.Add(_others);
        _feed.Children.Add(_you);
        _text = "\u0001";
        Update(null, "");
    }

    public void Update(LiveTranscriptSnapshot? snapshot, string fallback, string prefix = "")
    {
        var transcript = string.Join("\n", new[] { prefix.Trim(), (snapshot?.CommittedText ?? fallback).Trim() }.Where(value => value.Length > 0));
        var you = snapshot?.PartialMicrophone.Trim() ?? "";
        var others = snapshot?.PartialSystem.Trim() ?? "";
        if (transcript == _text && you == _partialYou && others == _partialOthers) return;
        var segments = TranscriptChatMessage.Parse(transcript);
        var changed = !_segments.SequenceEqual(segments);
        var scroll = changed || (_partialYou.Length == 0 && you.Length > 0) || (_partialOthers.Length == 0 && others.Length > 0)
            || !string.Equals(_text, fallback, StringComparison.Ordinal) && snapshot is null;
        if (changed)
        {
            var append = segments.Length >= _segments.Length && segments.Take(_segments.Length).SequenceEqual(_segments);
            if (!append) _committed.Clear();
            // ponytail: one bubble per utterance; use a compiled DataTemplate if long histories stress memory.
            foreach (var segment in segments.Skip(append ? _segments.Length : 0))
            {
                var bubble = Bubble(segment.Text, segment.IsYou, false, segment.Speaker, segment.Timestamp);
                bubble.Tag = segment;
                _committed.Add(bubble);
            }
            _segments = segments;
        }
        SetPartial(_you, you);
        SetPartial(_others, others);
        _partialYou = you; _partialOthers = others; _text = transcript;
        _waiting.Visibility = segments.Length == 0 && you.Length == 0 && others.Length == 0 && transcript.Length == 0
            ? Visibility.Visible : Visibility.Collapsed;
        CopyText = LiveTranscriptCopyContent.Text(transcript, you, others);
        if (scroll) DispatcherQueue.TryEnqueue(() => { UpdateLayout(); _scroll ??= FindScroll(_list); _scroll?.ChangeView(null, _scroll.ScrollableHeight, null, false); });
    }

    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScroll(child) is { } found) return found;
        }
        return null;
    }

    private static void SetPartial(Grid row, string text)
    {
        ((TextBlock)((StackPanel)((Border)row.Children[0]).Child).Children[1]).Text = text;
        row.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private Grid Bubble(string text, bool you, bool partial, string? speaker = null, string? timestamp = null)
    {
        var row = new Grid { ColumnSpacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = you ? new GridLength(1, GridUnitType.Star) : GridLength.Auto, MinWidth = you ? 40 : 0 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = you ? GridLength.Auto : new GridLength(1, GridUnitType.Star), MinWidth = you ? 0 : 40 });
        var label = new TextBlock { Text = string.Join(" · ", new[] { speaker ?? (partial ? you ? "You" : "Others" : null), timestamp }.Where(value => value is not null)), FontSize = 10 };
        Theme(label, "Foreground", "MuesliTextTertiaryBrush");
        var body = new TextBlock { Text = text, FontSize = 13, FontStyle = partial ? global::Windows.UI.Text.FontStyle.Italic : global::Windows.UI.Text.FontStyle.Normal,
            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        Theme(body, "Foreground", partial ? "MuesliTextSecondaryBrush" : "MuesliTextPrimaryBrush");
        var contents = new StackPanel { Spacing = 2 };
        contents.Children.Add(label); contents.Children.Add(body);
        label.Visibility = label.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var bubble = new Border { Child = contents, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6),
            BorderThickness = partial ? new Thickness(0) : new Thickness(1), HorizontalAlignment = you ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        Theme(bubble, "Background", you ? "MuesliAccentMutedBrush" : "MuesliSurfaceBrush", "BorderBrush", you ? "MuesliAccentMutedBrush" : "MuesliBorderBrush");
        Grid.SetColumn(bubble, you ? 2 : 0); row.Children.Add(bubble);
        row.SizeChanged += (_, args) => bubble.MaxWidth = Math.Max(1, args.NewSize.Width - 76);
        if (partial)
        {
            var outline = new Rectangle { RadiusX = 6, RadiusY = 6, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false,
                HorizontalAlignment = you ? HorizontalAlignment.Right : HorizontalAlignment.Left };
            Theme(outline, "Stroke", you ? "MuesliAccentMutedBrush" : "MuesliBorderBrush");
            bubble.SizeChanged += (_, args) => { outline.Width = args.NewSize.Width; outline.Height = args.NewSize.Height; };
            Grid.SetColumn(outline, you ? 2 : 0); row.Children.Add(outline);
        }
        var copy = new Button { Content = new FontIcon { Glyph = "\uE8C8", FontSize = 10 }, Width = 24, Height = 24, MinWidth = 24, MinHeight = 24,
            Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0,
            Style = (Style)Application.Current.Resources["MuesliGhostButtonStyle"] };
        AutomationProperties.SetAutomationId(copy, "LiveTranscriptMessageCopy");
        AutomationProperties.SetName(copy, you ? "Copy your message" : "Copy others' message");
        ToolTipService.SetToolTip(copy, "Copy message");
        Grid.SetColumn(copy, 1); row.Children.Add(copy);
        row.PointerEntered += (_, _) => copy.Opacity = 1;
        row.PointerExited += (_, _) => { if (copy.FocusState == FocusState.Unfocused) copy.Opacity = 0; };
        copy.GotFocus += (_, _) => copy.Opacity = 1;
        copy.LostFocus += (_, _) => copy.Opacity = 0;
        copy.Click += async (_, _) => await CopyAsync(copy, body.Text);
        bubble.Tapped += (_, _) => OpenNotes?.Invoke();
        return row;
    }

    private static void Theme(FrameworkElement element, string property, string key, string? second = null, string? secondKey = null)
    {
        // XAML ThemeResource setters keep Light, Dark and High Contrast changes live.
        var extra = second is null ? "" : $"<Setter Property=\"{second}\" Value=\"{{ThemeResource {secondKey}}}\"/>";
        element.Style = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            $"<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"{element.GetType().Name}\"><Setter Property=\"{property}\" Value=\"{{ThemeResource {key}}}\"/>{extra}</Style>");
    }

    internal static async Task CopyAsync(Button button, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            await App.Clipboard.SetTextAsync(text.Trim());
            var previous = button.Content;
            button.Content = new FontIcon { Glyph = "\uE73E", FontSize = 12 };
            await Task.Delay(1200);
            button.Content = previous;
        }
        catch (Exception exception)
        {
            new Flyout { Content = new TextBlock { Text = $"Could not copy: {Services.WinUiClipboardService.DescribeFailure(exception)}",
                MaxWidth = 280, TextWrapping = TextWrapping.Wrap } }.ShowAt(button);
        }
    }
}
