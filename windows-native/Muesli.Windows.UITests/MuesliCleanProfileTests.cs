using System.Text.Json;

namespace Muesli.Windows.UITests;

public sealed class MuesliCleanProfileTests
{
    [Fact]
    public void Populated_profile_uses_truthful_shapes_and_cleans_up()
    {
        string root;
        using (var profile = new MuesliCleanProfile(seedPopulatedData: true))
        {
            root = profile.MuesliRoot;
            profile.AssertIsolatedFromDeveloperProfile();
            Assert.True(File.Exists(Path.Combine(root, MuesliCleanProfile.MarkerFileName)));

            var data = Path.Combine(root, "data");
            var dictations = ReadArray(Path.Combine(data, "windows-dictations.json"));
            var meetings = ReadArray(Path.Combine(data, "windows-meetings.json"));
            Assert.Equal(6, dictations.Count);
            Assert.Equal(3, meetings.Count);
            Assert.Contains(dictations, item => item.GetProperty("id").GetString()!.Contains("today", StringComparison.Ordinal));
            Assert.Contains(dictations, item => item.GetProperty("id").GetString()!.Contains("yesterday", StringComparison.Ordinal));
            Assert.Contains(meetings, item => item.GetProperty("summary").GetString()!.Length > 20);
            Assert.All(meetings, item =>
            {
                Assert.Equal(5, item.GetProperty("schemaVersion").GetInt32());
                Assert.True(item.GetProperty("durationMs").GetInt32() > 0);
                Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("templateName").GetString()));
            });

            Assert.Equal(3, ReadArray(Path.Combine(data, "windows-meeting-folders.json")).Count);
            Assert.Equal(4, ReadArray(Path.Combine(data, "windows-dictionary.json")).Count);
            Assert.Equal(3, ReadArray(Path.Combine(data, "windows-meeting-templates.json")).Count);
            using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "windows-settings.json")));
            Assert.Equal(8, settings.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(3, settings.RootElement.GetProperty("dictionarySuggestions").GetArrayLength());
        }

        Assert.False(Directory.Exists(root), "The generated populated profile must be removed after the test.");
    }

    [Fact]
    public void Empty_profile_stays_empty_and_isolated()
    {
        string root;
        using (var profile = new MuesliCleanProfile())
        {
            root = profile.MuesliRoot;
            profile.AssertIsolatedFromDeveloperProfile();
            Assert.False(File.Exists(Path.Combine(root, "data", "windows-dictations.json")));
            Assert.False(File.Exists(Path.Combine(root, "data", "windows-meetings.json")));
        }

        Assert.False(Directory.Exists(root));
    }

    private static List<JsonElement> ReadArray(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateArray().Select(item => item.Clone()).ToList();
    }
}
