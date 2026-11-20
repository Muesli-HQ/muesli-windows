using Muesli.Windows.Services;
using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Tests;

public sealed class FolderLaunchParityTests
{
    [Fact]
    public void PendingJsonFolderMutationRecoversBothFilesAfterPartialCommit()
    {
        using var directory = new TestDirectory();
        var store = new AppDataStore(directory.Path);
        store.SaveMeetingFolders([
            new PersistedMeetingFolder("parent", "Parent"),
            new PersistedMeetingFolder("deleted", "Deleted", "parent"),
            new PersistedMeetingFolder("child", "Child", "deleted")
        ]);
        store.SaveMeetings([new PersistedMeeting { Id = "meeting", FolderId = "deleted" }]);

        var recoveredFolders = new List<PersistedMeetingFolder>
        {
            new("parent", "Parent"),
            new("child", "Child", "parent")
        };
        var recoveredMeetings = new List<PersistedMeeting>
        {
            new() { Id = "meeting", FolderId = null }
        };
        var journalPath = directory.File("windows-library-mutation.json");
        new AtomicJsonFile().Save(
            journalPath,
            new LibraryMutationJournal(recoveredFolders, recoveredMeetings),
            AtomicJsonSaveMode.PrivacySensitive);

        // Simulate a crash after the first of the two data files was replaced.
        store.SaveMeetingFolders(recoveredFolders);
        var reopened = new AppDataStore(directory.Path);

        Assert.Null(Assert.Single(reopened.LoadMeetings()).FolderId);
        Assert.Equal("parent", reopened.LoadMeetingFolders().Single(folder => folder.Id == "child").ParentId);
        Assert.False(File.Exists(journalPath));
    }

    [Fact]
    public void SqliteFolderMoveRejectsUnknownFolderLikeJsonAdapter()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();

        var exception = Assert.Throws<PersistenceException>(() => store.Folders.Move("missing", null));

        Assert.Contains("does not exist", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NestedFolderUiOffersMoveToParentAndRoot()
    {
        // The shipping WinUI shell moves folders through the shared library context and the
        // meeting-library view model; the retired WPF menu handlers no longer exist.
        var library = File.ReadAllText(TestRepositoryLayout.Combine(
            "windows-native", "Muesli.Windows.WinUI", "Services", "WinUiLibraryContext.cs"));
        var viewModel = File.ReadAllText(TestRepositoryLayout.Combine(
            "windows-native", "Muesli.Windows.WinUI", "ViewModels", "LibraryPageViewModels.cs"));
        var adapter = File.ReadAllText(TestRepositoryLayout.Combine(
            "windows-native", "Muesli.Windows.Core", "Services", "Persistence", "Adapters", "ILibraryHistoryAdapter.cs"));

        Assert.Contains("public void MoveMeetingFolder(string id, string? parentId)", library, StringComparison.Ordinal);
        Assert.Contains("_library.MoveMeetingFolder(", viewModel, StringComparison.Ordinal);
        // A null parent id means "move to root"; the view model exposes the chosen parent.
        Assert.Contains("SelectedParentFolderId", viewModel, StringComparison.Ordinal);
        Assert.Contains("void MoveMeetingFolder(string id, string? newParentId)", adapter, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot() => TestRepositoryLayout.Root;
}
