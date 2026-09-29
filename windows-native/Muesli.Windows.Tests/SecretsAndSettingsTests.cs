namespace Muesli.Windows.Tests;

public sealed class SecretsAndSettingsTests
{
    [Fact]
    public void PostMeetingAutomationDefaultsAreSafeAndDisabled()
    {
        using var directory = new TestDirectory();
        var settings = new SettingsStore(directory.File("settings.json"), new InMemorySecretStore()).Load();

        Assert.False(settings.PostMeetingHookEnabled);
        Assert.Equal("", settings.PostMeetingHookExecutablePath);
        Assert.Equal("metadata-only", settings.PostMeetingHookTranscriptPolicy);
        Assert.Equal(30, settings.PostMeetingHookTimeoutSeconds);
        Assert.Equal(2, settings.PostMeetingHookMaxAttempts);
        Assert.False(settings.AutoExportMarkdownEnabled);
        Assert.Equal("", settings.AutoExportMarkdownDirectory);
        Assert.Equal("notes", settings.AutoExportMarkdownContent);
    }

    [Fact]
    public void ComputerUseDefaultsAreFailClosedAndPrivacyMinimized()
    {
        using var directory = new TestDirectory();
        var settings = new SettingsStore(directory.File("settings.json"), new InMemorySecretStore()).Load();

        Assert.False(settings.ComputerUseEnabled);
        Assert.Equal("none", settings.ComputerUsePlannerProvider);
        Assert.Equal("", settings.ComputerUsePlannerModel);
        Assert.Equal(30, settings.ComputerUsePlannerTimeoutSeconds);
        Assert.Equal(10, settings.ComputerUsePerActionTimeoutSeconds);
        Assert.Equal(5, settings.ComputerUseMaximumActionCount);
        Assert.Equal("", settings.ComputerUseAllowedApplications);
        Assert.Equal("", settings.ComputerUseAllowedBrowserDomains);
        Assert.False(settings.ComputerUseIncludeWindowText);
        Assert.False(settings.ComputerUseIncludeScreenshots);
        Assert.False(settings.ComputerUseIncludeBrowserPageText);
        Assert.Equal("none", settings.ComputerUseBrowserInterface);
    }

    [Fact]
    public void ComputerUseSettingsNormalizeBoundsAllowlistsAndLoopbackBrowserEndpoint()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        var store = new SettingsStore(path, new InMemorySecretStore());
        store.Save(new MuesliSettings
        {
            ComputerUseEnabled = true,
            ComputerUsePlannerProvider = "OPENAI",
            ComputerUsePlannerModel = "  gpt-planner  ",
            ComputerUsePlannerTimeoutSeconds = 999,
            ComputerUsePerActionTimeoutSeconds = 0,
            ComputerUseMaximumActionCount = 999,
            ComputerUseAllowedApplications = " Notepad;NOTEPAD, Explorer ",
            ComputerUseAllowedBrowserDomains = " Example.COM;example.com ",
            ComputerUseIncludeWindowText = true,
            ComputerUseBrowserInterface = "LOOPBACK-DEVTOOLS",
            ComputerUseBrowserEndpoint = "https://remote.example:9222"
        });

