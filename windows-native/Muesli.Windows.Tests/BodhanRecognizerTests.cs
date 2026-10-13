using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Muesli.Windows.Services;
using NAudio.Wave;

namespace Muesli.Windows.Tests;

public sealed class BodhanRecognizerTests
{
    [Fact]
    public void ShortAudioIsCentredInOneSecondLikeTheReference()
    {
        var padded = BodhanRecognizer.PadToOneSecond(Enumerable.Repeat(1f, 8001).ToArray());
        Assert.Equal(16000, padded.Length);
        // round(7999 / 2) == 4000 under Python's banker's rounding.
        Assert.Equal(0f, padded[3999]);
        Assert.Equal(1f, padded[4000]);
        Assert.Equal(1f, padded[12000]);
        Assert.Equal(0f, padded[12001]);
        var full = new float[16000];
        Assert.Same(full, BodhanRecognizer.PadToOneSecond(full));
    }

    [Fact]
    public void FlexOffersItsExtraLanguagesAndSkipsSinkLanguagesWhenDetecting()
    {
        var core = Model("bodhan-core-int8");
        var flex = Model("bodhan-flex-int8");
        Assert.DoesNotContain(BodhanModels.Languages(core), option => option.Code == "hne");
        Assert.Contains(BodhanModels.Languages(flex), option => option.Code == "hne");
        Assert.Contains("bhb", BodhanModels.Detectable(core));
        Assert.DoesNotContain("bhb", BodhanModels.Detectable(flex));
        Assert.DoesNotContain("bgc", BodhanModels.Detectable(flex));
        Assert.Equal(BodhanOutputMode.Native, BodhanModels.OutputModeFor(core));
        Assert.Equal("int8", BodhanModels.Precision(core));
        Assert.True(BodhanModels.NeedsGpu(Model("bodhan-flex-fp16")));
    }

    /// <summary>
    /// Replays the official PyTorch transcripts saved by tools/bodhan_check.py (ref) through the
    /// exported graphs, exactly as Muesli runs them. MUESLI_BODHAN_CUDA=1 runs on the CUDA pack.
    /// Language identification must match; 8-bit and FP16 weights may move a few characters
    /// (Flex INT8 measured 5 edits in 4,755), so transcripts are held to 0.5% character edits.
    /// </summary>
    [BodhanModelFact]
    public void ExportedGraphsMatchTheOfficialTranscripts()
    {
        var directory = Environment.GetEnvironmentVariable("MUESLI_BODHAN_MODEL_DIR")!;
        var refsPath = Environment.GetEnvironmentVariable("MUESLI_BODHAN_REFS")!;
        var precision = Environment.GetEnvironmentVariable("MUESLI_BODHAN_PRECISION") ?? "int8";
        // Load the native runtime first: it decides which onnxruntime.dll ONNX Runtime binds to.
        var cuda = Environment.GetEnvironmentVariable("MUESLI_BODHAN_CUDA") == "1";
        Assert.True(cuda ? NativeSherpaRuntime.IsCudaCapable : NativeSherpaRuntime.IsAvailable, NativeSherpaRuntime.Diagnostic);
        using var options = new SessionOptions();
        if (cuda) BodhanRecognizer.UseCuda(options);

        using var recognizer = new BodhanRecognizer(directory, precision, options);
        var refs = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(refsPath))!;
        var root = Path.GetDirectoryName(Path.GetFullPath(refsPath))!;
        var failures = new List<string>();
        var mismatches = new List<string>();
        int chars = 0, edits = 0;
        foreach (var (clip, expected) in refs)
        {
            var samples = Read(Path.Combine(Environment.GetEnvironmentVariable("MUESLI_BODHAN_AUDIO_DIR") ?? root, clip));
            var prefix = Path.GetFileName(clip).Split('_', '.')[0];
            var language = prefix == "short" ? "en" : prefix;
            var detected = recognizer.Identify(samples, recognizer.Languages);
            if (detected != expected["lid"]) failures.Add($"{clip} lid {detected} != {expected["lid"]}");
            foreach (var (mode, text) in expected.Where(pair => pair.Key != "lid"))
            {
                var output = mode switch { "mixed" => BodhanOutputMode.Mixed, "romanised" => BodhanOutputMode.Romanized, _ => BodhanOutputMode.Native };
                var got = recognizer.Recognize(samples, language, output, 512, []).Text;
                chars += text.Length;
                edits += Distance(text, got);
                if (got != text) mismatches.Add($"{clip} {mode}\n  ref: {text}\n  got: {got}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.True(edits <= chars * 0.005, $"{edits}/{chars} character edits\n" + string.Join("\n", mismatches));
    }

    private static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var previous = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var current = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = current;
            }
        }

        return row[b.Length];
    }

    private static TranscriptionModelDefinition Model(string id) =>
        TranscriptionModelCatalog.Models.FirstOrDefault(model => model.Id == id)
        ?? new TranscriptionModelDefinition(id, id, "", "", "", NativeAsrModelKind.Bodhan, id, "", "", new Dictionary<string, string>());

    private static float[] Read(string path)
    {
        using var reader = new WaveFileReader(path);
        Assert.Equal(16000, reader.WaveFormat.SampleRate);
        var provider = reader.ToSampleProvider();
        var samples = new float[reader.SampleCount];
        Assert.Equal(samples.Length, provider.Read(samples, 0, samples.Length));
        return samples;
    }

    private sealed class BodhanModelFactAttribute : FactAttribute
    {
        public BodhanModelFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("MUESLI_BODHAN_MODEL_DIR") is null ||
                Environment.GetEnvironmentVariable("MUESLI_BODHAN_REFS") is null)
            {
                Skip = "Set MUESLI_BODHAN_MODEL_DIR and MUESLI_BODHAN_REFS (and MUESLI_BODHAN_AUDIO_DIR) to an exported Bodhan model and its reference transcripts.";
            }
        }
    }
}
