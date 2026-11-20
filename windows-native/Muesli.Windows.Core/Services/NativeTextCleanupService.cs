using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace Muesli.Windows.Services;

public sealed class NativeTextCleanupService : IDisposable
{
    private const string CleanupInstruction =
        "Clean this transcript without summarizing. Preserve all timestamps and speaker labels exactly. Do not add facts. Do not remove meaningful content. Return only the cleaned transcript.";

    private static readonly Regex SpeakerLineRegex = new(
        @"^(?<prefix>\[\d{2}:\d{2}:\d{2}\]\s+[^:\r\n]+:\s*)(?<body>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex BracketSpeakerLineRegex = new(
        @"^(?<prefix>\[[^\]\r\n]+\]\s*)(?<body>.*)$",
        RegexOptions.Compiled);

    private readonly AppLogService _logService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LLamaWeights? _model;
    private ModelParams? _modelParams;
    private string? _loadedModelPath;

    public NativeTextCleanupService(AppLogService? logService = null)
    {
        _logService = logService ?? new AppLogService();
    }

    public static string ModelCacheDirectory => CleanupModelCatalog.ModelCacheDirectory;

    /// <summary>
    /// The cleanup model the user selected. Set once at startup from settings; an empty value keeps
    /// the legacy behaviour of using any GGUF already placed in the cache.
    /// </summary>
    public static string SelectedModelId { get; private set; } = "";

    public static void Configure(string? selectedModelId) =>
        SelectedModelId = CleanupModelCatalog.Normalize(selectedModelId);

    public static bool IsRuntimeAvailable => RuntimeAvailable();

    public static bool IsModelCached => FindModelPath() is not null;

    public static string Status(bool enabled)
    {
        if (!enabled)
        {
            return "Disabled";
        }

        if (!RuntimeAvailable())
        {
            return "Runtime unavailable";
        }

        var modelPath = FindModelPath();
        if (modelPath is null)
        {
            return "Needs model";
        }

        return CanOpenModelVocabularyCached(modelPath) ? "Ready" : "Runtime unavailable";
    }

    // macOS uses cached availability while preparing a recording. Opening the vocabulary reads the GGUF, so the
    // answer is remembered per file (path, size, write time) instead of being recomputed on every dictation.
    private static (string Key, bool Ok)? _vocabularyProbe;

    private static bool CanOpenModelVocabularyCached(string modelPath)
    {
        string key;
        try
        {
            var info = new FileInfo(modelPath);
            key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
        if (_vocabularyProbe is { } cached && cached.Key == key) return cached.Ok;
        var ok = CanOpenModelVocabulary(modelPath);
        _vocabularyProbe = (key, ok);
        return ok;
    }

    public Task<ModelOperationResult> EnsureModelAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(ModelCacheDirectory);

        if (!IsRuntimeAvailable)
        {
            return Task.FromResult(new ModelOperationResult(
                "Qwen cleanup native runtime is unavailable.",
                ModelCacheDirectory));
        }

        var modelPath = FindModelPath();
        if (modelPath is null)
        {
            return Task.FromResult(new ModelOperationResult(
                "Qwen cleanup needs a compatible GGUF model in the native-cleanup cache.",
                ModelCacheDirectory));
        }

        return Task.FromResult(CanOpenModelVocabulary(modelPath)
            ? new ModelOperationResult("Qwen cleanup model is ready.", modelPath)
            : new ModelOperationResult("Qwen cleanup model could not be opened by the native runtime.", modelPath));
    }

    public static long ModelCacheSizeBytes()
    {
        if (!Directory.Exists(ModelCacheDirectory))
        {
            return 0;
        }

        return Directory.EnumerateFiles(ModelCacheDirectory, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists)
            .Sum(file => file.Length);
    }

    public static string? FindModelPath()
    {
        return CleanupModelCatalog.ResolveInstalledModelPath(SelectedModelId);
    }

