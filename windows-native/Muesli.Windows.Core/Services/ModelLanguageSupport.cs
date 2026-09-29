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

    // Whisper multilingual (sherpa-onnx consumes Whisper.Language).
    private static readonly IReadOnlyList<ModelLanguageOption> WhisperMultilingual =
    [
        new(AutomaticCode, "Automatic"),
        new("en", "English"),
        new("hi", "Hindi"),
        new("es", "Spanish"),
        new("fr", "French"),
        new("de", "German"),
        new("it", "Italian"),
        new("pt", "Portuguese"),
        new("zh", "Chinese"),
        new("ja", "Japanese"),
        new("ko", "Korean"),
        new("ru", "Russian"),
        new("ar", "Arabic")
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
        NativeAsrModelKind.Whisper => IsEnglishOnly(model) ? EnglishOnly : WhisperMultilingual,
        NativeAsrModelKind.CohereTranscribe => CohereLanguages,
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

    public static string LabelFor(TranscriptionModelDefinition model, string? code)
    {
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
