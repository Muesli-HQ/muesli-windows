using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.System;

namespace Muesli.Windows.WinUI;

public sealed partial class ComputerUseConfirmationWindow : Window
{
    private const double WidthDip = 560;
    private const double HeightDip = 460;
    private readonly IntPtr _hwnd;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    /// <summary>Asks the user to allow one Computer Use step, for example clicking "Send".</summary>
    public ComputerUseConfirmationWindow(string question, string notice)
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        PreviewText.Text = question;
        NoticeText.Text = notice;
        ApplyChrome();
        Title = "Allow this Computer Use step?";
        ResizeDip(WidthDip, HeightDip);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        CenterInWorkArea();
        Activated += (_, _) => DeclineButton.Focus(FocusState.Programmatic);
        RootBorder.Loaded += (_, _) => DeclineButton.Focus(FocusState.Keyboard);
        RootBorder.ActualThemeChanged += (_, _) => ApplyChrome();
    }

    public bool Allowed { get; private set; }

    private void Allow_Click(object sender, RoutedEventArgs e)
    {
        Allowed = true;
        Close();
    }

    private void Decline_Click(object sender, RoutedEventArgs e)
    {
        Allowed = false;
        Close();
    }

    private void DeclineAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Decline_Click(sender, new RoutedEventArgs());
    }

    private void RootBorder_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        Decline_Click(sender, e);
    }

    /// <summary>Every step that reaches this window can send, delete, buy or close something.</summary>
    private void ApplyChrome()
    {
        RiskBadgeText.Text = "NEEDS YOUR OK";
        RiskBadgeText.Foreground = Brush("MuesliWarningBrush");
        RiskBadge.Background = Brush("MuesliSurfaceRaisedBrush");
        RiskSummaryText.Text = "Computer Use paused before this step. Nothing happens unless you allow it.";
        RiskBar.Severity = InfoBarSeverity.Warning;
        RiskBar.Title = "Your approval is required";
        RiskBar.Message = "This step may send, delete, buy or close something. Escape declines.";
        RiskBar.IsOpen = true;
        HeaderIcon.Glyph = "";
        HeaderIcon.Foreground = Brush("MuesliWarningBrush");
        HeaderGlyph.Background = Brush("MuesliSurfaceRaisedBrush");
        AllowButton.Style = (Style)Application.Current.Resources["MuesliPrimaryButtonStyle"];
        AutomationProperties.SetName(AllowButton, "Allow this Computer Use step");
        AutomationProperties.SetName(DeclineButton, "Decline this Computer Use step");
    }

    private static Brush Brush(string key) =>
        (Brush)Application.Current.Resources[key];

    /// <summary>
    /// Centres this confirmation on the work area of the display it opens on, never letting it
    /// extend past a screen edge.
    /// </summary>
    /// <remarks>
    /// This window only ever called <see cref="AppWindow.Resize"/>, so it took whatever position the
    /// OS gave it — the same defect class as P1-03 (dashboard) and the setup window's clamp. A
    /// Computer Use approval whose Allow/Decline buttons sit off the bottom of the screen is not a
    /// cosmetic problem, so it is placed explicitly. The clamp mirrors
    /// <c>MainWindow.ResizeForDpi</c> and <c>OnboardingWindow.ResizeForDpi</c> rather than
    /// re-deriving the arithmetic.
    /// </remarks>
    private void CenterInWorkArea()
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary)?.WorkArea;
            if (area is not { Width: > 0, Height: > 0 } bounds) return;

            var width = Math.Min(AppWindow.Size.Width, bounds.Width);
            var height = Math.Min(AppWindow.Size.Height, bounds.Height);
            AppWindow.MoveAndResize(new RectInt32(
                bounds.X + Math.Max(0, (bounds.Width - width) / 2),
                bounds.Y + Math.Max(0, (bounds.Height - height) / 2),
                width,
                height));
        }
        catch (Exception)
        {
            // Placement is a refinement; a display that will not resolve must not stop the approval
            // window from opening.
        }
    }

    private void ResizeDip(double widthDip, double heightDip)
    {
        var scale = Math.Max(1d, GetDpiForWindow(_hwnd) / 96d);
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(widthDip * scale),
            (int)Math.Round(heightDip * scale)));
    }
}