    public static void OpenModelCacheDirectory()
    {
        Directory.CreateDirectory(ModelCacheDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = ModelCacheDirectory,
            UseShellExecute = true
        });
    }

    public static void ClearModelCache()
    {
        if (!Directory.Exists(ModelCacheDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(ModelCacheDirectory, "*", SearchOption.AllDirectories))
        {
            File.Delete(file);
        }
    }

    public async Task<string> CleanupAsync(string rawText, bool enabled, CancellationToken cancellationToken = default)
    {
        if (!enabled || string.IsNullOrWhiteSpace(rawText))
        {
            return rawText;
        }

        var status = Status(enabled);
        if (!status.Equals("Ready", StringComparison.OrdinalIgnoreCase))
        {
            _logService.Info($"Native Qwen cleanup skipped: {status}.");
            return rawText;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var modelPath = FindModelPath();
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                _logService.Info("Native Qwen cleanup skipped: no GGUF model in native-cleanup cache.");
                return rawText;
            }

            var result = HasPreservedPrefixes(rawText)
                ? await CleanupPrefixedTranscriptAsync(rawText, modelPath, cancellationToken)
                : await CleanupPlainTextAsync(rawText, modelPath, cancellationToken);

            if (HasPreservedPrefixes(rawText) && !HasSameSpeakerPrefixes(rawText, result))
            {
                _logService.Info("Cleanup rejected because transcript structure changed.");
                return rawText;
            }

            return IsSafeCleanup(rawText, result) ? result : rawText;
        }
        catch (Exception exception)
        {
            _logService.Error("Native Qwen cleanup failed; returning original transcript.", exception);
            return rawText;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static bool LooksLikeSpeakerTranscript(string text)
    {
        return HasPreservedPrefixes(text);
    }

    internal static bool HasPreservedPrefixes(string text)
    {
        return text.SplitLinesPreservingBlank()
            .Any(line => ExtractPreservedPrefix(line) is not null);
    }

    internal static string ReattachSpeakerPrefix(string originalLine, string cleanedBody)
    {
        var prefix = ExtractPreservedPrefix(originalLine);
        if (prefix is null)
        {
            return cleanedBody;
        }

        return prefix.Value.Prefix + cleanedBody.Trim();
    }

    internal static bool HasSameSpeakerPrefixes(string original, string cleaned)
    {
        var originalLines = original.SplitLinesPreservingBlank().ToList();
        var cleanedLines = cleaned.SplitLinesPreservingBlank().ToList();
        if (originalLines.Count != cleanedLines.Count)
        {
            return false;
        }

        for (var index = 0; index < originalLines.Count; index++)
        {
            var originalPrefix = ExtractPreservedPrefix(originalLines[index]);
            if (originalPrefix is null)
            {
                continue;
            }

            var cleanedPrefix = ExtractPreservedPrefix(cleanedLines[index]);
            if (cleanedPrefix is null || !string.Equals(
                    originalPrefix.Value.Prefix,
                    cleanedPrefix.Value.Prefix,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<string> CleanupPrefixedTranscriptAsync(string rawText, string modelPath, CancellationToken cancellationToken)
    {
        var lines = rawText.SplitLinesPreservingBlank().ToList();
        var cleaned = new List<string>(lines.Count);

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prefix = ExtractPreservedPrefix(line);
            if (prefix is null || string.IsNullOrWhiteSpace(prefix.Value.Body))
            {
                cleaned.Add(line);
                continue;
            }

            var cleanedBody = await RunCleanupPromptAsync(prefix.Value.Body, modelPath, preserveStructure: false, cancellationToken);
            cleaned.Add(ReattachSpeakerPrefix(line, cleanedBody));
        }

        var result = string.Join(Environment.NewLine, cleaned);
        return result;
    }

    private async Task<string> CleanupPlainTextAsync(string rawText, string modelPath, CancellationToken cancellationToken)
    {
        var blocks = rawText.SplitParagraphsPreservingBlank();
        var cleaned = new List<string>(blocks.Count);
        foreach (var block in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cleaned.Add(string.IsNullOrWhiteSpace(block)
                ? block
                : await RunCleanupPromptAsync(block, modelPath, preserveStructure: true, cancellationToken));
        }

        return string.Join(Environment.NewLine + Environment.NewLine, cleaned);
    }

    private async Task<string> RunCleanupPromptAsync(string text, string modelPath, bool preserveStructure, CancellationToken cancellationToken)
    {
        EnsureModelLoaded(modelPath);
        if (_model is null || _modelParams is null)
        {
            return text;
        }

        using var context = _model.CreateContext(_modelParams);
        var executor = new InteractiveExecutor(context);
        var contract = PromptContractFor(modelPath);
        var trained = contract is CleanupPromptContract.TrainedV12Qwen or CleanupPromptContract.TrainedV12Lfm;
        var prompt = trained
            ? MuesliCleanupV2Prompt.GgufRequest(text, contract, addsBos: _model.Vocab.ShouldAddBOS)
            : BuildPrompt(text, preserveStructure, contract);
        var maxTokens = trained ? 1024 : Math.Min(1024, Math.Max(96, text.Length / 2 + 64));
        var inferenceParams = new InferenceParams
        {
            MaxTokens = maxTokens,
            AntiPrompts = trained ? [MuesliCleanupV2Prompt.EndOfTurn] : ["<|end|>", "RAW TRANSCRIPT:", "Instructions:"],
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.1f,
                TopP = 0.9f
            }
        };

        var builder = new StringBuilder();
        var generated = 0;
        await foreach (var token in executor.InferAsync(prompt, inferenceParams, cancellationToken))
        {
            builder.Append(token);
            generated++;
        }

        if (!trained) return ExtractCleanedText(builder.ToString());
        // A turn that ends without its end-of-turn marker ran out of budget: keep the dictation, never its prefix.
        var raw = builder.ToString();
        var completed = raw.Contains(MuesliCleanupV2Prompt.EndOfTurn, StringComparison.Ordinal) || generated < maxTokens;
        var guarded = MuesliCleanupV2Prompt.Guard(raw, text, completed);
        // Privacy: timings and outcomes only, never prompt or output content.
        if (guarded.DidFallback) _logService.Info($"Cleanup output rejected; kept the transcript. tokens={generated} completed={completed}");
        return guarded.Text;
    }

    /// <summary>
    /// macOS Qwen3PostProcessor.generate for Quill: one ChatML turn on a general Qwen model with thinking suppressed and
    /// a 4,096-token context (Qwen3PostProcessorConfig.quilMaxContextTokens). Local only; nothing leaves the PC.
    /// </summary>
    internal static string QuillPrompt(string systemPrompt, string userPrompt) =>
        $"<|im_start|>system\n{systemPrompt}<|im_end|>\n<|im_start|>user\n{userPrompt}<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n";

    public async Task<string> GenerateAsync(string modelPath, string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureModelLoaded(modelPath);
            if (_model is null || _modelParams is null) throw new InvalidOperationException("The local model could not be loaded.");
            using var context = _model.CreateContext(_modelParams);
            var executor = new InteractiveExecutor(context);
            var prompt = QuillPrompt(systemPrompt, userPrompt);
            var inferenceParams = new InferenceParams
            {
                MaxTokens = 2048,
                AntiPrompts = [MuesliCleanupV2Prompt.EndOfTurn],
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.2f, TopP = 0.9f }
            };
            var builder = new StringBuilder();
            await foreach (var token in executor.InferAsync(prompt, inferenceParams, cancellationToken)) builder.Append(token);
            return builder.ToString().Replace(MuesliCleanupV2Prompt.EndOfTurn, "").Trim();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Loads the selected model off the UI thread without blocking dictation. Failures are tolerated: cleanup
    /// still fails safe to the raw transcript when it runs. Skipped when the gate is busy (already loading or cleaning).
    /// </summary>
    public void WarmInBackground() => _ = Task.Run(async () =>
    {
        try { if (!await _gate.WaitAsync(0)) return; }
        catch (ObjectDisposedException) { return; }
        try
        {
            if (FindModelPath() is { } path && RuntimeAvailable() && CanOpenModelVocabularyCached(path)) EnsureModelLoaded(path);
        }
        catch (Exception exception)
        {
            _logService.Info($"Cleanup warm-up skipped: {exception.GetType().Name}.");
        }
        finally { try { _gate.Release(); } catch (ObjectDisposedException) { } }
    });

    private void EnsureModelLoaded(string modelPath)
    {
        if (_model is not null && string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _model?.Dispose();
        _modelParams = new ModelParams(modelPath)
        {
            ContextSize = 4096,
            GpuLayerCount = 0,
            Threads = Math.Max(1, Math.Min(Environment.ProcessorCount, 8))
        };
        _model = LLamaWeights.LoadFromFile(_modelParams);
        _loadedModelPath = modelPath;
    }

    /// <summary>
    /// S1-mini was trained on one fixed control line rather than free-form instructions, so Muesli
    /// switches prompt shape for it exactly as the macOS cleanup stage does.
    /// </summary>
    internal static CleanupPromptContract PromptContractFor(string modelPath) =>
        CleanupModelCatalog.DefinitionForPath(modelPath)?.Contract ?? CleanupPromptContract.Configurable;

    internal static string BuildPrompt(string text, bool preserveStructure, CleanupPromptContract contract)
    {
        if (contract == CleanupPromptContract.S1Mini)
        {
            return $"""
                    [Styling: semi-formal] [Structure: prose] [Context: general]

                    {text}

                    CLEANED TEXT:
                    """;
        }

        var structureRule = preserveStructure
            ? "Preserve paragraph breaks and line breaks."
            : "Return only the cleaned utterance body.";

        return $"""
                Instructions:
                {CleanupInstruction}
                - Remove filler words and obvious ASR disfluencies.
                - Fix punctuation and casing.
                - Keep meaning unchanged.
                - Preserve speaker names and timestamps exactly if present.
                - Do not summarize.
                - Do not add facts.
                - {structureRule}
                - Return only the cleaned text, with no explanation.

                RAW TRANSCRIPT:
                {text}

                CLEANED TEXT:
                """;
    }

    private static string ExtractCleanedText(string text)
    {
        var cleaned = text.Trim();
        var markerIndex = cleaned.LastIndexOf("CLEANED TEXT:", StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            cleaned = cleaned[(markerIndex + "CLEANED TEXT:".Length)..].Trim();
        }

        return cleaned.Trim('"', '\'', ' ', '\r', '\n', '\t');
    }

    private static bool IsSafeCleanup(string original, string cleaned)
    {
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return false;
        }

        if (LooksLikeSpeakerTranscript(original) && !HasSameSpeakerPrefixes(original, cleaned))
        {
            return false;
        }

        var originalWords = CountWords(original);
        var cleanedWords = CountWords(cleaned);
        if (cleanedWords <= 0 || cleanedWords > Math.Max(originalWords + 12, (int)Math.Ceiling(originalWords * 1.6)))
        {
            return false;
        }

        return originalWords < 8 || cleanedWords >= Math.Max(1, (int)Math.Floor(originalWords * 0.55));
    }

    private static int CountWords(string text)
    {
        return Regex.Matches(text, @"\b[\p{L}\p{N}']+\b").Count;
    }

    private static bool RuntimeAvailable()
    {
        try
        {
            _ = typeof(LLamaWeights).Assembly.FullName;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static PreservedPrefix? ExtractPreservedPrefix(string line)
    {
        var speakerMatch = SpeakerLineRegex.Match(line);
        if (speakerMatch.Success)
        {
            return new PreservedPrefix(speakerMatch.Groups["prefix"].Value, speakerMatch.Groups["body"].Value);
        }

        var bracketMatch = BracketSpeakerLineRegex.Match(line);
        if (bracketMatch.Success)
        {
            return new PreservedPrefix(bracketMatch.Groups["prefix"].Value, bracketMatch.Groups["body"].Value);
        }

        return null;
    }

    private readonly record struct PreservedPrefix(string Prefix, string Body);

    private static bool CanOpenModelVocabulary(string modelPath)
    {
        try
        {
            var parameters = new ModelParams(modelPath)
            {
                VocabOnly = true,
                GpuLayerCount = 0,
                ContextSize = 512,
                Threads = 1
            };
            using var model = LLamaWeights.LoadFromFile(parameters);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _model?.Dispose();
        _gate.Dispose();
    }
}

internal static class TextCleanupStringExtensions
{
    public static IEnumerable<string> SplitLinesPreservingBlank(this string text)
    {
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    public static List<string> SplitParagraphsPreservingBlank(this string text)
    {
        return Regex.Split(text.Replace("\r\n", "\n").Replace('\r', '\n'), @"\n{2,}")
            .ToList();
    }
}
