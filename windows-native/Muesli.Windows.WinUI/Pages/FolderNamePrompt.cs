using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Muesli.Windows.WinUI.Pages;

/// <summary>The macOS "New Folder" / rename alert: a title, a message and a "Folder name" field.</summary>
internal static class FolderNamePrompt
{
    /// <summary>The trimmed name, or null when cancelled or left blank.</summary>
    public static async Task<string?> AskAsync(XamlRoot root, string title, string message, string initial, string primary = "Create")
    {
        var box = new TextBox { PlaceholderText = "Folder name", Text = initial };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new StackPanel
            {
                Spacing = 12,
                Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, box }
            },
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Folder name");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        var name = box.Text.Trim();
        return name.Length == 0 ? null : name;
    }
}

/// <summary>
/// The macOS folder popover: every folder by its path with a check on the current one, then
/// New Folder.... The list row adds an Unfiled entry; the detail pill instead clears the folder
/// when the checked one is chosen again.
/// </summary>
internal static class MeetingFolderMenu
{
    public static void Show(FrameworkElement anchor, string? currentFolderId, bool includeUnfiled,
        Action<string?> move, Action<string> createAndMove)
    {
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        if (includeUnfiled)
        {
            var unfiled = new ToggleMenuFlyoutItem { Text = "Unfiled", IsChecked = currentFolderId is null, Icon = new FontIcon { Glyph = "" } };
            unfiled.Click += (_, _) => move(null);
            menu.Items.Add(unfiled);
        }
        var stored = App.Library.History.LoadMeetingFolders();
        var folders = ViewModels.MeetingFolderListItem.BuildTree(App.Library.ReadSnapshot()).Where(folder => !folder.IsAllMeetings).ToList();
        if (includeUnfiled && folders.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var folder in folders)
        {
            var target = folder.Id;
            var current = currentFolderId == target;
            var item = new ToggleMenuFlyoutItem
            {
                Text = ViewModels.MeetingsPageViewModel.FolderPath(stored, target) ?? folder.Name,
                IsChecked = current,
                Icon = new FontIcon { Glyph = "" }
            };
            item.Click += (_, _) => move(current && !includeUnfiled ? null : target);
            menu.Items.Add(item);
        }
        if (folders.Count > 0 || includeUnfiled) menu.Items.Add(new MenuFlyoutSeparator());
        var create = new MenuFlyoutItem { Text = "New Folder...", Icon = new FontIcon { Glyph = "" } };
        create.Click += async (_, _) =>
        {
            if (await FolderNamePrompt.AskAsync(anchor.XamlRoot, "New Folder", "Create a new folder and move this meeting into it.", "") is { } name)
                createAndMove(name);
        };
        menu.Items.Add(create);
        menu.ShowAt(anchor);
    }
}
