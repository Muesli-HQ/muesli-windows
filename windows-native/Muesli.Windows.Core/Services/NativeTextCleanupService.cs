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

    public static string ModelCacheDirectory =>
        Environment.GetEnvironmentVariable("MUESLI_NATIVE_CLEANUP_CACHE") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "muesli", "native-cleanup");

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

        return CanOpenModelVocabulary(modelPath) ? "Ready" : "Runtime unavailable";
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
        if (!Directory.Exists(ModelCacheDirectory))
        {
            return null;
        }

        return Directory.EnumerateFiles(ModelCacheDirectory, "*.gguf", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
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
        var prompt = BuildPrompt(text, preserveStructure);
        var inferenceParams = new InferenceParams
        {
            MaxTokens = Math.Min(1024, Math.Max(96, text.Length / 2 + 64)),
            AntiPrompts = ["<|end|>", "RAW TRANSCRIPT:", "Instructions:"],
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.1f,
                TopP = 0.9f
            }
        };

        var builder = new StringBuilder();
        await foreach (var token in executor.InferAsync(prompt, inferenceParams, cancellationToken))
        {
            builder.Append(token);
        }

        return ExtractCleanedText(builder.ToString());
    }

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

    private static string BuildPrompt(string text, bool preserveStructure)
    {
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
