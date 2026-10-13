using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.ML.OnnxRuntime;

namespace Muesli.Windows.Services;

public enum BodhanOutputMode
{
    Native,
    Mixed,
    Romanized
}

public sealed record BodhanRecognition(string Text, string Language, int Tokens);

/// <summary>
/// Greedy decoding for Bodhan's IndicCanary checkpoints (canary-1b-v2 fine-tunes), ported from
/// the official bodhan-ai PyTorch code. The checkpoint was exported to two graphs:
/// encoder.onnx (16 kHz audio → every decoder layer's cross-attention K/V; the NeMo mel front
/// end runs inside it) and decoder.onnx (tokens + position-major self-attention cache → logits
/// for the last token and the new cache rows).
/// </summary>
public sealed class BodhanRecognizer : IDisposable
{
    public const int SampleRate = 16000;
    private const int Layers = 24;
    private const int CacheRow = Layers * 8 * 128;
    private const int MaxPositions = 1024;
    private const int LanguageSlot = 3;
    private const int ItnSlot = 6;
    private const int RomanizedSlot = 7;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly BodhanVocabulary _vocabulary;
    private readonly RunOptions _run = new();

    public BodhanRecognizer(string modelDirectory, string precision, SessionOptions options)
    {
        _vocabulary = BodhanVocabulary.Load(Path.Combine(modelDirectory, "tokenizer.json"));
        _encoder = new InferenceSession(Path.Combine(modelDirectory, $"encoder.{precision}.onnx"), options);
        try
        {
            _decoder = new InferenceSession(Path.Combine(modelDirectory, $"decoder.{precision}.onnx"), options);
        }
        catch
        {
            _encoder.Dispose();
            throw;
        }
    }

    public IReadOnlyCollection<string> Languages => _vocabulary.Prompts.Keys;

    /// <summary>
    /// Transcribes up to ~30 s of 16 kHz mono audio. A null language is identified from the
    /// audio first, choosing only among <paramref name="detectable"/>.
    /// </summary>
    public BodhanRecognition Recognize(
        float[] samples,
        string? language,
        BodhanOutputMode mode,
        int maxTokens,
        IReadOnlyCollection<string> detectable)
    {
        if (language is not null && !_vocabulary.Prompts.ContainsKey(language))
        {
            throw new ArgumentException($"Bodhan does not support the language '{language}'.", nameof(language));
        }

        var audio = PadToOneSecond(samples);
        using var audioValue = OrtValue.CreateTensorValueFromMemory(audio, [1, audio.Length]);
        using var encoded = _encoder.Run(_run, ["audio"], [audioValue], ["cross_k", "cross_v"]);
        language ??= Identify(encoded[0], encoded[1], detectable);

        var prompt = (int[])_vocabulary.Prompts[language].Clone();
        if (mode == BodhanOutputMode.Mixed) prompt[ItnSlot] = _vocabulary.ItnId;
        if (mode == BodhanOutputMode.Romanized) prompt[RomanizedSlot] = _vocabulary.RomanizedId;
        var tokens = Decode(prompt, encoded[0], encoded[1], Math.Min(maxTokens, MaxPositions - prompt.Length));
        return new BodhanRecognition(_vocabulary.Text(tokens), language, tokens.Count);
    }

    /// <summary>Identifies the language only, choosing among <paramref name="detectable"/>.</summary>
    public string Identify(float[] samples, IReadOnlyCollection<string> detectable)
    {
        var audio = PadToOneSecond(samples);
        using var audioValue = OrtValue.CreateTensorValueFromMemory(audio, [1, audio.Length]);
        using var encoded = _encoder.Run(_run, ["audio"], [audioValue], ["cross_k", "cross_v"]);
        return Identify(encoded[0], encoded[1], detectable);
    }

    /// <summary>
    /// lid.py: feed the language-independent prompt head and read which language token the model
    /// expects next. One decoder step; the encoder output is shared with transcription.
    /// </summary>
    private string Identify(OrtValue crossK, OrtValue crossV, IReadOnlyCollection<string> detectable)
    {
        var head = _vocabulary.Prompts.Values.First()[..LanguageSlot];
        using var step = Step(head, [], [], 0, crossK, crossV);
        var logits = step[0].GetTensorDataAsSpan<float>();
        string? best = null;
        foreach (var code in detectable)
        {
            if (_vocabulary.Prompts.ContainsKey(code) && _vocabulary.LanguageTokens.TryGetValue(code, out var token) &&
                (best is null || logits[token] > logits[_vocabulary.LanguageTokens[best]]))
            {
                best = code;
            }
        }

        return best ?? throw new InvalidOperationException("Bodhan has no detectable language for this model.");
    }

