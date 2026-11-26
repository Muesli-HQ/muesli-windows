using Muesli.Windows.Core.Profiles;
using Muesli.Windows.Services;
using Muesli.Windows.Services.Persistence;
using Muesli.Windows.WinUI.Services;

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
    public void TheSidebarCreatesAFolderWithAFreeNameReadyToBeRenamed()
    {
        using var directory = new TestDirectory();
        using var library = new WinUiLibraryContext(MuesliProfilePaths.Isolated(directory.Path));

        var first = MeetingFolderActions.Create(library, null);
        var second = MeetingFolderActions.Create(library, null);
        var child = MeetingFolderActions.Create(library, first.Id);

        Assert.Equal("New Folder", first.Name);
        Assert.Equal("New Folder 2", second.Name);
        // A name only has to be free among siblings.
        Assert.Equal("New Folder", child.Name);
        var stored = library.History.LoadMeetingFolders();
        Assert.Equal(3, stored.Count);
        Assert.Null(stored.Single(folder => folder.Id == first.Id).ParentId);
        Assert.Equal(first.Id, stored.Single(folder => folder.Id == child.Id).ParentId);
    }

    [Fact]
    public void RenamingAFolderRefusesEmptyReservedAndDuplicateSiblingNames()
    {
        using var directory = new TestDirectory();
        using var library = new WinUiLibraryContext(MuesliProfilePaths.Isolated(directory.Path));
        var work = MeetingFolderActions.Create(library, null);
        var other = MeetingFolderActions.Create(library, null);
        var nested = MeetingFolderActions.Create(library, work.Id);
        Assert.Null(MeetingFolderActions.Rename(library, work.Id, "  Work  "));

        Assert.NotNull(MeetingFolderActions.Rename(library, other.Id, "   "));
        Assert.NotNull(MeetingFolderActions.Rename(library, other.Id, "all meetings"));
        Assert.Contains("already exists", MeetingFolderActions.Rename(library, other.Id, "work"));
        Assert.NotNull(MeetingFolderActions.Rename(library, "missing", "Anything"));
        // The same name under a different parent is allowed, and keeps the folder where it was.
        Assert.Null(MeetingFolderActions.Rename(library, nested.Id, "Work"));
        // Saving the name a folder already has is not a clash with itself.
        Assert.Null(MeetingFolderActions.Rename(library, work.Id, "Work"));

        var stored = library.History.LoadMeetingFolders();
        Assert.Equal("Work", stored.Single(folder => folder.Id == work.Id).Name);
        Assert.Equal("New Folder 2", stored.Single(folder => folder.Id == other.Id).Name);
        Assert.Equal("Work", stored.Single(folder => folder.Id == nested.Id).Name);
        Assert.Equal(work.Id, stored.Single(folder => folder.Id == nested.Id).ParentId);
    }

    [Fact]
    public void ASubfolderCanMoveToTheTopLevelAndDeletingAFolderKeepsItsMeetings()
    {
        using var directory = new TestDirectory();
        using var library = new WinUiLibraryContext(MuesliProfilePaths.Isolated(directory.Path));
        var parent = MeetingFolderActions.Create(library, null);
        var child = MeetingFolderActions.Create(library, parent.Id);
        library.SaveMeeting(new PersistedMeeting { Id = "m", Title = "Planning", FolderId = child.Id, CreatedAt = DateTime.UtcNow });

        // Both are called "New Folder": side by side at the top level they could not be told apart
        // when filing a meeting, so the move is refused until one is renamed.
        Assert.Contains("already exists", MeetingFolderActions.MoveToTopLevel(library, child.Id));
        Assert.Equal(parent.Id, library.History.LoadMeetingFolders().Single(folder => folder.Id == child.Id).ParentId);
        Assert.Null(MeetingFolderActions.Rename(library, child.Id, "Archive"));

        Assert.Null(MeetingFolderActions.MoveToTopLevel(library, child.Id));
        Assert.Null(library.History.LoadMeetingFolders().Single(folder => folder.Id == child.Id).ParentId);

        Assert.True(MeetingFolderActions.Delete(library, child.Id));

        Assert.DoesNotContain(library.History.LoadMeetingFolders(), folder => folder.Id == child.Id);
        var meeting = Assert.Single(library.History.LoadMeetings());
        Assert.Equal("m", meeting.Id);
        Assert.Null(meeting.FolderId);
        Assert.False(MeetingFolderActions.Delete(library, child.Id));
    }

    [Fact]
    public void SavingAMeetingThatStillNamesADeletedFolderKeepsItsNotesAndUnfilesIt()
    {
        // A meeting can be open while its folder is deleted from the sidebar. The page still holds
        // the old folder; saving must not be rejected and lose what the user typed.
        using var directory = new TestDirectory();
        using var library = new WinUiLibraryContext(MuesliProfilePaths.Isolated(directory.Path));
        var folder = MeetingFolderActions.Create(library, null);
        var open = new PersistedMeeting { Id = "m", Title = "Planning", FolderId = folder.Id, CreatedAt = DateTime.UtcNow };
        library.SaveMeeting(open);
        Assert.True(MeetingFolderActions.Delete(library, folder.Id));

        Assert.True(library.UpdateMeeting(open with { ManualNotes = "typed after the folder was deleted" }));

        var stored = library.FindMeeting("m")!;
        Assert.Equal("typed after the folder was deleted", stored.ManualNotes);
        Assert.Null(stored.FolderId);
    }

    [Theory]
    // pending, stored before, stored now, expected. "" is "No folder".
    [InlineData("projects", "inbox", "inbox", "projects")]   // unsaved pick survives a rename elsewhere
    [InlineData("", "inbox", "inbox", "")]                    // an unsaved "No folder" is a choice too
    [InlineData("inbox", "inbox", "inbox", "inbox")]          // nothing pending: follow what is stored
    [InlineData("inbox", "inbox", "", "")]                    // its folder was deleted: now unfiled
    [InlineData("gone", "inbox", "inbox", "inbox")]           // the unsaved pick itself was deleted
    [InlineData("gone", "inbox", "", "")]                     // both were deleted
    public void AMeetingsUnsavedFolderChoiceSurvivesFolderChangesElsewhere(
        string pending, string storedBefore, string storedNow, string expected)
    {
        var existing = new HashSet<string>(StringComparer.Ordinal) { "inbox", "projects" };
        if (storedNow.Length == 0) existing.Remove(storedBefore);

        Assert.Equal(expected, MeetingFolderActions.ResolvePickerSelection(pending, storedBefore, storedNow, existing));
    }

    [Fact]
    public void TheMeetingsPageNoLongerCarriesAFolderEditor()
    {
        // macOS manages folders from the sidebar and has no folder editor on the meetings page.
        var page = File.ReadAllText(TestRepositoryLayout.Combine(
            "windows-native", "Muesli.Windows.WinUI", "Pages", "MeetingsPage.xaml"));
        var shell = File.ReadAllText(TestRepositoryLayout.Combine(
            "windows-native", "Muesli.Windows.WinUI", "MainPage.xaml.cs"));

        Assert.DoesNotContain("Manage folders", page, StringComparison.Ordinal);
        Assert.DoesNotContain("MeetingFoldersExpander", page, StringComparison.Ordinal);
        foreach (var action in new[] { "New subfolder", "Rename", "Move to top level", "Delete" })
            Assert.Contains($"Item(\"{action}\"", shell, StringComparison.Ordinal);

        // The icon-only rail of a narrow window hides the folder rows, so it must keep its own way in.
        var rail = File.ReadAllText(TestRepositoryLayout.Combine(
            "windows-native", "Muesli.Windows.WinUI", "MainPage.xaml"));
        Assert.Contains("CompactMeetingFoldersButton", rail, StringComparison.Ordinal);
        Assert.Contains("CompactFoldersButton.Visibility = compact ? Visibility.Visible", shell, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot() => TestRepositoryLayout.Root;
}
