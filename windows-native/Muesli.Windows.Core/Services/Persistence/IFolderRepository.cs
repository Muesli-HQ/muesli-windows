namespace Muesli.Windows.Services.Persistence;

public enum FolderDeleteMode
{
    /// <summary>Child folders move up to the deleted folder's parent and items become unfiled.</summary>
    Detach,

    /// <summary>Deletes the subtree; meetings and dictations inside it become unfiled, not deleted.</summary>
    CascadeFolders
}

/// <summary>
/// The folder tree. Deleting a folder never deletes the history inside it — losing recordings to a
/// folder tidy-up is not recoverable for the user, so items are unfiled instead.
/// </summary>
public interface IFolderRepository
{
    FolderRecord? Find(string id);

    IReadOnlyList<FolderRecord> List();

    IReadOnlyList<FolderRecord> ListChildren(string? parentId);

    /// <summary>Root-first path to <paramref name="id"/>, excluding the folder itself.</summary>
    IReadOnlyList<FolderRecord> ListAncestors(string id);

    /// <summary>The folder and every folder beneath it, breadth-first.</summary>
    IReadOnlyList<FolderRecord> ListSubtree(string id);

    void Upsert(FolderRecord folder);

    void UpsertRange(IEnumerable<FolderRecord> folders);

    /// <summary>Reparents a folder, rejecting a move that would put a folder inside its own subtree.</summary>
    void Move(string id, string? newParentId);

    bool Delete(string id, FolderDeleteMode mode = FolderDeleteMode.Detach);

    int Count();
}
