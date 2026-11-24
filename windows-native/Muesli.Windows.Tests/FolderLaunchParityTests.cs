using Muesli.Windows.Services;
using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Tests;

public sealed class FolderLaunchParityTests
{
    [Fact]
    public void SqliteFolderMoveRejectsUnknownFolder()
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
