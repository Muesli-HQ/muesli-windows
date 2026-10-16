using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Muesli.Windows.Services;
using Windows.System;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// macOS DiagnosticIncidentReporter + DiagnosticIncidentReportView: after a hard failure, and only
/// when "Automatic issue reporting prompts" is on, show the anonymized report once per kind per day
/// and let the user open it as a GitHub issue. Nothing is sent unless they choose to.
/// </summary>
public static class DiagnosticIncidentPrompts
{
    // ponytail: throttle lives for this session; macOS persists it per app version and day.
    private static readonly Dictionary<string, DateOnly> Prompted = [];

    public static void Record(string kind, string stage, string? model, Exception? exception)
    {
        if (exception is OperationCanceledException) return;
        var incident = DiagnosticIncident.Create(kind, stage, model, exception);
        new AppLogService().Info($"Diagnostic incident. kind={kind}; stage={stage}; error={incident.Error.Signature}");
        if (App.Settings?.Load().EnableAutomaticDiagnosticIssuePrompts != true) return;
        var today = DateOnly.FromDateTime(DateTime.Now);
        lock (Prompted)
        {
            if (Prompted.TryGetValue(kind, out var day) && day == today) return;
            Prompted[kind] = today;
        }
        App.UiDispatcher.TryEnqueue(() =>
        {
            if (App.Window is MainWindow window) window.ShowDashboard("about");
            _ = ShowAsync(incident);
        });
    }

    /// <summary>The report sheet; <paramref name="secondary"/> adds one more action (Windows: the support bundle).</summary>
    public static async Task ShowAsync(DiagnosticIncident incident, string? secondary = null, Action? onSecondary = null)
    {
        if (App.Window.Content is not FrameworkElement owner) return;
        var dialog = new ContentDialog
        {
            XamlRoot = owner.XamlRoot,
            Content = Body(incident),
            PrimaryButtonText = "Open GitHub Issue",
            CloseButtonText = "Not Now",
            SecondaryButtonText = secondary ?? "",
            DefaultButton = ContentDialogButton.Primary,
            PrimaryButtonStyle = (Style)Application.Current.Resources["MuesliPrimaryButtonStyle"],
        };
        // macOS sheet: 560-720 wide; ContentDialog otherwise caps content at 548.
        dialog.Resources["ContentDialogMaxWidth"] = 720.0;
        foreach (var key in new[] { "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed" })
            dialog.Resources[key] = new SolidColorBrush(Colors.White);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dialog, Heading(incident));
        try
        {
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary) await Launcher.LaunchUriAsync(incident.IssueUri);
            else if (result == ContentDialogResult.Secondary) onSecondary?.Invoke();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Another dialog is open; the incident is already in the log.
        }
    }

    private static string Heading(DiagnosticIncident incident) =>
        incident.IsManual ? "Report a Problem" : "Diagnostic Failure Detected";

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    private static FrameworkElement Body(DiagnosticIncident incident)
    {
        var orange = global::Windows.UI.Color.FromArgb(255, 255, 149, 0);
        var stack = new StackPanel { Spacing = 20, Width = 600 };

        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.Children.Add(new FontIcon
        {
            Glyph = incident.IsManual ? "" : "",
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = incident.IsManual ? Resource("MuesliAccentBrush") : new SolidColorBrush(orange),
        });
        var titles = new StackPanel { Spacing = 4 };
        titles.Children.Add(new TextBlock { Text = Heading(incident), FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        titles.Children.Add(new TextBlock
        {
            Text = incident.IsManual
                ? "Muesli can prepare an anonymized GitHub issue for you to review before opening it."
                : $"Muesli detected a hard failure in {incident.Stage}. You can review the anonymized report before opening a GitHub issue.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Resource("MuesliTextSecondaryBrush"),
        });
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        stack.Children.Add(header);

        if (!incident.IsManual)
        {
            var rows = new StackPanel { Spacing = 8 };
            foreach (var (label, value) in new[]
            {
                ("Failure", incident.Title), ("Stage", incident.Stage), ("Model", incident.Model),
                ("Error", incident.ErrorDisplay), ("Meaning", incident.Error.Summary),
            })
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = Resource("MuesliTextTertiaryBrush") });
                var text = new TextBlock { Text = value, FontSize = 12, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true, Foreground = Resource("MuesliTextSecondaryBrush") };
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                rows.Children.Add(row);
            }
            stack.Children.Add(new Border
            {
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(26, orange.R, orange.G, orange.B)),
                BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(61, orange.R, orange.G, orange.B)),
                BorderThickness = new Thickness(1),
                Child = rows,
            });
        }

        stack.Children.Add(new TextBlock
        {
            Text = "Only allowlisted diagnostic categories and a random incident ID are included. No transcript, audio, meeting title, calendar title, clipboard contents, screen text, API keys, auth tokens, local file paths, raw error messages, raw logs, or database contents are included.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Resource("MuesliTextTertiaryBrush"),
        });
        stack.Children.Add(new Border
        {
            MinHeight = 240,
            MaxHeight = 320,
            CornerRadius = new CornerRadius(6),
            Background = Resource("MuesliSurfaceBrush"),
            BorderBrush = Resource("MuesliBorderSoftBrush"),
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = incident.IssueBody,
                    FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                    FontSize = 12,
                    Padding = new Thickness(12),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Foreground = Resource("MuesliTextSecondaryBrush"),
                },
            },
        });
        return stack;
    }
}
