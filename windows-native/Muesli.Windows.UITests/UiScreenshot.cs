using System.Drawing;
using System.Drawing.Imaging;

namespace Muesli.Windows.UITests;

internal static class UiScreenshot
{
    public static string ArtifactDirectory =>
        Path.Combine(TestPaths.RepositoryRoot, "artifacts", "ui-automation");

    public static string Capture(AutomationElement? window, string slug)
    {
        Directory.CreateDirectory(ArtifactDirectory);
        var safe = Sanitize(slug);
        var path = Path.Combine(ArtifactDirectory, $"{safe}-{DateTime.UtcNow:yyyyMMddHHmmssfff}.png");
        var bounds = TryWindowBounds(window);
        using var bitmap = bounds is { Width: > 8, Height: > 8 } rect
            ? CaptureRectangle(rect)
            : CapturePrimary();
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    private static Rectangle? TryWindowBounds(AutomationElement? window)
    {
        if (window is null)
            return null;
        try
        {
            var rect = window.Current.BoundingRectangle;
            if (rect.IsEmpty)
                return null;
            return Rectangle.FromLTRB(
                Math.Max(0, (int)Math.Floor(rect.Left)),
                Math.Max(0, (int)Math.Floor(rect.Top)),
                (int)Math.Ceiling(rect.Right),
                (int)Math.Ceiling(rect.Bottom));
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    private static Bitmap CaptureRectangle(Rectangle bounds)
    {
        var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
        return bitmap;
    }

    private static Bitmap CapturePrimary()
    {
        var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
                     ?? new Rectangle(0, 0, 1280, 720);
        return CaptureRectangle(bounds);
    }

    private static string Sanitize(string slug)
    {
        var chars = slug.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-').ToArray();
        var value = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(value) ? "failure" : value;
    }
}
