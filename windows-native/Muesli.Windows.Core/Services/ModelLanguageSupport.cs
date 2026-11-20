namespace Muesli.Windows.Services;

/// <summary>A language the runtime can actually be asked to transcribe.</summary>
public sealed record ModelLanguageOption(string Code, string Label);

/// <summary>
/// The truthful language choices per Windows model. Only backends whose sherpa-onnx runtime consumes
/// a language are offered more than one option; everything else shows its single real behaviour
/// (English-only, or automatic multilingual) instead of inventing manual choices.
/// </summary>
public static class ModelLanguageSupport
{
    public const string AutomaticCode = "auto";

    private static readonly IReadOnlyList<ModelLanguageOption> EnglishOnly =
    [
        new("en", "Default English")
    ];

    private static readonly IReadOnlyList<ModelLanguageOption> AutomaticMultilingual =
    [
        new(AutomaticCode, "Automatic (multilingual)")
    ];

    // Whisper multilingual (sherpa-onnx consumes Whisper.Language): all 100 language tokens, as macOS
    // WhisperKitLanguage (4f97a3d1, 09c3203b, ede5701f). Cantonese exists only in large-v3 vocabularies.
    private static readonly IReadOnlyList<ModelLanguageOption> WhisperMultilingual =
    [
        new(AutomaticCode, "Automatic"),
        new("af", "Afrikaans"),
        new("sq", "Albanian"),
        new("am", "Amharic"),
        new("ar", "Arabic"),
        new("hy", "Armenian"),
        new("as", "Assamese"),
        new("az", "Azerbaijani"),
        new("ba", "Bashkir"),
        new("eu", "Basque"),
        new("be", "Belarusian"),
        new("bn", "Bengali"),
        new("bs", "Bosnian"),
        new("br", "Breton"),
        new("bg", "Bulgarian"),
        new("my", "Burmese"),
        new("yue", "Cantonese"),
        new("ca", "Catalan"),
        new("zh", "Chinese"),
        new("hr", "Croatian"),
        new("cs", "Czech"),
        new("da", "Danish"),
        new("nl", "Dutch"),
        new("en", "English"),
        new("et", "Estonian"),
        new("fo", "Faroese"),
        new("fi", "Finnish"),
        new("fr", "French"),
        new("gl", "Galician"),
        new("ka", "Georgian"),
        new("de", "German"),
        new("el", "Greek"),
        new("gu", "Gujarati"),
        new("ht", "Haitian Creole"),
        new("ha", "Hausa"),
        new("haw", "Hawaiian"),
        new("he", "Hebrew"),
        new("hi", "Hindi"),
        new("hu", "Hungarian"),
        new("is", "Icelandic"),
        new("id", "Indonesian"),
        new("it", "Italian"),
        new("ja", "Japanese"),
        new("jw", "Javanese"),
        new("kn", "Kannada"),
        new("kk", "Kazakh"),
        new("km", "Khmer"),
        new("ko", "Korean"),
        new("lo", "Lao"),
        new("la", "Latin"),
        new("lv", "Latvian"),
        new("ln", "Lingala"),
        new("lt", "Lithuanian"),
        new("lb", "Luxembourgish"),
        new("mk", "Macedonian"),
        new("mg", "Malagasy"),
        new("ms", "Malay"),
        new("ml", "Malayalam"),
        new("mt", "Maltese"),
        new("mi", "Maori"),
        new("mr", "Marathi"),
        new("mn", "Mongolian"),
        new("ne", "Nepali"),
        new("no", "Norwegian"),
        new("nn", "Norwegian Nynorsk"),
        new("oc", "Occitan"),
        new("ps", "Pashto"),
        new("fa", "Persian"),
        new("pl", "Polish"),
        new("pt", "Portuguese"),
        new("pa", "Punjabi"),
        new("ro", "Romanian"),
        new("ru", "Russian"),
        new("sa", "Sanskrit"),
        new("sr", "Serbian"),
        new("sn", "Shona"),
        new("sd", "Sindhi"),
        new("si", "Sinhala"),
        new("sk", "Slovak"),
        new("sl", "Slovenian"),
        new("so", "Somali"),
        new("es", "Spanish"),
        new("su", "Sundanese"),
        new("sw", "Swahili"),
        new("sv", "Swedish"),
        new("tl", "Tagalog"),
        new("tg", "Tajik"),
        new("ta", "Tamil"),
        new("tt", "Tatar"),
        new("te", "Telugu"),
        new("th", "Thai"),
        new("bo", "Tibetan"),
        new("tr", "Turkish"),
        new("tk", "Turkmen"),
        new("uk", "Ukrainian"),
        new("ur", "Urdu"),
        new("uz", "Uzbek"),
        new("vi", "Vietnamese"),
        new("cy", "Welsh"),
        new("yi", "Yiddish"),
        new("yo", "Yoruba")
    ];