    private List<int> Decode(int[] prompt, OrtValue crossK, OrtValue crossV, int limit)
    {
        var pastK = new float[(prompt.Length + limit) * CacheRow];
        var pastV = new float[pastK.Length];
        var tokens = new List<int>();
        var input = prompt;
        var past = 0;
        for (var i = 0; i < limit; i++)
        {
            int next;
            using (var step = Step(input, pastK, pastV, past, crossK, crossV))
            {
                step[1].GetTensorDataAsSpan<float>().CopyTo(pastK.AsSpan(past * CacheRow));
                step[2].GetTensorDataAsSpan<float>().CopyTo(pastV.AsSpan(past * CacheRow));
                next = ArgMax(step[0].GetTensorDataAsSpan<float>());
            }

            past += input.Length;
            if (next == _vocabulary.EosId || next == _vocabulary.PadId) return tokens;
            tokens.Add(next);
            input = [next];
        }

        throw new InvalidOperationException("Bodhan reached its token limit before finishing the transcript.");
    }

    private IDisposableReadOnlyCollection<OrtValue> Step(
        int[] input, float[] pastK, float[] pastV, int past, OrtValue crossK, OrtValue crossV)
    {
        // The cache is position-major, so the first `past` rows are one contiguous prefix.
        var ids = Array.ConvertAll(input, id => (long)id);
        using var idsValue = OrtValue.CreateTensorValueFromMemory(ids, [1, ids.Length]);
        long[] cacheShape = [past, Layers, 8, 128];
        using var kValue = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, pastK.AsMemory(0, past * CacheRow), cacheShape);
        using var vValue = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, pastV.AsMemory(0, past * CacheRow), cacheShape);
        return _decoder.Run(
            _run,
            ["input_ids", "past_k", "past_v", "cross_k", "cross_v"],
            [idsValue, kValue, vValue, crossK, crossV],
            ["logits", "new_k", "new_v"]);
    }

    private static int ArgMax(ReadOnlySpan<float> values)
    {
        var best = 0;
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] > values[best]) best = i;
        }

        return best;
    }

    /// <summary>
    /// Adds the CUDA provider through the V2 options API; the CUDA pack's onnxruntime.dll does not
    /// export the legacy OrtSessionOptionsAppendExecutionProvider_CUDA entry point.
    /// </summary>
    public static void UseCuda(SessionOptions options)
    {
        // ORT 1.24's extended (level 2) fusions corrupt the fp16 encoder on CUDA: its cross-attention
        // keys come out ~95% wrong and decoding loops. Basic optimizations match the CPU output.
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC;
        using var cuda = new OrtCUDAProviderOptions();
        cuda.UpdateOptions(new Dictionary<string, string> { ["device_id"] = "0" });
        options.AppendExecutionProvider_CUDA(cuda);
    }

    /// <summary>indic_transcribe._features: short audio is centred in one second of silence.</summary>
    internal static float[] PadToOneSecond(float[] samples)
    {
        if (samples.Length >= SampleRate) return samples;
        var padded = new float[SampleRate];
        // Python's round() is banker's rounding, as is Math.Round's default.
        samples.CopyTo(padded, (int)Math.Round((SampleRate - samples.Length) / 2.0));
        return padded;
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
        _run.Dispose();
    }
}

/// <summary>tokenizer.json written from the official IndicCanaryTokenizer at export time.</summary>
internal sealed class BodhanVocabulary
{
    [JsonPropertyName("pieces")] public string[] Pieces { get; init; } = [];
    [JsonPropertyName("special_count")] public int SpecialCount { get; init; }
    [JsonPropertyName("eos_id")] public int EosId { get; init; }
    [JsonPropertyName("pad_id")] public int PadId { get; init; }
    [JsonPropertyName("itn_id")] public int ItnId { get; init; }
    [JsonPropertyName("romanized_id")] public int RomanizedId { get; init; }
    [JsonPropertyName("prompts")] public Dictionary<string, int[]> Prompts { get; init; } = [];
    [JsonPropertyName("language_tokens")] public Dictionary<string, int> LanguageTokens { get; init; } = [];

    public static BodhanVocabulary Load(string path)
    {
        var vocabulary = JsonSerializer.Deserialize<BodhanVocabulary>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Bodhan's tokenizer.json is empty.");
        if (vocabulary.Pieces.Length <= vocabulary.SpecialCount || vocabulary.Prompts.Count == 0 ||
            vocabulary.Prompts.Values.Any(prompt => prompt.Length != 10))
        {
            throw new InvalidDataException("Bodhan's tokenizer.json is invalid.");
        }

        return vocabulary;
    }

