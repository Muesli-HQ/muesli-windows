using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Muesli.Windows.Services;
using Xunit;

namespace Muesli.Windows.Tests;

/// <summary>
/// Truthful per-model language support: only backends the Windows sherpa-onnx runtime actually
/// configures offer a choice; everything else shows its single real behaviour.
/// </summary>
public sealed class ModelLanguageSupportTests
{
    private static TranscriptionModelDefinition Model(string id) => TranscriptionModelCatalog.GetRequired(id);

    [Fact]
    public void Whisper_multilingual_offers_automatic_plus_supported_languages()
    {
        var model = Model("whisper-tiny-multilingual");
        var options = ModelLanguageSupport.OptionsFor(model);

        Assert.True(options.Count > 1);
        Assert.Equal(ModelLanguageSupport.AutomaticCode, options[0].Code);
        Assert.Contains(options, option => option.Code == "en");
        Assert.True(ModelLanguageSupport.IsSelectable(model));
    }

    [Fact]
    public void English_only_models_offer_only_default_english_and_are_not_selectable()
    {
        foreach (var id in new[] { "whisper-tiny-en", "parakeet-unified-en-int8", "parakeet-v2-en-int8" })
        {
            var model = Model(id);
            var options = ModelLanguageSupport.OptionsFor(model);
            Assert.Single(options);
            Assert.Equal("en", options[0].Code);
            Assert.Equal("Default English", options[0].Label);
            Assert.False(ModelLanguageSupport.IsSelectable(model));
        }
    }

    [Fact]
    public void Parakeet_multilingual_and_sensevoice_report_automatic_only()
    {
        var parakeet = Model("parakeet-v3");
        Assert.False(ModelLanguageSupport.IsSelectable(parakeet));
        Assert.Equal(ModelLanguageSupport.AutomaticCode, ModelLanguageSupport.DefaultCodeFor(parakeet));

        var senseVoice = TranscriptionModelCatalog.Models.First(m => m.Kind == NativeAsrModelKind.SenseVoice);
        Assert.False(ModelLanguageSupport.IsSelectable(senseVoice));
        Assert.Equal(ModelLanguageSupport.AutomaticCode, ModelLanguageSupport.DefaultCodeFor(senseVoice));
    }

    [Fact]
    public void Cohere_offers_its_real_language_set()
    {
        var cohere = TranscriptionModelCatalog.Models.FirstOrDefault(m => m.Kind == NativeAsrModelKind.CohereTranscribe);
        if (cohere is null) return; // Catalog without Cohere on this build.
        var options = ModelLanguageSupport.OptionsFor(cohere);
        Assert.True(options.Count > 1);
        Assert.Equal("en", ModelLanguageSupport.DefaultCodeFor(cohere));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("DE")]
    [InlineData("fr")]
    public void Valid_choices_normalize_to_their_canonical_code(string value)
    {
        var model = Model("whisper-tiny-multilingual");
        Assert.Equal(value.ToLowerInvariant(), ModelLanguageSupport.Normalize(model, value));
    }

    [Fact]
    public void Invalid_or_obsolete_choices_fall_back_to_the_model_default()
    {
        Assert.Equal("auto", ModelLanguageSupport.Normalize(Model("whisper-tiny-multilingual"), "klingon"));
        Assert.Equal("auto", ModelLanguageSupport.Normalize(Model("whisper-tiny-multilingual"), ""));
        Assert.Equal("en", ModelLanguageSupport.Normalize(Model("whisper-tiny-en"), "fr"));
    }

    [Fact]
    public void Persisted_selection_is_normalized_and_unknown_models_are_dropped()
    {
        var settings = new MuesliSettings
        {
            ModelLanguages = new Dictionary<string, string>
            {
                ["whisper-tiny-multilingual"] = "de",
                ["whisper-tiny-en"] = "fr",        // invalid -> default en
                ["does-not-exist"] = "en"          // unknown -> dropped
            }
        };

        var normalized = SettingsStore.NormalizeAfterDeserialization(settings);
        Assert.Equal("de", normalized.ModelLanguages["whisper-tiny-multilingual"]);
        Assert.Equal("en", normalized.ModelLanguages["whisper-tiny-en"]);
        Assert.DoesNotContain("does-not-exist", normalized.ModelLanguages.Keys);
    }

    [Fact]
    public void Runtime_resolution_prefers_the_saved_choice_over_the_catalog_value()
    {
        var model = Model("whisper-tiny-multilingual");
        try
        {
            TranscriptionLanguageSelection.Reset();
            Assert.Equal(model.Language, TranscriptionLanguageSelection.Resolve(model));

            TranscriptionLanguageSelection.Apply(new Dictionary<string, string> { [model.Id] = "de" });
            Assert.Equal("de", TranscriptionLanguageSelection.Resolve(model));
        }
        finally
        {
            TranscriptionLanguageSelection.Reset();
        }
    }

    [Fact]
    public void Changing_the_selection_raises_once_and_an_identical_apply_is_a_no_op()
    {
        var model = Model("whisper-tiny-multilingual");
        var raised = 0;
        void Handler() => raised++;

        TranscriptionLanguageSelection.Changed += Handler;
        try
        {
            var selection = new Dictionary<string, string> { [model.Id] = "de" };
            TranscriptionLanguageSelection.Apply(selection);
            Assert.Equal(1, raised);

            // Applying the same mapping again must not churn recognizers.
            TranscriptionLanguageSelection.Apply(new Dictionary<string, string> { [model.Id] = "DE" });
            Assert.Equal(1, raised);

            TranscriptionLanguageSelection.Apply(new Dictionary<string, string> { [model.Id] = "fr" });
            Assert.Equal(2, raised);
        }
        finally
        {
            TranscriptionLanguageSelection.Changed -= Handler;
            TranscriptionLanguageSelection.Reset();
        }
    }

    [Fact]
    public void Native_client_and_offline_config_consume_the_live_selection()
    {
        var root = TestRepositoryLayout.Root;
        var offline = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows.Core",
            "Services", "NativeOfflineAsrClient.cs"));
        var client = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows.Core",
            "Services", "NativeTranscriptionClient.cs"));

        Assert.Contains("TranscriptionLanguageSelection.Resolve(_model)", offline, StringComparison.Ordinal);
        Assert.Contains("_reconfigurePending", client, StringComparison.Ordinal);
        Assert.Contains("TranscriptionLanguageSelection.Changed +=", client, StringComparison.Ordinal);
    }
}