    // Cohere Transcribe prompt-token languages (sherpa-onnx consumes CohereTranscribe.Language).
    private static readonly IReadOnlyList<ModelLanguageOption> CohereLanguages =
    [
        new("en", "English"),
        new("fr", "French"),
        new("de", "German"),
        new("es", "Spanish"),
        new("it", "Italian"),
        new("pt", "Portuguese"),
        new("nl", "Dutch"),
        new("pl", "Polish"),
        new("el", "Greek"),
        new("ar", "Arabic"),
        new("ja", "Japanese"),
        new("zh", "Chinese"),
        new("vi", "Vietnamese"),
        new("ko", "Korean")
    ];

    /// <summary>The selectable options for a model, in menu order.</summary>
    public static IReadOnlyList<ModelLanguageOption> OptionsFor(TranscriptionModelDefinition model) => model.Kind switch
    {
        NativeAsrModelKind.Whisper => IsEnglishOnly(model) ? EnglishOnly
            : HasCantoneseToken(model) ? WhisperMultilingual
            : WhisperMultilingual.Where(option => option.Code != "yue").ToList(),
        NativeAsrModelKind.CohereTranscribe => CohereLanguages,
        NativeAsrModelKind.Bodhan => BodhanModels.Languages(model),
        NativeAsrModelKind.SenseVoice => AutomaticMultilingual,
        NativeAsrModelKind.Parakeet or NativeAsrModelKind.ParakeetTransducer =>
            IsEnglishOnly(model) ? EnglishOnly : AutomaticMultilingual,
        _ => [new ModelLanguageOption(AutomaticCode, "Automatic")]
    };

    /// <summary>True when more than one language can genuinely be selected for this model.</summary>
    public static bool IsSelectable(TranscriptionModelDefinition model) => OptionsFor(model).Count > 1;

    public static string DefaultCodeFor(TranscriptionModelDefinition model) => OptionsFor(model)[0].Code;

    /// <summary>Coerces a saved/obsolete value to a supported code, falling back to the default.</summary>
    public static string Normalize(TranscriptionModelDefinition model, string? value)
    {
        var options = OptionsFor(model);
        if (!string.IsNullOrWhiteSpace(value))
        {
            var trimmed = value.Trim();
            foreach (var option in options)
            {
                if (string.Equals(option.Code, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return option.Code;
                }
            }
        }

        return DefaultCodeFor(model);
    }

    public static bool IsSupported(TranscriptionModelDefinition model, string? code) =>
        !string.IsNullOrWhiteSpace(code) &&
        OptionsFor(model).Any(option => string.Equals(option.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string LabelFor(TranscriptionModelDefinition model, string? code)
    {
        if (!string.IsNullOrWhiteSpace(code) && !IsSupported(model, code))
            return $"{code.Trim()} (not supported by this model; using {OptionsFor(model)[0].Label})";
        var normalized = Normalize(model, code);
        foreach (var option in OptionsFor(model))
        {
            if (string.Equals(option.Code, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return option.Label;
            }
        }

        return normalized;
    }

    /// <summary>The Cantonese token was added in large-v3; the catalog's only large Whisper is large-v3-turbo.</summary>
    private static bool HasCantoneseToken(TranscriptionModelDefinition model) =>
        model.Id.Contains("large", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whisper `.en` variants and every other English-only model report Language "en".</summary>
    private static bool IsEnglishOnly(TranscriptionModelDefinition model) =>
        string.Equals(model.Language, "en", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The live per-model language selections, resolved from settings and read by the native client when
/// it builds its recognizer configuration. <see cref="Changed"/> lets pooled clients invalidate so the
/// next transcription is reconfigured with the new language rather than reusing the old recognizer.
/// </summary>
public static class TranscriptionLanguageSelection
{
    private static readonly object Gate = new();
    private static IReadOnlyDictionary<string, string> _byModelId =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static event Action? Changed;

    public static void Apply(IReadOnlyDictionary<string, string>? byModelId)
    {
        var next = byModelId is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(byModelId, StringComparer.OrdinalIgnoreCase);

        lock (Gate)
        {
            if (Same(_byModelId, next))
            {
                return;
            }

            _byModelId = next;
        }

        Changed?.Invoke();
    }

    private static bool Same(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var value) ||
                !string.Equals(pair.Value, value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The effective language for a model: the saved choice if valid, else the catalog value.</summary>
    public static string Resolve(TranscriptionModelDefinition model)
    {
        lock (Gate)
        {
            if (_byModelId.TryGetValue(model.Id, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return ModelLanguageSupport.Normalize(model, value);
            }
        }

        return model.Language;
    }

    public static void Reset() => Apply(null);
}
