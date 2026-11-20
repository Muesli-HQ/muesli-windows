using System.Drawing.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Muesli.Windows.ComputerUse;
using Windows.Graphics.Capture;

namespace Muesli.Windows.WinUI.Controls;

/// <summary>A window or display the user can record; the capture item is created only on confirm.</summary>
public sealed record MeetingScreenSource(string Id, string Title, string Subtitle, bool IsDisplay, long WindowId, ulong DisplayId, CuaRect Frame)
{
    public GraphicsCaptureItem? CreateItem() => IsDisplay
        ? GraphicsCaptureItem.TryCreateFromDisplayId(new global::Windows.Graphics.DisplayId(DisplayId))
        : GraphicsCaptureItem.TryCreateFromWindowId(new global::Windows.UI.WindowId((ulong)WindowId));
}

/// <summary>
/// Port of macOS MeetingScreenSourcePicker (2f10b23d): a preview grid of windows and displays, an
/// optional local announcement, and an explicit "Record Screen" confirm. Previews are transient
/// in-memory images; system utility windows and Muesli's own windows are excluded.
/// </summary>
public static class MeetingScreenSourcePicker
{
    public static IReadOnlyList<MeetingScreenSource> Sources()
    {
        var windows = Win32Desktop.ListWindows()
            .Where(window => window.Frame.Width > 100 && window.Frame.Height > 100 && !Win32Desktop.IsMinimized(window.Id))
            .Select(window => new MeetingScreenSource($"window-{window.Id}", window.AppName,
                window.Title.Length > 0 ? window.Title : "Untitled window", false, window.Id, 0, window.Frame))
            .OrderBy(source => source.Title, StringComparer.CurrentCultureIgnoreCase);
        var displays = Microsoft.UI.Windowing.DisplayArea.FindAll().Select((area, index) => new MeetingScreenSource(
            $"display-{area.DisplayId.Value}", $"Display {index + 1}", $"{area.OuterBounds.Width} × {area.OuterBounds.Height}", true, 0,
            area.DisplayId.Value, new CuaRect(area.OuterBounds.X, area.OuterBounds.Y, area.OuterBounds.Width, area.OuterBounds.Height)));
        return [.. windows, .. displays];
    }

    /// <summary>The chosen source and whether to play the announcement, or null when cancelled.</summary>
    public static async Task<(MeetingScreenSource Source, bool Announce)?> PickAsync(XamlRoot root, IReadOnlyList<MeetingScreenSource> sources)
    {
        MeetingScreenSource? selected = null;
        var showDisplays = false;
        var grid = new GridView { SelectionMode = ListViewSelectionMode.Single, Height = 330, Padding = new Thickness(0) };
        var empty = new TextBlock { Text = "No available windows. Choose an entire display instead.", Visibility = Visibility.Collapsed, Opacity = 0.7 };
        var kind = new RadioButtons { MaxColumns = 2, Items = { "Windows", "Entire displays" }, SelectedIndex = 0 };
        var announce = new CheckBox { Content = "Play ‘This meeting is being recorded’" };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Choose what to record",
            PrimaryButtonText = "Record Screen",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            Content = new StackPanel
            {
                Spacing = 12, Width = 640,
                Children =
                {
                    new TextBlock { Text = "Screen and meeting audio are saved on this PC.", Opacity = 0.7 },
                    kind, grid, empty, announce,
                    new TextBlock { Text = "Let participants know before recording. Playback on this PC may not reach remote participants.",
                        FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap }
                }
            }
        };

        void Render()
        {
            grid.Items.Clear();
            selected = null;
            dialog.IsPrimaryButtonEnabled = false;
            var visible = sources.Where(source => source.IsDisplay == showDisplays).ToList();
            empty.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var source in visible) grid.Items.Add(Tile(source));
        }
        kind.SelectionChanged += (_, _) => { showDisplays = kind.SelectedIndex == 1; Render(); };
        grid.SelectionChanged += (_, _) =>
        {
            selected = (grid.SelectedItem as FrameworkElement)?.Tag as MeetingScreenSource;
            dialog.IsPrimaryButtonEnabled = selected is not null;
        };
        Render();
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary && selected is not null ? (selected, announce.IsChecked == true) : null;
    }

    private static FrameworkElement Tile(MeetingScreenSource source)
    {
        var image = new Image { Stretch = Stretch.Uniform };
        var preview = new Grid
        {
            Height = 112, Width = 196, CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(64, 0, 0, 0)),
            Children = { image }
        };
        _ = LoadPreviewAsync(source, image, preview);
        var tile = new StackPanel
        {
            Spacing = 4, Padding = new Thickness(6), Tag = source,
            Children =
            {
                preview,
                new TextBlock { Text = source.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 196 },
                new TextBlock { Text = source.Subtitle, FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 196 }
            }
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tile, $"{source.Title}, {source.Subtitle}");
        return tile;
    }

    private static async Task LoadPreviewAsync(MeetingScreenSource source, Image image, Grid host)
    {
        try
        {
            var bytes = await Task.Run(() =>
            {
                using var bitmap = source.IsDisplay ? Win32Desktop.CaptureScreen(source.Frame) : Win32Desktop.CaptureWindow(source.WindowId, source.Frame);
                if (bitmap is null) return null;
                var scale = Math.Min(1.0, 440.0 / bitmap.Width);
                using var small = new System.Drawing.Bitmap(bitmap, Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale)));
                using var stream = new MemoryStream();
                small.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            });
            if (bytes is null) { host.Children.Add(Unavailable()); return; }
            var bitmapImage = new BitmapImage();
            using var memory = new MemoryStream(bytes);
            await bitmapImage.SetSourceAsync(memory.AsRandomAccessStream());
            image.Source = bitmapImage;
        }
        catch (Exception) { host.Children.Add(Unavailable()); }
    }

    private static TextBlock Unavailable() => new()
    {
        Text = "Preview unavailable", FontSize = 12, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
}
