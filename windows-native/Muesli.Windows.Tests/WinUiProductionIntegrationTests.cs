using Muesli.Windows.Core.Profiles;
using Muesli.Windows.Services;
using Muesli.Windows.Services.Persistence;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.Tests;

public sealed class WinUiProductionIntegrationTests
{
    [Fact]
    public void ProtectedKeysSurviveContextRecreationAndAreIsolatedByProfile()
    {
        using var directory = new TestDirectory();
        using var other = new TestDirectory();
        using var library = new WinUiLibraryContext(MuesliProfilePaths.Isolated(directory.Path));
        using var otherLibrary = new WinUiLibraryContext(MuesliProfilePaths.Isolated(other.Path));
        var secrets = new WindowsCredentialSecretStore(directory.Path);
        try
        {
            var first = new WinUiSettingsContext(library);
            first.Load();
            first.SaveProviderKeys("test-openai-key", "test-openrouter-key");
            first.Save(new MuesliSettings { UserName = "Migration test" });
            var recreated = new WinUiSettingsContext(library).Load();
            Assert.Equal("test-openai-key", recreated.ResolvedOpenAIApiKey);
            Assert.Equal("test-openrouter-key", recreated.ResolvedOpenRouterApiKey);
            Assert.Equal("", new WinUiSettingsContext(otherLibrary).Load().ResolvedOpenAIApiKey);
            Assert.DoesNotContain("test-openai-key", File.ReadAllText(library.Profile.SettingsPath));
            first.SaveProviderKeys("", "");
            Assert.Equal("", new WinUiSettingsContext(library).Load().ResolvedOpenAIApiKey);
        }
        finally
        {
            secrets.Delete(SettingsStore.OpenAISecretKey);
            secrets.Delete(SettingsStore.OpenRouterSecretKey);
        }
    }

    [Fact]
    public void ExistingSqliteAuthorityIsUsedWithoutEnvironmentFlagAndDeletionSurvivesRestart()
    {
        using var directory = new TestDirectory();
        var profile = MuesliProfilePaths.Isolated(directory.Path);
        var json = new AppDataStore(profile.DataDirectory);
        json.SaveDictations([new PersistedDictation("dictation", DateTime.Now, "retained text", 1000, TranscriptionModelCatalog.DefaultModelId)]);
        Assert.True(new PersistenceCutover(profile.DataDirectory).EnsureMigrated().Succeeded);
        using (var library = new WinUiLibraryContext(profile))
        {
            Assert.IsType<SqliteLibraryHistoryAdapter>(library.History);
            Assert.Single(library.History.LoadDictations());
            Assert.True(library.DeleteDictation("dictation"));
        }
        using var reopened = new WinUiLibraryContext(profile);
        Assert.Empty(reopened.History.LoadDictations());
    }

    [Fact]
    public void RenamingNestedFolderKeepsItsParentAndMeetingAssignment()
    {
        using var directory = new TestDirectory();
        using var library = new WinUiLibraryContext(MuesliProfilePaths.Isolated(directory.Path));
        var parent = library.SaveMeetingFolder(null, "Parent");
        var child = library.SaveMeetingFolder(null, "Child", parent.Id);
        library.SaveMeetingFolder(child.Id, "Renamed");
        var saved = library.History.LoadMeetingFolders().Single(folder => folder.Id == child.Id);
        Assert.Equal(parent.Id, saved.ParentId);
        Assert.Equal("Renamed", saved.Name);
    }

    [Fact]
    public void SelectingFinalModelPublishesSettingsChangeAndPreservesDictationSelection()
    {
        using var directory = new TestDirectory();
        using var library = new WinUiLibraryContext(MuesliProfilePaths.Isolated(directory.Path));
        var settings = new WinUiSettingsContext(library, new InMemorySecretStore());
        var original = settings.Load();
        settings.Save(original);
        MuesliSettings? observed = null;
        settings.Changed += (_, changed) => observed = changed;
        using var models = new WinUiModelsContext(library, settings);
        var next = TranscriptionModelCatalog.Models.First(model => model.Id != original.FinalMeetingModelId).Id;
        models.SetActive(next, forMeetings: true);
        Assert.Equal(next, observed?.FinalMeetingModelId);
        Assert.Equal(next, settings.Load().FinalMeetingModelId);
        Assert.Equal(original.DictationModelId, settings.Load().DictationModelId);
    }
}
