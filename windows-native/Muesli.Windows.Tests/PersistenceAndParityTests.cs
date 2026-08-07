using System.Text.Json;
using System.IO;
using Muesli.Windows.Services;
using Xunit;

namespace Muesli.Windows.Tests;

public sealed class PersistenceAndParityTests
{
    [Fact]
    public void LoadsLegacyArrayAndWritesVersionedEnvelope()
    {
        using var directory = new TestDirectory();
        var path = directory.File("history.json");
        File.WriteAllText(path, "[\"one\",\"two\"]");
        var store = new AtomicJsonFile();

        Assert.Equal(["one", "two"], store.Load(path, new List<string>()).Value);
        store.Save(path, new[] { "three" });

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(AtomicJsonFile.CurrentSchemaVersion, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void RecoversFromBackupAndQuarantinesCorruption()
    {
        using var directory = new TestDirectory();
        var path = directory.File("history.json");
        var store = new AtomicJsonFile();
        store.Save(path, new[] { "first" });
        store.Save(path, new[] { "second" });
        File.WriteAllText(path, "{broken");

        var result = store.Load(path, Array.Empty<string>());

        Assert.True(result.RecoveredFromBackup);
        Assert.Equal(["first"], result.Value);
        Assert.Contains(Directory.EnumerateFiles(directory.Path), file => file.Contains(".corrupt-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Um, please, you know, send this.", "Please, send this.")]
    [InlineData("like, this is ready", "This is ready")]
    [InlineData("I like this result.", "I like this result.")]
    [InlineData("kind of sort of finished", "Finished")]
    public void FillerRemovalIsDeterministic(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Apply(input));

    [Fact]
    public void PortableDictionaryAcceptsMacAndCliSchema()
    {
        using var directory = new TestDirectory();
        var path = directory.File("dictionary.json");
        File.WriteAllText(path, "[{\"word\":\"museli\",\"replacement\":\"Muesli\",\"matching_threshold\":0.9}]");

        var entry = Assert.Single(DictionaryPortabilityService.Import(path));

        Assert.Equal("museli", entry.Phrase);
        Assert.Equal("Muesli", entry.Replacement);
        Assert.Equal(0.9, entry.MatchingThreshold);
    }

    [Fact]
    public void PlaintextKeysMigrateToSecretStoreAndAreRemovedFromJson()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        File.WriteAllText(path, "{\"OpenAIApiKey\":\"secret-openai\",\"OpenRouterApiKey\":\"secret-router\"}");
        var secrets = new InMemorySecretStore();
        var store = new SettingsStore(secrets, path);

        var settings = store.Load();

        Assert.Equal("secret-openai", settings.OpenAIApiKey);
        Assert.Equal("secret-router", settings.OpenRouterApiKey);
        var persisted = File.ReadAllText(path);
        Assert.DoesNotContain("secret-openai", persisted);
        Assert.DoesNotContain("secret-router", persisted);
    }
}

internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"muesli-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }
    public string Path { get; }
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
