using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

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
