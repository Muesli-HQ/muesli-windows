using Muesli.Windows.Core.Profiles;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// The settings context is an in-memory snapshot: hot paths (hotkey, indicator, live transcript,
/// model status) must never pay a JSON read plus Credential Manager reads per call.
/// </summary>
public sealed class SettingsSnapshotTests
{
    [Fact]
    public void Reads_after_the_first_never_touch_disk_or_the_credential_store()
    {
        using var directory = new TestDirectory();
        using var library = new WinUiLibraryContext(MuesliProfilePaths.Isolated(directory.Path));
        var secrets = new CountingSecretStore();
        var settings = new WinUiSettingsContext(library, secrets);

        var first = settings.Load();
        var readsAfterFirstLoad = secrets.Reads;
        File.WriteAllText(library.Profile.SettingsPath, "{ \"UserName\": \"edited outside the app\" }");
        for (var i = 0; i < 1000; i++) settings.Load();

        Assert.Equal(3, readsAfterFirstLoad);
        Assert.Equal(readsAfterFirstLoad, secrets.Reads);
        Assert.Same(first, settings.Load());
        Assert.NotEqual("edited outside the app", settings.Load().UserName);
    }

    [Fact]
    public void Save_publishes_the_normalized_persisted_form_and_raises_changed()
    {
        using var directory = new TestDirectory();
        using var library = new WinUiLibraryContext(MuesliProfilePaths.Isolated(directory.Path));
        var settings = new WinUiSettingsContext(library, new InMemorySecretStore());
        MuesliSettings? observed = null;
        settings.Changed += (_, changed) => observed = changed;

        settings.Save(settings.Load() with { UserName = "Ada", Hotkey = "  F9  ", RecordingColorHex = "#ABCDEF" });

        var current = settings.Load();
        Assert.Same(current, observed);
        Assert.Equal("Ada", current.UserName);
        Assert.Equal("F9", current.Hotkey);
        Assert.Equal("abcdef", current.RecordingColorHex);
        // A fresh context reads the file; the snapshot must equal what was persisted. Records compare
        // collections by reference, so compare the serialized form.
        var reloaded = new WinUiSettingsContext(library, new InMemorySecretStore()).Load();
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(current), System.Text.Json.JsonSerializer.Serialize(reloaded));
    }

    [Fact]
    public void Saving_settings_without_keys_keeps_the_resolved_keys_in_the_snapshot()
    {
        using var directory = new TestDirectory();
        using var library = new WinUiLibraryContext(MuesliProfilePaths.Isolated(directory.Path));
        var secrets = new CountingSecretStore();
        secrets.Write(SettingsStore.OpenAISecretKey, "sk-test");
        var settings = new WinUiSettingsContext(library, secrets);
        settings.Load();

        settings.Save(new MuesliSettings { UserName = "No keys in this object" });

        Assert.Equal("sk-test", settings.Load().ResolvedOpenAIApiKey);
        Assert.DoesNotContain("sk-test", File.ReadAllText(library.Profile.SettingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Saving_provider_keys_updates_the_snapshot_and_the_credential_store()
    {
        using var directory = new TestDirectory();
        using var library = new WinUiLibraryContext(MuesliProfilePaths.Isolated(directory.Path));
        var secrets = new CountingSecretStore();
        secrets.Write(SettingsStore.CustomLlmSecretKey, "custom-existing");
        var settings = new WinUiSettingsContext(library, secrets);
        settings.Load();

        settings.SaveProviderKeys("  sk-openai  ", "sk-router");
        Assert.Equal("sk-openai", settings.Load().ResolvedOpenAIApiKey);
        Assert.Equal("sk-router", settings.Load().ResolvedOpenRouterApiKey);
        Assert.Equal("custom-existing", settings.Load().ResolvedCustomLlmApiKey);
        Assert.Equal("sk-openai", secrets.Read(SettingsStore.OpenAISecretKey));

        settings.SaveProviderKeys("", "", customLlm: "");
        Assert.Equal("", settings.Load().ResolvedOpenAIApiKey);
        Assert.Equal("", settings.Load().ResolvedCustomLlmApiKey);
        Assert.False(secrets.IsConfigured(SettingsStore.CustomLlmSecretKey));
    }

    [Fact]
    public void Concurrent_first_reads_load_from_disk_once_and_share_one_snapshot()
    {
        using var directory = new TestDirectory();
        using var library = new WinUiLibraryContext(MuesliProfilePaths.Isolated(directory.Path));
        var secrets = new CountingSecretStore();
        var settings = new WinUiSettingsContext(library, secrets);
        using var start = new ManualResetEventSlim();

        var readers = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => { start.Wait(); return settings.Load(); }))
            .ToArray();
        start.Set();
        // Blocking on purpose: the library's profile lease must be released on this thread.
        var results = Task.WhenAll(readers).GetAwaiter().GetResult();

        Assert.All(results, result => Assert.Same(results[0], result));
        Assert.Equal(3, secrets.Reads);
    }

    private sealed class CountingSecretStore : ISecretStore
    {
        private readonly InMemorySecretStore _inner = new();
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);

        public string? Read(string key)
        {
            Interlocked.Increment(ref _reads);
            return _inner.Read(key);
        }

        public void Write(string key, string value) => _inner.Write(key, value);
        public void Delete(string key) => _inner.Delete(key);
        public bool IsConfigured(string key) => _inner.IsConfigured(key);
    }
}
