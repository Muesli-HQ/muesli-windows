using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

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
        using var bitmap = bounds is { Width: > 8, Height: > 8 } rect && TryWindowHandle(window) is { } handle && handle != IntPtr.Zero
            ? CaptureWindow(handle, rect.Size)
            : window is not null
                ? throw new InvalidOperationException($"The target window for '{slug}' has no valid HWND bounds; refusing desktop capture.")
            : CapturePrimary();
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    private static IntPtr? TryWindowHandle(AutomationElement? window)
    {
        if (window is null)
            return null;
        try
        {
            var handle = window.Current.NativeWindowHandle;
            return handle == 0 ? null : new IntPtr(handle);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
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

    private static Bitmap CaptureWindow(IntPtr handle, Size expectedSize)
    {
        var bitmap = new Bitmap(expectedSize.Width, expectedSize.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var deviceContext = graphics.GetHdc();
        try
        {
            if (!PrintWindow(handle, deviceContext, PrintWindowFullContent))
            {
                bitmap.Dispose();
                throw new InvalidOperationException($"PrintWindow could not render HWND 0x{handle.ToInt64():X}; desktop capture is disabled for app evidence.");
            }
        }
        finally
        {
            graphics.ReleaseHdc(deviceContext);
        }

        if (bitmap.Width != expectedSize.Width || bitmap.Height != expectedSize.Height)
        {
            bitmap.Dispose();
            throw new InvalidOperationException("The HWND-bounded screenshot dimensions did not match the target window bounds.");
        }

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

    private const uint PrintWindowFullContent = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
}
