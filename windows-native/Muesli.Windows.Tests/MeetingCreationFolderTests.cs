using System;
using System.IO;
using Xunit;

namespace Muesli.Windows.Tests;

/// <summary>MAC-09 (macOS 7c3abb66): Quick Note and audio import file the new meeting into the open folder.</summary>
public sealed class MeetingCreationFolderTests
{
    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([TestRepositoryLayout.Root, "windows-native", "Muesli.Windows.WinUI", .. parts]));

    [Fact]
    public void Both_creation_paths_pass_the_current_folder()
    {
        var page = Read("ViewModels", "LibraryPageViewModels.cs");
        Assert.Contains("_meetingRuntime.StartQuickNoteAsync(cancellationToken, SelectedFolderId)", page, StringComparison.Ordinal);
        // The import captures the folder before transcription, so navigating away doesn't move it.
        var import = page.IndexOf("private async Task ImportAudioAsync", StringComparison.Ordinal);
        var captured = page.IndexOf("var folderId = SelectedFolderId;", import, StringComparison.Ordinal);
        var call = page.IndexOf("_meetingRuntime.ImportAsync(picked.Path, progress, cancellationToken, folderId)", import, StringComparison.Ordinal);
        Assert.True(import < captured && captured < call);
    }

    [Fact]
    public void A_folder_deleted_meanwhile_is_dropped()
    {
        var runtime = Read("Services", "WinUiMeetingContext.cs");
        Assert.Contains("FolderId = ExistingFolder(folderId)", runtime, StringComparison.Ordinal);
        Assert.Contains("if (ExistingFolder(folderId) is { } folder && ActiveMeetingId is { } id)", runtime, StringComparison.Ordinal);
        Assert.Contains("LoadMeetingFolders().Any(folder => folder.Id == folderId)", runtime, StringComparison.Ordinal);
    }
}
