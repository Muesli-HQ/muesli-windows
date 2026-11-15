using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Core.Profiles;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// Settings save as they are edited: there is no Save button, each field is checked on its own,
/// and one provider key can change without the others being rewritten.
/// </summary>
public sealed class SettingsAutosaveTests
{
    [Fact]
    public void SettingsPageHasNoSaveButtonOrUnsavedState()
    {
        var xaml = File.ReadAllText(TestRepositoryLayout.Combine(
            "windows-native", "Muesli.Windows.WinUI", "Pages", "SettingsPage.xaml"));
        var viewModel = File.ReadAllText(TestRepositoryLayout.Combine(
            "windows-native", "Muesli.Windows.WinUI", "ViewModels", "SettingsPageViewModel.cs"));

        Assert.DoesNotContain("Save settings", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Unsaved changes", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("HasUnsavedChanges", viewModel, StringComparison.Ordinal);
        // Detector and capture diagnostics do not belong on the Settings page.
        Assert.DoesNotContain("DetectionStatus", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureStatus", xaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://localhost:11434", true)]
    [InlineData("  https://example.com/v1/chat/completions ", true)]
    [InlineData("", false)]
    [InlineData("localhost:11434", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("http://", false)]
    public void HttpEndpointsMustBeAbsoluteHttpAddresses(string value, bool valid) =>
        Assert.Equal(valid, SettingsFieldValidation.HttpEndpoint(value) is null);

    [Theory]
    [InlineData("http://127.0.0.1:9222", true)]
    [InlineData("http://localhost:9222", true)]
    [InlineData("https://127.0.0.1:9222", false)]
    [InlineData("http://example.com:9222", false)]
    [InlineData("", false)]
    public void TheDevToolsEndpointMustStayOnLoopback(string value, bool valid) =>
        Assert.Equal(valid, SettingsFieldValidation.LoopbackEndpoint(value) is null);

    [Theory]
    [InlineData("", true)]
    [InlineData(@"C:\Notes\Meetings", true)]
    [InlineData(@"Notes\Meetings", false)]
    [InlineData(@"C:Notes", false)]
    [InlineData(@"C:\Notes|x", false)]
    public void TheExportFolderIsEmptyOrAFullPath(string value, bool valid) =>
        Assert.Equal(valid, SettingsFieldValidation.ExportDirectory(value) is null);

    [Fact]
    public void ThePostMeetingProgramIsEmptyOrAnExistingExe()
    {
        using var directory = new TestDirectory();
        var program = Path.Combine(directory.Path, "publish-notes.exe");
        File.WriteAllBytes(program, []);
        var script = Path.Combine(directory.Path, "publish-notes.cmd");
        File.WriteAllBytes(script, []);

        Assert.Null(SettingsFieldValidation.HookExecutable(""));
        Assert.Null(SettingsFieldValidation.HookExecutable(program));
        Assert.NotNull(SettingsFieldValidation.HookExecutable("publish-notes.exe"));
        Assert.NotNull(SettingsFieldValidation.HookExecutable(script));
        Assert.NotNull(SettingsFieldValidation.HookExecutable(Path.Combine(directory.Path, "missing.exe")));
    }

    [Fact]
    public void SavingOneProviderKeyLeavesTheOthersAndTheSettingsFileAlone()
    {
        using var directory = new TestDirectory();
        using var library = new WinUiLibraryContext(MuesliProfilePaths.Isolated(directory.Path));
        var secrets = new RecordingSecretStore();
        secrets.Write(SettingsStore.OpenAISecretKey, "sk-openai");
        secrets.Write(SettingsStore.OpenRouterSecretKey, "sk-router");
        var settings = new WinUiSettingsContext(library, secrets);
        settings.Save(settings.Load() with { UserName = "Ada" });
        secrets.Touched.Clear();
        var changedEvents = 0;
        settings.Changed += (_, _) => changedEvents++;

        settings.SaveProviderKey(SettingsStore.OpenRouterSecretKey, "  sk-router-2  ");

        Assert.Equal([SettingsStore.OpenRouterSecretKey], secrets.Touched);
        Assert.Equal("sk-router-2", settings.Load().ResolvedOpenRouterApiKey);
        Assert.Equal("sk-openai", settings.Load().ResolvedOpenAIApiKey);
        Assert.Equal("Ada", settings.Load().UserName);
        Assert.Equal(0, changedEvents);
        Assert.DoesNotContain("sk-router-2", File.ReadAllText(library.Profile.SettingsPath), StringComparison.Ordinal);

        settings.SaveProviderKey(SettingsStore.OpenRouterSecretKey, "");

        Assert.Equal("", settings.Load().ResolvedOpenRouterApiKey);
        Assert.False(secrets.IsConfigured(SettingsStore.OpenRouterSecretKey));
        // A fresh context reads the credential store, so the change survives a restart.
        Assert.Equal("sk-openai", new WinUiSettingsContext(library, secrets).Load().ResolvedOpenAIApiKey);
        Assert.Equal("", new WinUiSettingsContext(library, secrets).Load().ResolvedOpenRouterApiKey);
    }

    [Fact]
    public void AnUnknownProviderKeyIsRefused()
    {
        using var directory = new TestDirectory();
        using var library = new WinUiLibraryContext(MuesliProfilePaths.Isolated(directory.Path));
        var settings = new WinUiSettingsContext(library, new InMemorySecretStore());

        Assert.Throws<ArgumentOutOfRangeException>(() => settings.SaveProviderKey("not-a-key", "value"));
    }

    private sealed class RecordingSecretStore : ISecretStore
    {
        private readonly InMemorySecretStore _inner = new();
        public List<string> Touched { get; } = [];

        public string? Read(string key) => _inner.Read(key);

        public void Write(string key, string value)
        {
            Touched.Add(key);
            _inner.Write(key, value);
        }

        public void Delete(string key)
        {
            Touched.Add(key);
            _inner.Delete(key);
        }

        public bool IsConfigured(string key) => _inner.IsConfigured(key);
    }
}
