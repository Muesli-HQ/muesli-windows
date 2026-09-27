using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.ViewModels;

namespace Muesli.Windows.WinUI.Pages;

/// <summary>
/// Prompt 9 (P3-01 class). A <see cref="ListViewItem"/> with no explicit name publishes its data
/// item's <c>ToString()</c>, and every item here is a C# record, so a screen reader announced
/// "TimelineGroup { Name = Today, Items = System.Collections.Generic.List`1[…] }". P3-01 fixed the
/// Meetings lists (<c>MeetingsPage.MeetingList_ContainerContentChanging</c>); a UIA sweep of every
/// surface found the same dump on the Timeline and Dictations day groups and on both Dictionary
/// lists. This gives those containers the same treatment, including the reset on recycle.
/// </summary>
internal static class ListItemAutomationNames
{
    public static void Apply(ContainerContentChangingEventArgs args)
    {
        var name = args.InRecycleQueue ? null : Describe(args.Item);
        AutomationProperties.SetName(args.ItemContainer, name ?? string.Empty);
    }

    private static string? Describe(object? item) => item switch
    {
        TimelineGroup group => Group(group.Name, group.Items.Count),
        DictationGroup group => Group(group.Name, group.Items.Count),
        DictionaryListItem entry => $"{entry.Phrase}, {entry.ReplacementLabel}, {entry.ThresholdLabel}",
        DictionarySuggestion suggestion => $"Suggested correction: {suggestion.Observed} to {suggestion.Replacement}",
        _ => null,
    };

    private static string Group(string name, int count) =>
        $"{name}, {count} {(count == 1 ? "item" : "items")}";
}
