namespace Muesli.Windows.Services;

public static class MeetingFolderHierarchy
{
    public static IReadOnlySet<string> Subtree(IEnumerable<PersistedMeetingFolder> folders, string root)
    {
        var children = folders.ToLookup(folder => folder.ParentId ?? "", StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var id))
        {
            if (!ids.Add(id)) continue;
            foreach (var child in children[id]) pending.Enqueue(child.Id);
        }
        return ids;
    }

    public static IReadOnlyList<MeetingFolderNode> Flatten(IEnumerable<PersistedMeetingFolder> folders)
    {
        var source = folders.OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var ids = source.Select(folder => folder.Id).ToHashSet(StringComparer.Ordinal);
        var children = source.ToLookup(folder => folder.ParentId ?? "", StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<MeetingFolderNode>();
        void Visit(PersistedMeetingFolder folder, int depth)
        {
            if (!visited.Add(folder.Id)) return;
            result.Add(new(folder, depth));
            foreach (var child in children[folder.Id]) Visit(child, depth + 1);
        }
        foreach (var folder in source.Where(folder => folder.ParentId is null || !ids.Contains(folder.ParentId))) Visit(folder, 0);
        foreach (var folder in source) Visit(folder, 0);
        return result;
    }
}

public sealed record MeetingFolderNode(PersistedMeetingFolder Folder, int Depth)
{
    public string DisplayName => new string(' ', Math.Min(Depth, 20) * 2) + Folder.Name;
}
