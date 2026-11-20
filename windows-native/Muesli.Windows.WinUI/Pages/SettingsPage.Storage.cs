using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.Pages;

/// <summary>macOS LocalStorageUsageView (MAC-11): the scan runs off the UI thread and is cancelled with the page.</summary>
public sealed partial class SettingsPage
{
    private static readonly (LocalStorageUsage.Category Category, string Title, string Glyph, global::Windows.UI.Color Color)[] StorageCategories =
    [
        (LocalStorageUsage.Category.Models, "Downloaded models", "", Colors.DodgerBlue),
        (LocalStorageUsage.Category.MeetingAudio, "Meeting audio", "", Colors.Teal),
        (LocalStorageUsage.Category.MeetingVideo, "Meeting video", "", Colors.MediumPurple),
        (LocalStorageUsage.Category.Database, "Notes, transcripts & app database", "", Colors.Orange),
        (LocalStorageUsage.Category.WaveformCache, "Audio waveform cache", "", Colors.HotPink),
        (LocalStorageUsage.Category.Other, "Other app data", "", Colors.Gray),
    ];

    private CancellationTokenSource? _storageScan;
    private bool _showModels;
    private LocalStorageUsage? _storage;

    private void InitializeStorage()
    {
        Loaded += (_, _) => _ = ScanStorageAsync();
        Unloaded += (_, _) => _storageScan?.Cancel();
    }

    private void StorageRefresh_Click(object sender, RoutedEventArgs e) => _ = ScanStorageAsync();

    private async Task ScanStorageAsync()
    {
        _storageScan?.Cancel();
        var scan = _storageScan = new CancellationTokenSource();
        StorageProgress.IsActive = true;
        StorageRefresh.IsEnabled = false;
        try
        {
            var root = App.Library.Profile.RootDirectory;
            _storage = await Task.Run(() => LocalStorageUsage.Scan(root, LocalStorageUsage.DefaultModelRoots(), scan.Token), scan.Token);
            RenderStorage();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_storageScan, scan))
            {
                StorageProgress.IsActive = false;
                StorageRefresh.IsEnabled = true;
            }
        }
    }

    private void RenderStorage()
    {
        if (_storage is not { } usage) return;
        StorageTotal.Text = Bytes(usage.TotalBytes);
        StorageRows.Children.Clear();

        var bar = new Grid { Height = 8, CornerRadius = new CornerRadius(4), Background = (Brush)Application.Current.Resources["MuesliBorderSoftBrush"] };
        AutomationProperties.SetAccessibilityView(bar, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        foreach (var (category, _, _, color) in StorageCategories)
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, usage[category]), GridUnitType.Star) });
            var segment = new Border { Background = new SolidColorBrush(color) };
            Grid.SetColumn(segment, bar.ColumnDefinitions.Count - 1);
            bar.Children.Add(segment);
        }
        if (usage.TotalBytes > 0) StorageRows.Children.Add(bar);

        foreach (var (category, title, glyph, color) in StorageCategories)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = new SolidColorBrush(color) });
            FrameworkElement label = new TextBlock { Text = title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            if (category == LocalStorageUsage.Category.Models && usage.Models.Count > 0)
            {
                var toggle = new HyperlinkButton { Content = $"{title} {(_showModels ? "▾" : "▸")}", Padding = new Thickness(0) };
                AutomationProperties.SetName(toggle, title);
                toggle.Click += (_, _) => { _showModels = !_showModels; RenderStorage(); };
                label = toggle;
            }
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            var size = Secondary(Bytes(usage[category]));
            Grid.SetColumn(size, 2);
            row.Children.Add(size);
            AutomationProperties.SetName(row, $"{title}: {Bytes(usage[category])}");
            StorageRows.Children.Add(row);
            if (category == LocalStorageUsage.Category.Models && _showModels)
                foreach (var model in usage.Models)
                {
                    var modelRow = new Grid { Margin = new Thickness(30, 0, 0, 0) };
                    modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    modelRow.Children.Add(Secondary(model.Name, TextTrimming.CharacterEllipsis));
                    var modelSize = Secondary(Bytes(model.Bytes));
                    Grid.SetColumn(modelSize, 1);
                    modelRow.Children.Add(modelSize);
                    StorageRows.Children.Add(modelRow);
                }
        }

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var shared = Secondary("Includes shared model caches");
        ToolTipService.SetToolTip(shared, "File sizes in Muesli's app data and known model folders. Model caches may also be used by other Muesli builds or apps. The database includes dictation history. NTFS compression can affect physical disk usage. App binaries and system-managed caches are excluded.");
        footer.Children.Add(shared);
        if (usage.FreeBytes is { } free)
        {
            var available = Secondary($"{Bytes(free)} available");
            Grid.SetColumn(available, 1);
            footer.Children.Add(available);
        }
        StorageRows.Children.Add(footer);
        if (usage.UnavailableLocations > 0)
            StorageRows.Children.Add(Secondary("Some files could not be measured. Totals may be incomplete."));
    }

    private static TextBlock Secondary(string text, TextTrimming trimming = TextTrimming.None) => new()
    {
        Text = text,
        FontSize = 12,
        TextTrimming = trimming,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = (Brush)Application.Current.Resources["MuesliTextSecondaryBrush"],
    };

    /// <summary>Like macOS ByteCountFormatter's file style: decimal units.</summary>
    internal static string Bytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; }
        return unit == 0 ? $"{bytes} bytes" : $"{value:0.#} {units[unit]}";
    }
}
