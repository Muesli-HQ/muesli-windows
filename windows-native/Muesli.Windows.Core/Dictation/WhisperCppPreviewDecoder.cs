using System.Security.Cryptography;
using System.Text;
using Whisper.net;

namespace Muesli.Windows.Services;

/// <summary>
/// macOS WhisperKitTranscriber.transcribePreview and previewWindow (WhisperCppBackend.swift, 2f10b23d) on
/// whisper.cpp. Sherpa Whisper reports no word timings, so live Whisper previews decode here with DTW token
/// timestamps and token probabilities. Normal dictation, files and the model picker stay on sherpa.
/// </summary>
public static class WhisperCppPreviewDecoder
{
    private const string FileName = "ggml-large-v3-turbo-q5_0.bin";
    private const string Url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + FileName;
    private const string Sha256 = "394221709CD5AD1F40C46E6031CA61BCE88931E6E088C188294C6D5A55FFA7E2";
    public const string SizeLabel = "574 MB";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object DownloadLock = new();
    private static Task? s_download;
    // ponytail: loaded once and kept (~0.8 GB VRAM) so every preview skips a 2.5 s load; release it on
    // model switch if GPU memory becomes a problem.
    private static WhisperFactory? s_factory;

    public static string ModelPath => Path.Combine(TranscriptionModelCatalog.ModelCacheDirectory, "whisper-cpp", FileName);
    public static bool Supports(TranscriptionModelDefinition model) => model.Id == "whisper-large-turbo-multilingual";
    /// <summary>The file is moved into place only after its SHA-256 matched.</summary>
    public static bool IsReady => File.Exists(ModelPath);

    /// <summary>True when a new background download began; until it finishes live Whisper uses untimed chunks.</summary>
    public static bool StartDownload(Action<string> log)
    {
        lock (DownloadLock)
        {
            if (IsReady || s_download is { IsCompleted: false }) return false;
            s_download = Task.Run(async () =>
            {
                try
                {
                    await DownloadAsync().ConfigureAwait(false);
                    log("whisper_cpp_model_ready");
                }
                catch (Exception exception)
                {
                    log($"whisper_cpp_model_download_failed:{exception.GetType().Name}");
                }
            });
            return true;
        }
    }

    private static async Task DownloadAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        var partial = ModelPath + ".partial";
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromHours(2) })
            using (var response = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
                await input.CopyToAsync(output).ConfigureAwait(false);
            }
            await using (var stream = File.OpenRead(partial))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
                if (!actual.Equals(Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"whisper.cpp preview model verification failed; found {actual}.");
            }
            File.Move(partial, ModelPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    /// <summary>
    /// macOS WhisperKitTranscriber.warmup: load the model and compile GPU shaders (tens of seconds on a first
    /// run) before the first live dictation, so its previews keep up and its stop does not time out.
    /// </summary>
    public static void WarmUp(Action<string> log)
    {
        if (!IsReady)
        {
            StartDownload(log);
            return;
        }
        _ = Task.Run(async () =>
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await DecodeAsync(new float[16_000], ModelLanguageSupport.AutomaticCode, CancellationToken.None).ConfigureAwait(false);
            }
            catch (BufferedTranscriptionPreviewDeferral) { }
            catch (Exception exception)
            {
                log($"whisper_cpp_warmup_failed:{exception.GetType().Name}");
                return;
            }
            log($"whisper_cpp_warmup ms={started.ElapsedMilliseconds}");
        });
    }

    public static async Task<BufferedTranscriptionWindow> DecodeAsync(float[] samples, string language, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            s_factory ??= WhisperFactory.FromPath(ModelPath, new WhisperFactoryOptions
            {
                UseGpu = true,
                UseDtwTimeStamps = true,
                HeadsPreset = WhisperAlignmentHeadsPreset.LargeV3Turbo
            });
            // macOS makePreviewDecodeOptions: a preview gets another hypothesis as audio arrives, so no
            // temperature fallback.
            await using var processor = s_factory.CreateBuilder()
                .WithLanguage(string.IsNullOrEmpty(language) ? ModelLanguageSupport.AutomaticCode : language)
                .WithTokenTimestamps().WithProbabilities().WithNoContext().WithSingleSegment()
                .WithTemperature(0).WithTemperatureInc(0)
                .Build();
            var text = new StringBuilder();
            var tokens = new List<(string Text, double Time, float Probability)>();
            await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
            {
                text.Append(segment.Text);
                foreach (var token in segment.Tokens ?? [])
                {
                    // Special, timestamp and language tokens are bracketed by whisper.cpp.
                    if (string.IsNullOrEmpty(token.Text) || token.Text.StartsWith("[_", StringComparison.Ordinal) ||
                        token.Text.StartsWith("<|", StringComparison.Ordinal)) continue;
                    tokens.Add((token.Text, token.DtwTimestamp / 100.0, token.Probability));
                }
            }
            return Window(text.ToString().Trim(), tokens, samples.Length / 16_000.0);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// BPE tokens with a leading space start a word. DTW gives one time per token, so a word spans from its
    /// first token to the next word's first token; confidence is its mean token probability. Validation is
    /// macOS previewWindow: deferrals keep the PCM pending for a later hypothesis.
    /// </summary>
    internal static BufferedTranscriptionWindow Window(string text, IReadOnlyList<(string Text, double Time, float Probability)> tokens, double duration)
    {
        var groups = new List<(StringBuilder Text, double Start, double Last, float Sum, int Count)>();
        foreach (var token in tokens)
        {
            if (groups.Count == 0 || token.Text[0] == ' ')
            {
                groups.Add((new StringBuilder(token.Text), token.Time, token.Time, token.Probability, 1));
                continue;
            }
            var current = groups[^1];
            current.Text.Append(token.Text);
            groups[^1] = (current.Text, current.Start, token.Time, current.Sum + token.Probability, current.Count + 1);
        }
        var words = new List<BufferedTranscriptionWord>();
        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            var spelling = group.Text.ToString().Trim();
            if (spelling.Length == 0) continue;
            var end = index + 1 < groups.Count ? groups[index + 1].Start : group.Last;
            words.Add(new BufferedTranscriptionWord(spelling, group.Start, Math.Max(end, group.Start), group.Sum / group.Count));
        }

        if (text.Length > 0 && words.Count == 0)
            throw new BufferedTranscriptionPreviewDeferral(BufferedTranscriptionPreviewDeferral.MissingWordTimings);
        var previousStart = double.NegativeInfinity;
        foreach (var word in words)
        {
            if (!double.IsFinite(word.Start) || !double.IsFinite(word.End) || word.Start < 0 || word.Start < previousStart ||
                word.End < word.Start || word.End > duration + 0.25)
                throw new BufferedTranscriptionPreviewDeferral(BufferedTranscriptionPreviewDeferral.InvalidWordTimings);
            previousStart = word.Start;
        }
        var aligned = BufferedPreviewWindows.Window(text, words);
        // macOS: three or more lexical words aligned to one zero-duration point poison a stable prefix.
        double? collapsedTime = null;
        var collapsedCount = 0;
        foreach (var word in aligned.Words)
        {
            if (!word.Text.Any(char.IsLetterOrDigit)) continue;
            if (word.Start != word.End)
            {
                collapsedTime = null;
                collapsedCount = 0;
                continue;
            }
            collapsedCount = collapsedTime == word.Start ? collapsedCount + 1 : 1;
            collapsedTime = word.Start;
            if (collapsedCount >= 3)
                throw new BufferedTranscriptionPreviewDeferral(BufferedTranscriptionPreviewDeferral.CollapsedWordTimings);
        }
        return aligned;
    }
}