    /// <summary>NeMo piece-join decode; control tokens are dropped.</summary>
    public string Text(IEnumerable<int> tokens) => string.Concat(tokens
            .Where(token => token >= SpecialCount && token < Pieces.Length)
            .Select(token => Pieces[token]))
        .Replace('▁', ' ')
        .Trim();
}

/// <summary>
/// Catalog facts for the four Bodhan entries (Core/Flex × INT8/FP16), matching macOS
/// BodhanModel, BodhanLanguage and BodhanOutputMode.
/// </summary>
public static class BodhanModels
{
    /// <summary>Hindi, Tamil, ... in macOS BodhanLanguage order; Flex adds Chhattisgarhi and Haryanvi.</summary>
    private static readonly (string Code, string Label)[] LanguageLabels =
    [
        ("en", "English"), ("hi", "Hindi"), ("ta", "Tamil"), ("te", "Telugu"), ("bn", "Bengali"),
        ("mr", "Marathi"), ("ml", "Malayalam"), ("kn", "Kannada"), ("as", "Assamese"), ("brx", "Bodo"),
        ("doi", "Dogri"), ("gu", "Gujarati"), ("ks", "Kashmiri"), ("kok", "Konkani"), ("mai", "Maithili"),
        ("mni", "Manipuri"), ("ne", "Nepali"), ("or", "Odia"), ("pa", "Punjabi"), ("sa", "Sanskrit"),
        ("sat", "Santali"), ("sd", "Sindhi"), ("ur", "Urdu"), ("bho", "Bhojpuri"), ("bhb", "Bhili"),
        ("hne", "Chhattisgarhi"), ("bgc", "Haryanvi")
    ];

    private static volatile string _outputMode = "mixed";

    public static bool IsFlex(TranscriptionModelDefinition model) =>
        model.Id.StartsWith("bodhan-flex", StringComparison.OrdinalIgnoreCase);

    /// <summary>FP16 graphs run on CUDA only; 8-bit graphs run on the CPU only.</summary>
    public static bool NeedsGpu(TranscriptionModelDefinition model) =>
        model.Id.EndsWith("-fp16", StringComparison.OrdinalIgnoreCase);

    /// <summary>Graph file suffix: FP16 for CUDA, weight-only 8-bit (MatMulNBits, 32-wide blocks) for the CPU.</summary>
    public static string Precision(TranscriptionModelDefinition model) => NeedsGpu(model) ? "fp16" : "int8";

    public static IReadOnlyList<ModelLanguageOption> Languages(TranscriptionModelDefinition model) =>
        LanguageLabels
            .Where(language => IsFlex(model) || language.Code is not ("hne" or "bgc"))
            .Select(language => new ModelLanguageOption(language.Code, language.Label))
            .Prepend(new ModelLanguageOption(ModelLanguageSupport.AutomaticCode, "Auto-detect"))
            .ToArray();

    /// <summary>
    /// What auto-detect may choose. Flex skips Haryanvi and Bhili exactly as the official Flex
    /// wrapper does: on Bodhan's benchmarks they only ever absorb other languages' clips.
    /// </summary>
    public static IReadOnlyCollection<string> Detectable(TranscriptionModelDefinition model) =>
        Languages(model)
            .Select(language => language.Code)
            .Where(code => code != ModelLanguageSupport.AutomaticCode && !(IsFlex(model) && code is "bgc" or "bhb"))
            .ToArray();

    /// <summary>macOS bodhan_output_mode: one shared preference, honoured by Flex; Core is always native.</summary>
    public static string OutputModeSetting
    {
        get => _outputMode;
        set => _outputMode = NormalizeOutputMode(value);
    }

    public static string NormalizeOutputMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "native" => "native",
        "romanized" or "romanised" => "romanized",
        _ => "mixed"
    };

    public static BodhanOutputMode OutputModeFor(TranscriptionModelDefinition model) => !IsFlex(model)
        ? BodhanOutputMode.Native
        : _outputMode switch
        {
            "native" => BodhanOutputMode.Native,
            "romanized" => BodhanOutputMode.Romanized,
            _ => BodhanOutputMode.Mixed
        };

    /// <summary>macOS maximumGeneratedTokens: romanized text spends more tokens per word.</summary>
    public static int MaxTokens(BodhanOutputMode mode) => mode == BodhanOutputMode.Romanized ? 512 : 256;

    /// <summary>
    /// Indic Open Model License §2.1 attribution notice, shown wherever the model is offered. It names
    /// the upstream models (as the macOS port's notice does), not Muesli's card labels.
    /// </summary>
    public static string AttributionNotice(TranscriptionModelDefinition model) =>
        $"Built with {(IsFlex(model) ? "Indic-Transcribe-flex" : "Indic-Transcribe-core")} from Bodhan AI / AI4Bharat.";
}
