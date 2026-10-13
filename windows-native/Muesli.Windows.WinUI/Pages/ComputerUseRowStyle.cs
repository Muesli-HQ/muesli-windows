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
        // ponytail: theme read when the icon is first drawn; rows bound earlier keep theirs until rebuilt.
        var dark = App.ResolveTheme(App.Settings.Load().Theme) switch
        {
            ElementTheme.Light => false,
            ElementTheme.Dark => true,
            _ => Application.Current.RequestedTheme == ApplicationTheme.Dark,
        };
        var key = $"{(dark ? "dark" : "light")}|{path}";
        if (AppIcons.TryGetValue(key, out var cached)) return cached;
        ImageSource? source = null;
        try
        {
            using var bitmap = PackageLogo(path, dark) ?? IconBitmap(path);
            if (bitmap is not null)
            {
                // Kept open: the BitmapImage decodes from it lazily, and it is cached with the image.
                var png = new MemoryStream();
                bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                png.Position = 0;
                var image = new BitmapImage();
                image.SetSource(png.AsRandomAccessStream());
                source = image;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Xml.XmlException or System.Runtime.InteropServices.COMException) { }
        return AppIcons[key] = source;
    }

    private static System.Drawing.Bitmap? IconBitmap(string path)
    {
        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
        return icon?.ToBitmap();
    }

    /// <summary>A packaged (MSIX) app's exe icon is often a bare glyph; its real icon is the manifest's
    /// Square44x44Logo. Like the Windows 11 taskbar, use the theme's unplated variant, falling back to
    /// the plated logo on the manifest BackgroundColor.</summary>
    private static System.Drawing.Bitmap? PackageLogo(string exePath, bool dark)
    {
        var root = Path.GetDirectoryName(exePath);
        while (root is not null && !File.Exists(Path.Combine(root, "AppxManifest.xml"))) root = Path.GetDirectoryName(root);
        if (root is null) return null;
        var relative = Path.GetRelativePath(root, exePath);
        var visuals = System.Xml.Linq.XDocument.Load(Path.Combine(root, "AppxManifest.xml")).Descendants()
            .Where(e => e.Name.LocalName == "Application"
                && string.Equals(e.Attribute("Executable")?.Value.Replace('/', '\\'), relative, StringComparison.OrdinalIgnoreCase))
            .SelectMany(e => e.Elements())
            .FirstOrDefault(e => e.Name.LocalName == "VisualElements");
        if (visuals?.Attribute("Square44x44Logo")?.Value is not { Length: > 0 } logo) return null;

        // MRT stores qualified variants beside the unqualified name; prefer the sharpest plated one.
        var unqualified = Path.Combine(root, logo.Replace('/', '\\'));
        var stem = Path.Combine(Path.GetDirectoryName(unqualified)!, Path.GetFileNameWithoutExtension(unqualified));
        var ext = Path.GetExtension(unqualified);
        var unplated = (dark ? new[] { "unplated" } : ["lightunplated", "unplated"])
            .Select(form => $"{stem}.targetsize-48_altform-{form}{ext}")
            .FirstOrDefault(File.Exists);
        var file = unplated ?? new[] { $"{stem}.targetsize-48{ext}", $"{stem}.scale-200{ext}", unqualified, $"{stem}.scale-100{ext}" }
            .FirstOrDefault(File.Exists);
        if (file is null) return null;

        const int size = 48;
        var tile = new System.Drawing.Bitmap(size, size);
        using var g = System.Drawing.Graphics.FromImage(tile);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        var plate = System.Drawing.ColorTranslator.FromHtml(visuals.Attribute("BackgroundColor")?.Value ?? "transparent");
        if (unplated is null && plate.A > 0)
        {
            using var brush = new System.Drawing.SolidBrush(plate);
            g.FillRoundedRectangle(brush, new System.Drawing.Rectangle(0, 0, size, size), new System.Drawing.Size(12, 12));
        }
        using var logoImage = System.Drawing.Image.FromFile(file);
        g.DrawImage(logoImage, 0, 0, size, size);
        return tile;
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
