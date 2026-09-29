using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Muesli.Windows.Services;
using Xunit;

namespace Muesli.Windows.Tests;

/// <summary>
/// Priority 3: centralized post-deserialization normalization for every non-nullable setting.
/// The reflection contract test fails whenever a new non-nullable property is added without
/// normalization coverage.
/// </summary>
public sealed class SettingsNormalizationTests
{
    private static string NullHeavyJson()
    {
        var names = typeof(MuesliSettings).GetProperties()
            .Where(property => property.SetMethod is not null)
            .Where(property => !property.PropertyType.IsValueType)
            .Where(property => property.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name)
            .Where(name => !string.Equals(name, nameof(MuesliSettings.SchemaVersion), StringComparison.Ordinal))
            .Distinct();
        return "{ " + string.Join(", ", names.Select(name => $"\"{name}\": null")) + ", \"SchemaVersion\": 8 }";
    }

    [Fact]
    public void EveryNonNullableReferenceSettingIsNormalizedToNonNull()
    {
        var nullability = new NullabilityInfoContext();
        var deserialized = JsonSerializer.Deserialize<MuesliSettings>(NullHeavyJson())
            ?? throw new InvalidOperationException("Settings deserialized to null.");
        var normalized = SettingsStore.NormalizeAfterDeserialization(deserialized);

        var missing = new List<string>();
        foreach (var property in typeof(MuesliSettings).GetProperties())
        {
            if (property.PropertyType.IsValueType)
            {
                continue;
            }

            if (nullability.Create(property).WriteState != NullabilityState.NotNull)
            {
                continue;
            }

            if (property.GetValue(normalized) is null)
            {
                missing.Add(property.Name);
            }
        }

        Assert.True(missing.Count == 0,
            "Non-nullable settings missing normalization coverage: " + string.Join(", ", missing));
    }

    [Fact]
    public void MalformedValuesRecoverToDocumentedDefaults()
    {
        var normalized = SettingsStore.NormalizeAfterDeserialization(new MuesliSettings
        {
            RecordingColorHex = "not-a-colour",
            ComputerUseBrowserEndpoint = "http://evil.example.com/",
            OllamaEndpoint = "not a url",
            Theme = "   ",
            Hotkey = "   ",
            PasteBehavior = "",
            IndicatorAnchor = "",
            OpenAIModel = "",
            OllamaModel = "  ",
            DictionarySuggestions = null!,
        });

        Assert.Equal("1e1e2e", normalized.RecordingColorHex);
        Assert.Equal("http://127.0.0.1:9222", normalized.ComputerUseBrowserEndpoint);
        Assert.Equal("http://localhost:11434", normalized.OllamaEndpoint);
        Assert.Equal("dark", normalized.Theme);
        Assert.Equal("F8", normalized.Hotkey);
        Assert.Equal("active-app", normalized.PasteBehavior);
        Assert.Equal("Middle Right", normalized.IndicatorAnchor);
        Assert.Equal("gpt-5.4-mini", normalized.OpenAIModel);
        Assert.Equal("llama3.1:8b", normalized.OllamaModel);
        Assert.NotNull(normalized.DictionarySuggestions);
        Assert.Empty(normalized.DictionarySuggestions);
    }

    [Fact]
    public void ExplicitNullsThroughTheRealStoreProduceUsableSettings()
    {
        using var directory = new TempDirectory();
        var settingsPath = Path.Combine(directory.Path, "windows-settings.json");
        File.WriteAllText(settingsPath, NullHeavyJson());
        var store = new SettingsStore(settingsPath, new InMemorySecretStore());

        var settings = store.Load();

        var nullability = new NullabilityInfoContext();
        foreach (var property in typeof(MuesliSettings).GetProperties())
        {
            if (property.PropertyType.IsValueType ||
                nullability.Create(property).WriteState != NullabilityState.NotNull)
            {
                continue;
            }

            Assert.NotNull(property.GetValue(settings));
        }
    }

    [Fact]
    public void FutureSchemaIsPreservedAndNotOverwritten()
    {
        using var directory = new TempDirectory();
        var settingsPath = Path.Combine(directory.Path, "windows-settings.json");
        var future = "{ \"SchemaVersion\": 999, \"UserName\": \"future\" }";
        File.WriteAllText(settingsPath, future);
        var store = new SettingsStore(settingsPath, new InMemorySecretStore());

        var settings = store.Load();
        Assert.Equal(MuesliSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Contains("newer", store.LastWarning, StringComparison.OrdinalIgnoreCase);

        store.Save(settings with { UserName = "changed" });
        Assert.Equal(future, File.ReadAllText(settingsPath));
    }

    [Fact]
    public void CorruptJsonRecoversToDefaultsWithoutThrowing()
    {
        using var directory = new TempDirectory();
        var settingsPath = Path.Combine(directory.Path, "windows-settings.json");
        File.WriteAllText(settingsPath, "{ this is not json");
        var store = new SettingsStore(settingsPath, new InMemorySecretStore());

        var settings = store.Load();

        Assert.NotNull(settings);
        Assert.Equal("F8", settings.Hotkey);
    }

    [Fact]
    public void EmptyDocumentUsesDefaults()
    {
        using var directory = new TempDirectory();
        var settingsPath = Path.Combine(directory.Path, "windows-settings.json");
        File.WriteAllText(settingsPath, "{}");
        var store = new SettingsStore(settingsPath, new InMemorySecretStore());

        var settings = store.Load();

        Assert.Equal(MuesliSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal("F8", settings.Hotkey);
        Assert.Equal("dark", settings.Theme);
    }

    [Fact]
    public void PlaintextSecretIsMigratedToSecretStoreAndNeverRevertedToDisk()
    {
        using var directory = new TempDirectory();
        var settingsPath = Path.Combine(directory.Path, "windows-settings.json");
        File.WriteAllText(settingsPath, "{ \"SchemaVersion\": 8, \"OpenAIApiKey\": \"sk-secret-value\" }");
        var secrets = new InMemorySecretStore();
        var store = new SettingsStore(settingsPath, secrets);

        var settings = store.Load();

        Assert.Equal("sk-secret-value", settings.ResolvedOpenAIApiKey);
        Assert.Equal("sk-secret-value", secrets.Read(SettingsStore.OpenAISecretKey));
        Assert.DoesNotContain("sk-secret-value", File.ReadAllText(settingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void NullDictionaryCollectionWithDuplicateEntriesIsSafe()
    {
        var suggestions = new List<DictionarySuggestion>
        {
            new("Observed", null!),
            new(null!, "Replacement"),
            new("Kubernetes", "Kubernetes"),
            new("Kubernetes", "Kubernetes"),
        };
        var normalized = SettingsStore.NormalizeAfterDeserialization(new MuesliSettings
        {
            DictionarySuggestions = suggestions,
        });

        Assert.NotNull(normalized.DictionarySuggestions);
        Assert.All(normalized.DictionarySuggestions, item =>
        {
            Assert.NotNull(item.Observed);
            Assert.NotNull(item.Replacement);
        });
        Assert.Equal(3, normalized.DictionarySuggestions.Count); // the entry without Observed is dropped
    }
}
