using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// The folder operations the sidebar offers, as on macOS: create (then rename in place), rename,
/// new subfolder, move to top level and delete. They live here rather than in a page so the rail
/// and its tests share one set of rules.
/// </summary>
public static class MeetingFolderActions
{
    public const string DefaultName = "New Folder";
    public const string AllMeetingsName = "All Meetings";

    /// <summary>
    /// Creates a folder under <paramref name="parentId"/> (null for the top level) with a name
    /// that is free among its siblings: "New Folder", then "New Folder 2", and so on.
    /// </summary>
    public static PersistedMeetingFolder Create(WinUiLibraryContext library, string? parentId)
    {
        ArgumentNullException.ThrowIfNull(library);
        var parent = string.IsNullOrWhiteSpace(parentId) ? null : parentId;
        var siblings = library.History.LoadMeetingFolders()
            .Where(folder => string.Equals(folder.ParentId ?? "", parent ?? "", StringComparison.Ordinal))
            .Select(folder => folder.Name)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var name = DefaultName;
        for (var suffix = 2; siblings.Contains(name); suffix++)
        {
            name = $"{DefaultName} {suffix}";
        }

        var created = library.SaveMeetingFolder(null, name, parent);
        if (parent is not null) library.MoveMeetingFolder(created.Id, parent);
        return created with { ParentId = parent };
    }

    /// <summary>
    /// Renames a folder. Returns null when the name was saved (or was unchanged), otherwise the
    /// reason it was refused, worded for the user.
    /// </summary>
    public static string? Rename(WinUiLibraryContext library, string id, string? name)
    {
        ArgumentNullException.ThrowIfNull(library);
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0) return "Enter a folder name.";
        if (string.Equals(trimmed, AllMeetingsName, StringComparison.CurrentCultureIgnoreCase))
            return $"“{AllMeetingsName}” is the name of the whole library. Choose another name.";

        var stored = library.History.LoadMeetingFolders();
        var folder = stored.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (folder is null) return "That folder no longer exists.";
        if (string.Equals(folder.Name, trimmed, StringComparison.Ordinal)) return null;

        // Names only have to be unique among siblings: two folders may both be called "Notes"
        // under different parents, which is the point of nesting.
        var duplicate = stored.Any(other =>
            !string.Equals(other.Id, id, StringComparison.Ordinal) &&
            string.Equals(other.ParentId ?? "", folder.ParentId ?? "", StringComparison.Ordinal) &&
            string.Equals(other.Name, trimmed, StringComparison.CurrentCultureIgnoreCase));
        if (duplicate) return $"A folder called “{trimmed}” already exists here.";

        library.SaveMeetingFolder(id, trimmed, folder.ParentId);
        return null;
    }

    /// <summary>
    /// Moves a nested folder to the top level. Returns null when it moved, otherwise the reason it
    /// was refused: a top-level folder already has that name, and two identical names side by
    /// side cannot be told apart when filing a meeting.
    /// </summary>
    public static string? MoveToTopLevel(WinUiLibraryContext library, string id)
    {
        ArgumentNullException.ThrowIfNull(library);
        var stored = library.History.LoadMeetingFolders();
        var folder = stored.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (folder is null) return "That folder no longer exists.";
        if (string.IsNullOrWhiteSpace(folder.ParentId)) return null;
        var clash = stored.Any(other =>
            !string.Equals(other.Id, id, StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(other.ParentId) &&
            string.Equals(other.Name, folder.Name, StringComparison.CurrentCultureIgnoreCase));
        if (clash) return $"A folder called “{folder.Name}” already exists at the top level. Rename one of them first.";

        library.MoveMeetingFolder(id, null);
        return null;
    }

    /// <summary>
    /// Which folder a meeting's picker should show after folders changed elsewhere. A choice the
    /// user made but has not saved yet is kept as long as that folder still exists (including an
    /// explicit "no folder"); otherwise the picker follows what is stored. Ids use "" for no folder.
    /// </summary>
    public static string ResolvePickerSelection(
        string? pendingId,
        string? storedBeforeId,
        string? storedNowId,
        IReadOnlyCollection<string> existingFolderIds)
    {
        ArgumentNullException.ThrowIfNull(existingFolderIds);
        var pending = pendingId ?? "";
        var storedNow = storedNowId ?? "";
        var userChangedIt = !string.Equals(pending, storedBeforeId ?? "", StringComparison.Ordinal);
        if (userChangedIt && (pending.Length == 0 || existingFolderIds.Contains(pending))) return pending;
        return storedNow.Length == 0 || existingFolderIds.Contains(storedNow) ? storedNow : "";
    }

    /// <summary>Deletes a folder. Its meetings are unfiled, never deleted.</summary>
    public static bool Delete(WinUiLibraryContext library, string id)
    {
        ArgumentNullException.ThrowIfNull(library);
        return library.DeleteMeetingFolder(id);
    }
}
