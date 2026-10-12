using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.WinUI.Pages;

/// <summary>x:Bind helpers for the Computer Use parts of a dictation row (macOS DictationRowView),
/// shared by Timeline and Dictations.</summary>
public static class ComputerUseRowStyle
{
    public static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>macOS <c>DictationRowView.statusColor</c>.</summary>
    public static Brush StatusBrush(string status) => (Brush)Application.Current.Resources[status switch
    {
        "done" => "MuesliSuccessBrush",
        "failed" => "MuesliErrorBrush",
        "timed_out" or "confirm" => "MuesliWarningBrush",
        _ => "MuesliTextTertiaryBrush",
    }];

    /// <summary>macOS keeps a row's actions visible when it has expandable details; others reveal on hover.</summary>
    public static double ActionsOpacity(bool hasRun) => hasRun ? 1 : 0;

    /// <summary>chevron.up / chevron.down.</summary>
    public static string Chevron(bool expanded) => expanded ? "" : "";

    public static string ChevronName(bool expanded) => expanded ? "Hide steps" : "Show steps";

    private static readonly Dictionary<string, ImageSource?> AppIcons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>macOS TargetApplicationIconView: the app's own icon, cached per executable.</summary>
    public static ImageSource? AppIcon(DictationTargetApp? app)
    {
        if (app?.ExePath is not { Length: > 0 } path) return null;
        if (AppIcons.TryGetValue(path, out var cached)) return cached;
        ImageSource? source = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is not null)
            {
                using var bitmap = icon.ToBitmap();
                // Kept open: the BitmapImage decodes from it lazily, and it is cached with the image.
                var png = new MemoryStream();
                bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                png.Position = 0;
                var image = new BitmapImage();
                image.SetSource(png.AsRandomAccessStream());
                source = image;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException) { }
        return AppIcons[path] = source;
    }

    /// <summary>macOS shows app.dashed when the app is known but its icon is not.</summary>
    public static Visibility AppIconFallback(DictationTargetApp? app) => Show(app is not null && AppIcon(app) is null);

    public static string DictatedInto(DictationTargetApp? app) => app is null ? "" : $"Dictated into {app.Name}";

    /// <summary>True when a tap landed on a button or on selectable step text, which own the tap
    /// instead of the row.</summary>
    public static bool IsOwnTap(object? source, object row)
    {
        for (var node = source as DependencyObject; node is not null && !ReferenceEquals(node, row); node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or TextBlock { IsTextSelectionEnabled: true }) return true;
        }
        return false;
    }
}
