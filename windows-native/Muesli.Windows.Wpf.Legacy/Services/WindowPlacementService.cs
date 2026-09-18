using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Muesli.Windows.Services;

/// <summary>Converts native monitor pixels through a window-relative WPF transform; it never scales virtual-desktop origins directly.</summary>
public static class WindowPlacementService
{
    public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom);
    public readonly record struct PixelPoint(int X, int Y);
    public readonly record struct ScreenCoordinateAnchor(PixelPoint ScreenPixels, System.Windows.Point DesktopDips);

    /// <summary>Pure conversion for an area on the same monitor as <paramref name="anchor"/>. Coordinates are first made window-relative.</summary>
    public static Rect PixelRectToDipRect(PixelRect pixels, Matrix fromDevice, ScreenCoordinateAnchor anchor)
    {
        var firstOffset = fromDevice.Transform(new System.Windows.Point(pixels.Left - anchor.ScreenPixels.X, pixels.Top - anchor.ScreenPixels.Y));
        var secondOffset = fromDevice.Transform(new System.Windows.Point(pixels.Right - anchor.ScreenPixels.X, pixels.Bottom - anchor.ScreenPixels.Y));
        var first = new System.Windows.Point(anchor.DesktopDips.X + firstOffset.X, anchor.DesktopDips.Y + firstOffset.Y);
        var second = new System.Windows.Point(anchor.DesktopDips.X + secondOffset.X, anchor.DesktopDips.Y + secondOffset.Y);
        return new Rect(Math.Min(first.X, second.X), Math.Min(first.Y, second.Y), Math.Abs(second.X - first.X), Math.Abs(second.Y - first.Y));
    }

    public static Rect ClampToWorkArea(Rect bounds, Rect workArea)
    {
        var width = Math.Min(Math.Max(1, bounds.Width), Math.Max(1, workArea.Width));
        var height = Math.Min(Math.Max(1, bounds.Height), Math.Max(1, workArea.Height));
        var left = Math.Clamp(bounds.Left, workArea.Left, workArea.Right - width);
        var top = Math.Clamp(bounds.Top, workArea.Top, workArea.Bottom - height);
        return new Rect(left, top, width, height);
    }

    public static Rect GetWorkAreaForWindow(Window window) => GetWorkArea(window, GetWindowHandle(window), null);

    public static Rect GetWorkAreaForCursor(Window window)
    {
        var handle = GetWindowHandle(window);
        if (!GetCursorPos(out var cursor)) return GetWorkAreaForWindow(window);
        return GetWorkArea(window, handle, MonitorFromPoint(cursor, MonitorDefaultToNearest));
    }

    public static Rect GetWorkAreaForBounds(Window window, Rect bounds)
    {
        var handle = GetWindowHandle(window);
        var toDevice = GetTransformToDevice(window, handle);
        var topLeft = toDevice.Transform(new System.Windows.Point(bounds.Left, bounds.Top));
        var bottomRight = toDevice.Transform(new System.Windows.Point(bounds.Right, bounds.Bottom));
        var nativeBounds = new NativeRect
        {
            Left = (int)Math.Floor(Math.Min(topLeft.X, bottomRight.X)),
            Top = (int)Math.Floor(Math.Min(topLeft.Y, bottomRight.Y)),
            Right = (int)Math.Ceiling(Math.Max(topLeft.X, bottomRight.X)),
            Bottom = (int)Math.Ceiling(Math.Max(topLeft.Y, bottomRight.Y))
        };
        return GetWorkArea(window, handle, MonitorFromRect(ref nativeBounds, MonitorDefaultToNearest));
    }

    public static Rect ClampToVisibleWorkArea(Window window, Rect bounds) => ClampToWorkArea(bounds, GetWorkAreaForBounds(window, bounds));

    public static void FitToWorkArea(Window window, double preferredWidth = 1240, double preferredHeight = 820)
    {
        var work = GetWorkAreaForWindow(window);
        var availableWidth = Math.Max(1, work.Width - 24);
        var availableHeight = Math.Max(1, work.Height - 24);
        // A small high-DPI work area may be physically smaller than the normal XAML minimum.
        // Keep the window finite and visible; the existing page ScrollViewers retain access.
        window.MinWidth = Math.Min(760, availableWidth);
        window.MinHeight = Math.Min(560, availableHeight);
        window.Width = Math.Clamp(preferredWidth, window.MinWidth, Math.Max(window.MinWidth, availableWidth));
        window.Height = Math.Clamp(preferredHeight, window.MinHeight, Math.Max(window.MinHeight, availableHeight));
        window.Left = work.Left + Math.Max(12, (work.Width - window.Width) / 2);
        window.Top = work.Top + Math.Max(12, (work.Height - window.Height) / 2);
    }

    private static Rect GetWorkArea(Window window, IntPtr handle, IntPtr? requestedMonitor)
    {
        var monitor = requestedMonitor.GetValueOrDefault();
        if (monitor == IntPtr.Zero) monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = MonitorInfo.Create();
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;
        return PixelRectToDipRectForWindow(window, handle, new PixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom));
    }

    private static IntPtr GetWindowHandle(Window window) =>
        (PresentationSource.FromVisual(window) as HwndSource)?.Handle ?? new WindowInteropHelper(window).Handle;

    private static Rect PixelRectToDipRectForWindow(Window window, IntPtr handle, PixelRect pixels)
    {
        // PointFromScreen is WPF's monitor-aware screen conversion API. It avoids treating absolute virtual-screen coordinates as pixels at this window's DPI.
        // HwndSource.FromHwnd(handle) can exist before this Window visual is connected.
        // PointFromScreen is valid only against the Window's own PresentationSource.
        if (GetConnectedPresentationSource(window)?.CompositionTarget is not null)
        {
            var topLeft = DesktopDipPointFromScreen(window, new System.Windows.Point(pixels.Left, pixels.Top));
            var bottomRight = DesktopDipPointFromScreen(window, new System.Windows.Point(pixels.Right, pixels.Bottom));
            return new Rect(topLeft, bottomRight);
        }
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var nativeWindow) &&
            double.IsFinite(window.Left) && double.IsFinite(window.Top) &&
            nativeWindow.Left > -30000 && nativeWindow.Top > -30000)
        {
            return PixelRectToDipRect(pixels, GetTransformFromDevice(window, handle), new ScreenCoordinateAnchor(new PixelPoint(nativeWindow.Left, nativeWindow.Top), new System.Windows.Point(window.Left, window.Top)));
        }

        // An unshown WPF Window has neither a PresentationSource nor trustworthy Left/Top.
        // Never feed NaN/-32000 into a placement calculation. Callers that need cursor-monitor
        // fidelity show at zero opacity first; this finite fallback is only a safe last resort.
        var transform = GetTransformFromDevice(window, handle);
        return PixelRectToDipRect(pixels, transform, new ScreenCoordinateAnchor(new PixelPoint(pixels.Left, pixels.Top), new System.Windows.Point(0, 0)));
    }

    private static System.Windows.Point DesktopDipPointFromScreen(Window window, System.Windows.Point screenPixels)
    {
        var local = window.PointFromScreen(screenPixels);
        return new System.Windows.Point(window.Left + local.X, window.Top + local.Y);
    }

    private static Matrix GetTransformFromDevice(Window window, IntPtr handle)
    {
        var source = GetHwndSource(window, handle);
        if (source?.CompositionTarget is not null) return source.CompositionTarget.TransformFromDevice;
        var dpi = VisualTreeHelper.GetDpi(window);
        var scale = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : Math.Max(1d, GetDpiForWindow(handle) / 96d);
        return new Matrix(1 / scale, 0, 0, 1 / scale, 0, 0);
    }

    private static Matrix GetTransformToDevice(Window window, IntPtr handle)
    {
        var source = GetHwndSource(window, handle);
        if (source?.CompositionTarget is not null) return source.CompositionTarget.TransformToDevice;
        var dpi = VisualTreeHelper.GetDpi(window);
        var scale = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : Math.Max(1d, GetDpiForWindow(handle) / 96d);
        return new Matrix(scale, 0, 0, scale, 0, 0);
    }

    private const uint MonitorDefaultToNearest = 2;
    private static HwndSource? GetConnectedPresentationSource(Window window) =>
        PresentationSource.FromVisual(window) as HwndSource;
    private static HwndSource? GetHwndSource(Window window, IntPtr handle) =>
        GetConnectedPresentationSource(window) ?? (handle == IntPtr.Zero ? null : HwndSource.FromHwnd(handle));
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int cbSize; public NativeRect rcMonitor, rcWork; public uint dwFlags; public static MonitorInfo Create() => new() { cbSize = Marshal.SizeOf<MonitorInfo>() }; }
}