        var loaded = store.Load();
        Assert.True(loaded.ComputerUseEnabled);
        Assert.Equal("openai", loaded.ComputerUsePlannerProvider);
        Assert.Equal("gpt-planner", loaded.ComputerUsePlannerModel);
        Assert.Equal(120, loaded.ComputerUsePlannerTimeoutSeconds);
        Assert.Equal(1, loaded.ComputerUsePerActionTimeoutSeconds);
        Assert.Equal(20, loaded.ComputerUseMaximumActionCount);
        Assert.Equal("notepad; explorer", loaded.ComputerUseAllowedApplications);
        Assert.Equal("example.com", loaded.ComputerUseAllowedBrowserDomains);
        Assert.True(loaded.ComputerUseIncludeWindowText);
        Assert.Equal("loopback-devtools", loaded.ComputerUseBrowserInterface);
        Assert.Equal("http://127.0.0.1:9222", loaded.ComputerUseBrowserEndpoint);
    }

    [Fact]
    public void PostMeetingAutomationSettingsRoundTripLiteralUnicodePathsAndNormalizeBounds()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        var store = new SettingsStore(path, new InMemorySecretStore());
        var executable = @"C:\Hooks & Tools\完了 hook.exe";
        var destination = @"D:\Meeting exports\Müsli";

        store.Save(new MuesliSettings
        {
            PostMeetingHookEnabled = true,
            PostMeetingHookExecutablePath = executable,
            PostMeetingHookTranscriptPolicy = "INLINE",
            PostMeetingHookTimeoutSeconds = 9999,
            PostMeetingHookMaxAttempts = 99,
            AutoExportMarkdownEnabled = true,
            AutoExportMarkdownDirectory = destination,
            AutoExportMarkdownContent = "FULL-MEETING"
        });
        var loaded = store.Load();

        Assert.True(loaded.PostMeetingHookEnabled);
        Assert.Equal(executable, loaded.PostMeetingHookExecutablePath);
        Assert.Equal("inline", loaded.PostMeetingHookTranscriptPolicy);
        Assert.Equal(600, loaded.PostMeetingHookTimeoutSeconds);
        Assert.Equal(3, loaded.PostMeetingHookMaxAttempts);
        Assert.True(loaded.AutoExportMarkdownEnabled);
        Assert.Equal(destination, loaded.AutoExportMarkdownDirectory);
        Assert.Equal("full-meeting", loaded.AutoExportMarkdownContent);
        Assert.Equal(MuesliSettings.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public void FutureSettingsSchemaIsPreservedAndThisVersionDoesNotOverwriteIt()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        var futureJson = "{\"SchemaVersion\":999,\"FutureAutomationField\":\"keep-me\"}";
        File.WriteAllText(path, futureJson);
        var store = new SettingsStore(path, new InMemorySecretStore());

        var loaded = store.Load();
        store.Save(loaded with { PostMeetingHookEnabled = true });

        Assert.Contains("newer than this app supports", store.LastWarning, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(futureJson, File.ReadAllText(path));
    }

    [Fact]
    public void FailedSecretMigrationSuppressesLaterSettingsWritesSoPlaintextCanRetryAfterRestart()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        var original = "{\"SchemaVersion\":5,\"OpenAIApiKey\":\"retry-secret\"}";
        File.WriteAllText(path, original);
        var store = new SettingsStore(path, new ThrowingSecretStore());

        var loaded = store.Load();
        store.Save(loaded with { PostMeetingHookEnabled = true });

        Assert.Equal("retry-secret", loaded.ResolvedOpenAIApiKey);
        Assert.Contains("could not be completed", store.LastWarning, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void PlaintextKeysMigrateAndDisappearFromEveryPersistenceArtifact()
    {
        using var directory = new TestDirectory();
        var path = directory.File("windows-settings.json");
        File.WriteAllText(
            path,
            "{\"Hotkey\":\"F9\",\"OpenAIApiKey\":\"openai-secret\",\"OpenRouterApiKey\":\"router-secret\"}");
        File.WriteAllText(directory.File("windows-settings.json.bak"), "openai-secret router-secret");
        File.WriteAllText(directory.File(".windows-settings.json.abandoned.tmp"), "openai-secret router-secret");
        File.WriteAllText(directory.File("windows-settings.json.abandoned.recovery.tmp"), "openai-secret router-secret");
        File.WriteAllText(directory.File("windows-settings.json.corrupt-abandoned.json"), "openai-secret router-secret");
        File.WriteAllText(directory.File("windows-settings.json.bak.corrupt-abandoned.json"), "openai-secret router-secret");
        var secrets = new InMemorySecretStore();
        var store = new SettingsStore(path, secrets);

        var settings = store.Load();

        Assert.Equal("openai-secret", settings.ResolvedOpenAIApiKey);
        Assert.Equal("router-secret", settings.ResolvedOpenRouterApiKey);
        Assert.True(store.IsSecretConfigured(SettingsStore.OpenAISecretKey));
        Assert.True(store.IsSecretConfigured(SettingsStore.OpenRouterSecretKey));
        foreach (var artifact in Directory.EnumerateFiles(directory.Path))
        {
            var persisted = File.ReadAllText(artifact);
            Assert.DoesNotContain("openai-secret", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("router-secret", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("OpenAIApiKey", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("OpenRouterApiKey", persisted, StringComparison.Ordinal);
        }
        Assert.Single(Directory.EnumerateFiles(directory.Path));
        Assert.Equal("F9", settings.Hotkey);
    }

    [Fact]
    public void StoredKeyCanBeClearedWithoutSerializingIt()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        var secrets = new InMemorySecretStore();
        var store = new SettingsStore(path, secrets);
        store.SaveSecret(SettingsStore.OpenRouterSecretKey, "router-secret");
        store.Save(new MuesliSettings { ResolvedOpenRouterApiKey = "router-secret" });

        Assert.DoesNotContain("router-secret", File.ReadAllText(path), StringComparison.Ordinal);
        store.SaveSecret(SettingsStore.OpenRouterSecretKey, null);
        Assert.False(store.IsSecretConfigured(SettingsStore.OpenRouterSecretKey));
    }

    private sealed class ThrowingSecretStore : ISecretStore
    {
        public string? Read(string key) => null;
        public void Write(string key, string value) => throw new IOException("credential store unavailable");
        public void Delete(string key) { }
        public bool IsConfigured(string key) => false;
    }
}
