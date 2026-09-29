using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Muesli.Windows.WinUI;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const double DefaultWidthDip = 1280;
    private const double DefaultHeightDip = 820;
    private const double MinimumWidthDip = 720;
    private const double MinimumHeightDip = 640;

    private readonly IntPtr _hwnd;
    private double _effectiveWidthDip = DefaultWidthDip;
    private double _effectiveHeightDip = DefaultHeightDip;
    private uint _lastDpi;
    private bool _resizingForDpi;
    private bool _closeForExit;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    public MainWindow()
    {
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _lastDpi = GetDpiForWindow(_hwnd);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ResizeForDpi(_lastDpi, forceDefaultSize: true);
        AppWindow.Changed += AppWindow_Changed;
        AppWindow.Closing += (_, args) =>
        {
            if (_closeForExit) return;
            args.Cancel = true;
            HideDashboard();
        };
        WindowRoot.ActualThemeChanged += WindowRoot_ActualThemeChanged;
        WindowRoot.Loaded += WindowRoot_Loaded;

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
    }

    private void WindowRoot_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyCaptionButtonColors();
        var dpi = GetDpiForWindow(_hwnd);
        _lastDpi = dpi;
        // Construction can observe 96 DPI before the HWND is placed on a scaled monitor.
        // Re-apply so 1280×820 means effective pixels, not physical pixels.
        ResizeForDpi(dpi, forceDefaultSize: false);
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_resizingForDpi)
        {
            return;
        }

        var dpi = GetDpiForWindow(_hwnd);
        var scale = Math.Max(1d, dpi / 96d);
        var dpiChanged = dpi != _lastDpi;
        if (args.DidSizeChange && !dpiChanged)
        {
            // Keep the user's chosen physical size as effective DIPs, while enforcing the
            // compact shell's minimum. AppWindow.Size is physical pixels, not DIPs.
            _effectiveWidthDip = Math.Max(MinimumWidthDip, sender.Size.Width / scale);
            _effectiveHeightDip = Math.Max(MinimumHeightDip, sender.Size.Height / scale);
            if (sender.Size.Width < _effectiveWidthDip * scale ||
                sender.Size.Height < _effectiveHeightDip * scale)
            {
                ResizeForDpi(dpi, forceDefaultSize: false);
            }
        }

        // Moving between monitors can change the window DPI without changing its physical
        // bounds. Reapply the current effective size so the shell remains visually stable.
        if (dpiChanged)
        {
            _lastDpi = dpi;
            ResizeForDpi(dpi, forceDefaultSize: false);
        }
    }

    /// <summary>
    /// Applies the current effective size at the given DPI, clamped to the monitor's work area.
    /// </summary>
    /// <remarks>
    /// P1-03: the 1280×820 DIP default is taller than a 1080p display's 816 DIP work area at
    /// 125 % scale, and the previous implementation only called <see cref="AppWindow.Resize"/>,
    /// so a fresh profile opened with the bottom of the window ~105 px below the screen. The
    /// window is now never sized larger than the work area, and on the default-size path it is
    /// centred inside it; otherwise its existing position is nudged back inside. Harness sizing
    /// goes through Win32 <c>MoveWindow</c> and lands in <see cref="AppWindow_Changed"/> without
    /// re-entering this method, so the 1280×820 / 1008×800 / 720×720 test cases are unaffected.
    /// </remarks>
    private void ResizeForDpi(uint dpi, bool forceDefaultSize)
    {
        var scale = Math.Max(1d, dpi / 96d);
        if (forceDefaultSize)
        {
            _effectiveWidthDip = DefaultWidthDip;
            _effectiveHeightDip = DefaultHeightDip;
        }

        var width = Math.Max(MinimumWidthDip, _effectiveWidthDip);
        var height = Math.Max(MinimumHeightDip, _effectiveHeightDip);
        var widthPixels = (int)Math.Round(width * scale);
        var heightPixels = (int)Math.Round(height * scale);

        var workArea = TryGetWorkArea();
        if (workArea is { } area && area.Width > 0 && area.Height > 0)
        {
            widthPixels = Math.Min(widthPixels, area.Width);
            heightPixels = Math.Min(heightPixels, area.Height);
            _effectiveWidthDip = Math.Max(MinimumWidthDip, widthPixels / scale);
            _effectiveHeightDip = Math.Max(MinimumHeightDip, heightPixels / scale);
        }

        _resizingForDpi = true;
        try
        {
            if (workArea is { } bounds && bounds.Width > 0 && bounds.Height > 0)
            {
                var maxLeft = Math.Max(bounds.X, bounds.X + bounds.Width - widthPixels);
                var maxTop = Math.Max(bounds.Y, bounds.Y + bounds.Height - heightPixels);
                var left = forceDefaultSize
                    ? bounds.X + Math.Max(0, (bounds.Width - widthPixels) / 2)
                    : Math.Clamp(AppWindow.Position.X, bounds.X, maxLeft);
                var top = forceDefaultSize
                    ? bounds.Y + Math.Max(0, (bounds.Height - heightPixels) / 2)
                    : Math.Clamp(AppWindow.Position.Y, bounds.Y, maxTop);
                AppWindow.MoveAndResize(new RectInt32(left, top, widthPixels, heightPixels));
            }
            else
            {
                AppWindow.Resize(new SizeInt32(widthPixels, heightPixels));
            }
        }
        finally
        {
            _resizingForDpi = false;
        }
    }

    /// <summary>
    /// Work area (screen minus taskbar) of the display the window is on, in physical pixels.
    /// Returns null when no display can be resolved so sizing falls back to the old behaviour
    /// rather than failing.
    /// </summary>
    private RectInt32? TryGetWorkArea()
    {
        try
        {
            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            return display?.WorkArea;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void WindowRoot_ActualThemeChanged(FrameworkElement sender, object args) =>
        ApplyCaptionButtonColors();

    /// <summary>
    /// Delegates to the shared implementation (App.xaml.cs) so the dashboard and the setup window,
    /// which both extend content into the title bar, cannot drift apart. Behaviour is unchanged.
    /// </summary>
    private void ApplyCaptionButtonColors() =>
        App.ApplyCaptionButtonColors(AppWindow, WindowRoot.ActualTheme);

    public double EffectiveWidthDip => _effectiveWidthDip;

    public double EffectiveHeightDip => _effectiveHeightDip;

    /// <summary>
    /// Live outer width in effective pixels. Sidebar breakpoints use this instead of
    /// <c>Page.ActualWidth</c>, which is a few DIPs smaller because of window chrome.
    /// </summary>
    public double CurrentWidthDip
    {
        get
        {
            var scale = Math.Max(1d, GetDpiForWindow(_hwnd) / 96d);
            return AppWindow.Size.Width / scale;
        }
    }

    /// <summary>
    /// Keep the native caption drag region on the content column only. The sidebar is interactive
    /// chrome (brand, search, routes), not empty title-bar space, so it must stay outside the
    /// <see cref="SetTitleBar"/> hit-test strip. Caption buttons remain OS-owned at the right.
    /// </summary>
    public void SetTitleBarInset(double leftDip)
    {
        AppTitleBar.Margin = new Thickness(Math.Max(0, leftDip), 0, 0, 0);
    }

    public void ShowDashboard(string destination = "timeline")
    {
        AppWindow.Show(true);
        Activate();
        if (RootFrame.Content is MainPage page) page.NavigateTo(destination);
    }

    public void HideDashboard() => AppWindow.Hide();

    public void CloseForExit()
    {
        _closeForExit = true;
        Close();
    }

    public MainPage? ShellPage => RootFrame.Content as MainPage;

    public void StartFeatureTour() => ShellPage?.StartFeatureTour();
}
