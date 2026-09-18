using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DrawingIcon = System.Drawing.Icon;

namespace Muesli.Windows.Services;

/// <summary>
/// Binds the product .ico to every WPF window, including the small icon Windows
/// shows in the taskbar thumbnail title when hovering a running app.
/// </summary>
internal static class AppShellIcon
{
    private const int WmGetIcon = 0x007F;
    private const int WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const int GclpHIcon = -14;
    private const int GclpHIconSm = -34;

    private static readonly ConditionalWeakTable<Window, NativeState> BoundWindows = new();
    private static readonly object Gate = new();
    private static bool _registered;
    private static DrawingIcon? _small;
    private static DrawingIcon? _big;
    private static DrawingIcon? _tray;
    private static BitmapFrame? _wpfIcon;

    public static string FileName => "muesli.ico";

    public static string? ResolvePath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", FileName);
        return File.Exists(path) ? path : null;
    }

    public static void RegisterForApplication()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            EventManager.RegisterClassHandler(
                typeof(Window),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnWindowLoaded));
            _registered = true;
        }
    }

    public static DrawingIcon? CreateNotifyIcon()
    {
        EnsureLoaded();
        return _tray is null ? null : (DrawingIcon)_tray.Clone();
    }

    internal static IReadOnlyList<int> ReadIconDirectorySizes(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 6)
        {
            return Array.Empty<int>();
        }

        var count = BitConverter.ToUInt16(bytes, 4);
        var sizes = new int[count];
        for (var i = 0; i < count; i++)
        {
            var offset = 6 + (i * 16);
            if (offset + 2 > bytes.Length)
            {
                break;
            }

            var width = bytes[offset];
            sizes[i] = width == 0 ? 256 : width;
        }

        return sizes;
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs args)
    {
        try
        {
            if (sender is Window window)
            {
                Bind(window);
            }
        }
        catch
        {
            // Icon binding must never prevent other Loaded handlers such as StartRuntime.
        }
    }

    private static void Bind(Window window)
    {
        if (!BoundWindows.TryAdd(window, new NativeState()))
        {
            return;
        }

        EnsureLoaded();
        if (_small is null || _big is null)
        {
            return;
        }

        if (_wpfIcon is not null)
        {
            window.Icon = _wpfIcon;
        }

        ApplyNative(window);
        window.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                ApplyNative(window);
            }
            catch
            {
                // Native icon apply is best-effort after the window is already shown.
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void ApplyNative(Window window)
    {
        if (!BoundWindows.TryGetValue(window, out var state) || _small is null || _big is null)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        SendMessage(hwnd, WmSetIcon, new IntPtr(IconSmall), _small.Handle);
        SendMessage(hwnd, WmSetIcon, new IntPtr(IconBig), _big.Handle);
        SetClassLongPtr(hwnd, GclpHIconSm, _small.Handle);
        SetClassLongPtr(hwnd, GclpHIcon, _big.Handle);

        if (state.Hooked)
        {
            return;
        }

        var source = HwndSource.FromHwnd(hwnd);
        if (source is null)
        {
            return;
        }

        source.AddHook(WndProc);
        state.Hooked = true;
    }

    private sealed class NativeState
    {
        public bool Hooked;
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetIcon || _small is null || _big is null)
        {
            return IntPtr.Zero;
        }

        handled = true;
        return wParam.ToInt32() == IconBig ? _big.Handle : _small.Handle;
    }

    private static void EnsureLoaded()
    {
        if (_small is not null)
        {
            return;
        }

        lock (Gate)
        {
            if (_small is not null)
            {
                return;
            }

            var path = ResolvePath();
            if (path is null)
            {
                return;
            }

            try
            {
                _small = new DrawingIcon(path, 16, 16);
                _big = new DrawingIcon(path, 32, 32);
                _tray = new DrawingIcon(path, 32, 32);
                var decoder = new IconBitmapDecoder(
                    new Uri(path, UriKind.Absolute),
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                _wpfIcon = decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();
                _wpfIcon.Freeze();
            }
            catch
            {
                _small?.Dispose();
                _big?.Dispose();
                _tray?.Dispose();
                _small = null;
                _big = null;
                _tray = null;
                _wpfIcon = null;
            }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
