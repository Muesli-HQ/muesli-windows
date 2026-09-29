using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Muesli.Windows.Services;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.System;

namespace Muesli.Windows.WinUI;

public sealed partial class ComputerUseConfirmationWindow : Window
{
    private const double WidthDip = 560;
    private const double HeightDip = 520;
    private readonly ComputerUseRisk _risk;
    private readonly IntPtr _hwnd;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    public ComputerUseConfirmationWindow(string preview, string notice)
        : this(preview, notice, ComputerUseRisk.None)
    {
    }

    public ComputerUseConfirmationWindow(string preview, string notice, ComputerUseRisk risk)
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _risk = DeriveRisk(preview, risk);
        PreviewText.Text = preview;
        NoticeText.Text = notice;
        ApplyRiskChrome(_risk);
        Title = "Review Computer Use action";
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
        RootBorder.ActualThemeChanged += (_, _) => ApplyRiskChrome(_risk);
    }

    public bool Allowed { get; private set; }

    public ComputerUseRisk DisplayedRisk => _risk;

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

    private void ApplyRiskChrome(ComputerUseRisk risk)
    {
        var (label, severity, glyphBrush, glyphFill, badgeBackground, badgeForeground, glyph) = risk switch
        {
            ComputerUseRisk.Irreversible => (
                "IRREVERSIBLE",
                InfoBarSeverity.Error,
                "MuesliErrorBrush",
                "MuesliErrorMutedBrush",
                "MuesliErrorMutedBrush",
                "MuesliErrorBrush",
                "\uE7BA"),
            ComputerUseRisk.Credential or ComputerUseRisk.Destructive => (
                risk == ComputerUseRisk.Credential ? "CREDENTIAL" : "DESTRUCTIVE",
                InfoBarSeverity.Error,
                "MuesliErrorBrush",
                "MuesliErrorMutedBrush",
                "MuesliErrorMutedBrush",
                "MuesliErrorBrush",
                "\uE72E"),
            ComputerUseRisk.Financial or ComputerUseRisk.External => (
                risk == ComputerUseRisk.Financial ? "FINANCIAL" : "EXTERNAL",
                InfoBarSeverity.Warning,
                "MuesliWarningBrush",
                "MuesliSurfaceRaisedBrush",
                "MuesliSurfaceRaisedBrush",
                "MuesliWarningBrush",
                "\uE7BA"),
            _ => (
                "STANDARD",
                InfoBarSeverity.Informational,
                "MuesliAccentBrush",
                "MuesliAccentMutedBrush",
                "MuesliAccentMutedBrush",
                "MuesliAccentBrush",
                "\uE946")
        };

        RiskBadgeText.Text = label;
        RiskBadgeText.Foreground = Brush(badgeForeground);
        RiskBadge.Background = Brush(badgeBackground);
        RiskSummaryText.Text =
            $"Risk: {risk}. Review the exact action below. This preview stays local and is not saved.";
        RiskBar.Severity = severity;
        RiskBar.Title = "Your approval is required";
        RiskBar.Message = severity switch
        {
            InfoBarSeverity.Error => "This action can change another app in a way that is hard to undo.",
            InfoBarSeverity.Warning => "This action reaches outside the current window or carries extra impact.",
            _ => "This is a standard window action. Confirm the target before allowing it."
        };
        RiskBar.IsOpen = true;
        HeaderIcon.Glyph = glyph;
        HeaderIcon.Foreground = Brush(glyphBrush);
        HeaderGlyph.Background = Brush(glyphFill);
        AllowButton.Style = (Style)Application.Current.Resources[
            severity == InfoBarSeverity.Error ? "MuesliDestructiveButtonStyle" : "MuesliPrimaryButtonStyle"];
        AutomationProperties.SetName(RiskBadge, $"Computer Use risk {label}");
        AutomationProperties.SetName(AllowButton, $"Allow Computer Use action, risk {label}");
        AutomationProperties.SetName(DeclineButton, "Decline Computer Use action");
    }

    internal static ComputerUseRisk DeriveRisk(string preview, ComputerUseRisk reported)
    {
        if (reported != ComputerUseRisk.None) return reported;
        if (string.IsNullOrWhiteSpace(preview)) return ComputerUseRisk.None;

        ComputerUseActionKind? kind = null;
        string automationId = "";
        foreach (var raw in preview.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith("Action:", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse(line["Action:".Length..].Trim(), ignoreCase: true, out ComputerUseActionKind parsed))
            {
                kind = parsed;
            }
            else if (line.StartsWith("UI Automation target:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["UI Automation target:".Length..].Trim();
                automationId = string.Equals(value, "(none)", StringComparison.OrdinalIgnoreCase) ? "" : value;
            }
        }

        if (kind is ComputerUseActionKind.BrowserNavigate or ComputerUseActionKind.BrowserInvoke or ComputerUseActionKind.SetText)
        {
            return ComputerUseRisk.External;
        }

        if (kind == ComputerUseActionKind.InvokeElement)
        {
            return ComputerUseRisk.Irreversible;
        }

        var target = automationId.ToLowerInvariant();
        if (target.Contains("credential") || target.Contains("password") || target.Contains("signin"))
        {
            return ComputerUseRisk.Credential;
        }

        if (target.Contains("payment") || target.Contains("purchase") || target.Contains("transfer") || target.Contains("pay"))
        {
            return ComputerUseRisk.Financial;
        }

        if (target.Contains("delete") || target.Contains("remove") || target.Contains("discard"))
        {
            return ComputerUseRisk.Destructive;
        }

        if (target.Contains("send") || target.Contains("share") || target.Contains("publish"))
        {
            return ComputerUseRisk.External;
        }

        return ComputerUseRisk.None;
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
