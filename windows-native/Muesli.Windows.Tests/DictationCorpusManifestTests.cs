using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Muesli.Windows.Tests;

[Collection("PowerShellScripts")]
public sealed class DictationCorpusManifestTests
{
    private static readonly string[] RequiredCategories =
    [
        "short-command",
        "paragraph",
        "dictionary",
        "numbers-punctuation",
        "accent",
        "silence",
        "background-noise"
    ];

    [Fact]
    public void TemplateDeclaresSchema2CoverageAndRemainsUnreviewed()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TemplatePath));
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("windows-dictation-human-qualification", root.GetProperty("name").GetString());
        Assert.Equal(0.15, root.GetProperty("defaultMaxWordErrorRate").GetDouble());
        Assert.Equal(0.08, root.GetProperty("defaultMaxCharacterErrorRate").GetDouble());
        Assert.Equal(0.20, root.GetProperty("defaultMaxRealtimeFactor").GetDouble());
        Assert.Equal("placeholder-not-qualified", root.GetProperty("status").GetString());

        var cases = root.GetProperty("cases").EnumerateArray().ToList();
        Assert.True(cases.Count >= RequiredCategories.Length);
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dictionarySlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in cases)
        {
            Assert.True(item.GetProperty("placeholder").GetBoolean());
            Assert.True(string.IsNullOrWhiteSpace(item.GetProperty("reviewedBy").GetString()));
            Assert.True(string.IsNullOrWhiteSpace(item.GetProperty("reviewedAt").GetString()));
            Assert.True(string.IsNullOrWhiteSpace(item.GetProperty("referenceProvenance").GetString()));
            foreach (var category in item.GetProperty("categories").EnumerateArray())
                covered.Add(category.GetString() ?? "");
            var id = item.GetProperty("id").GetString() ?? "";
            if (id.StartsWith("dictionary-", StringComparison.Ordinal))
                dictionarySlots.Add(id);
            Assert.DoesNotContain("expectedTranscript", item.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("observedTranscript", item.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        foreach (var category in RequiredCategories)
            Assert.Contains(category, covered);
        Assert.Contains("dictionary-name", dictionarySlots);
        Assert.Contains("dictionary-multiword", dictionarySlots);
        Assert.Contains("dictionary-replacement", dictionarySlots);
        Assert.False(Directory.EnumerateFiles(Path.Combine(CorpusDirectory, "audio"), "*.wav").Any());
    }

    [Fact]
    public void ValidateOnlyRejectsTheCheckedInPlaceholderTemplate()
    {
        var result = RunCorpusValidate(TemplatePath);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("placeholder case is not human-reviewed evidence", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("validationPassed\":  true", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("expectedTranscript", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("observedTranscript", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOnlyRejectsEmptyAndModelOnlyReferences()
    {
        using var directory = new TempCorpus();
        directory.WriteSilentWav("case.wav");
        File.WriteAllText(Path.Combine(directory.Path, "empty.txt"), "");
        File.WriteAllText(Path.Combine(directory.Path, "model.txt"), "model draft text");

        var empty = RunCorpusValidate(directory.WriteManifest(
            "empty-reference",
            reference: "empty.txt",
            provenance: "human-reviewed"));
        Assert.NotEqual(0, empty.ExitCode);
        Assert.Contains("reference transcript is empty", empty.Text, StringComparison.OrdinalIgnoreCase);

        var modelOnly = RunCorpusValidate(directory.WriteManifest(
            "model-only",
            reference: "model.txt",
            provenance: "model-generated"));
        Assert.NotEqual(0, modelOnly.ExitCode);
        Assert.Contains("model-only references are rejected", modelOnly.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOnlyRejectsMissingCategoriesAndNoSpeechWithoutHumanReview()
    {
        using var directory = new TempCorpus();
        directory.WriteSilentWav("case.wav");
        File.WriteAllText(Path.Combine(directory.Path, "reference.txt"), "open settings");

        var missingCategory = RunCorpusValidate(directory.WriteManifest(
            "missing-category",
            reference: "reference.txt",
            provenance: "human-reviewed",
            categories: ["short-command"]));
        Assert.NotEqual(0, missingCategory.ExitCode);
        Assert.Contains("missing required category", missingCategory.Text, StringComparison.OrdinalIgnoreCase);

        var noSpeech = RunCorpusValidate(directory.WriteManifest(
            "no-speech-model",
            reference: "",
            provenance: "human-transcribed",
            expectedOutcome: "no-speech",
            includeReference: false));
        Assert.NotEqual(0, noSpeech.ExitCode);
        Assert.Contains("no-speech audio must be explicitly human-reviewed", noSpeech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOnlyAcceptsACompleteTempSchemaContractAndDoesNotClaimPhase2()
    {
        using var directory = new TempCorpus();
        directory.WriteSilentWav("speech.wav");
        directory.WriteSilentWav("silence.wav");
        File.WriteAllText(Path.Combine(directory.Path, "speech.txt"), "open the settings panel");
        var manifest = Path.Combine(directory.Path, "complete.json");
        File.WriteAllText(manifest, """
            {
              "schemaVersion": 2,
              "name": "schema-contract-fixture-not-qualification",
              "defaultMaxWordErrorRate": 0.15,
              "defaultMaxCharacterErrorRate": 0.08,
              "defaultMaxRealtimeFactor": 0.20,
              "cases": [
                {"id":"short-command","audio":"speech.wav","reference":"speech.txt","expectedOutcome":"transcript","categories":["short-command"],"referenceProvenance":"human-reviewed","reviewedBy":"schema-contract-fixture","reviewedAt":"2026-08-18T00:00:00Z"},
                {"id":"paragraph","audio":"speech.wav","reference":"speech.txt","expectedOutcome":"transcript","categories":["paragraph"],"referenceProvenance":"human-reviewed","reviewedBy":"schema-contract-fixture","reviewedAt":"2026-08-18T00:00:00Z"},
                {"id":"dictionary","audio":"speech.wav","reference":"speech.txt","expectedOutcome":"transcript","categories":["dictionary"],"referenceProvenance":"human-reviewed","reviewedBy":"schema-contract-fixture","reviewedAt":"2026-08-18T00:00:00Z"},
                {"id":"numbers-punctuation","audio":"speech.wav","reference":"speech.txt","expectedOutcome":"transcript","categories":["numbers-punctuation"],"referenceProvenance":"human-reviewed","reviewedBy":"schema-contract-fixture","reviewedAt":"2026-08-18T00:00:00Z"},
                {"id":"accent","audio":"speech.wav","reference":"speech.txt","expectedOutcome":"transcript","categories":["accent"],"referenceProvenance":"human-reviewed","reviewedBy":"schema-contract-fixture","reviewedAt":"2026-08-18T00:00:00Z"},
                {"id":"silence","audio":"silence.wav","expectedOutcome":"no-speech","categories":["silence"],"referenceProvenance":"human-reviewed","reviewedBy":"schema-contract-fixture","reviewedAt":"2026-08-18T00:00:00Z"},
                {"id":"background-noise","audio":"silence.wav","expectedOutcome":"no-speech","categories":["background-noise"],"referenceProvenance":"human-reviewed","reviewedBy":"schema-contract-fixture","reviewedAt":"2026-08-18T00:00:00Z"}
              ]
            }
            """);

        var result = RunCorpusValidate(manifest);
        Assert.True(result.ExitCode == 0, result.Text);
        Assert.Contains("validationPassed", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("windows-dictation-human-qualification", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("expectedTranscript", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TargetQualifierRejectsPlaceholderTraceIdsWithoutWritingAPassReport()
    {
        var output = Path.Combine(Path.GetTempPath(), $"muesli-target-{Guid.NewGuid():N}.json");
        var result = RunScript(
            Path.Combine(RepositoryRoot, "scripts", "qualify-dictation-target.ps1"),
            "-TargetKind", "Notepad",
            "-TargetProcess", "notepad",
            "-TraceId", "placeholder",
            "-RequiredModelId", "parakeet-v3",
            "-MaxReleaseToPasteMs", "3000",
            "-ReviewedBy", "schema-contract-fixture",
            "-ReviewedAt", "2026-08-18T00:00:00Z",
            "-TextVerified",
            "-OutputPath", output);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("12-character hex", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void TargetSuiteRequiresFourFreshNativeParakeetForegroundTracesWithinThreeSeconds()
    {
        using var directory = new TempCorpus();
        var reportPaths = new List<string>();
        foreach (var (kind, process) in new[]
                 {
                     ("Notepad", "notepad"),
                     ("Chrome", "chrome"),
                     ("Office", "winword"),
                     ("Other", "code")
                 })
        {
            var reportPath = Path.Combine(directory.Path, $"{kind}.json");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                targetKind = kind,
                targetProcess = process,
                traceId = Guid.NewGuid().ToString("N")[..12],
                requiredModelId = "parakeet-v3",
                releaseToPasteMs = 1250,
                reviewedBy = "human-reviewer",
                reviewedAt = DateTimeOffset.UtcNow,
                textVerified = true,
                passed = true,
                failures = Array.Empty<string>(),
                trace = new
                {
                    status = "success",
                    engine = "native-sherpa-onnx/cpu",
                    model = "parakeet-v3",
                    deliveryMode = "active-app",
                    targetForeground = "True",
                    historyPersisted = "True",
                    chars = "24"
                }
            }));
            reportPaths.Add(reportPath);
        }

        var output = Path.Combine(directory.Path, "suite.json");
        var result = RunScript(
            Path.Combine(RepositoryRoot, "scripts", "qualify-dictation-target-suite.ps1"),
            "-ReportPaths", string.Join(",", reportPaths), "-OutputPath", output);

        Assert.True(result.ExitCode == 0, result.Text);
        using var suite = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal(2, suite.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(3000, suite.RootElement.GetProperty("prerequisites").GetProperty("latencyTargetMs").GetInt32());
        Assert.Equal(1250, suite.RootElement.GetProperty("latency").GetProperty("p95ReleaseToPasteMs").GetInt64());
    }

    [Fact]
    public void TargetSuiteRejectsNonParakeetOrSlowEvidence()
    {
        using var directory = new TempCorpus();
        var reportPaths = new List<string>();
        foreach (var (kind, process, model, latency) in new[]
                 {
                     ("Notepad", "notepad", "parakeet-v3", 1250),
                     ("Chrome", "chrome", "parakeet-v3", 1250),
                     ("Office", "winword", "parakeet-v3", 1250),
                     ("Other", "code", "whisper-small-en", 3500)
                 })
        {
            var reportPath = Path.Combine(directory.Path, $"{kind}.json");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                targetKind = kind,
                targetProcess = process,
                traceId = Guid.NewGuid().ToString("N")[..12],
                requiredModelId = model,
                releaseToPasteMs = latency,
                reviewedBy = "human-reviewer",
                reviewedAt = DateTimeOffset.UtcNow,
                textVerified = true,
                passed = true,
                failures = Array.Empty<string>(),
                trace = new
                {
                    status = "success",
                    engine = "native-sherpa-onnx/cpu",
                    model,
                    deliveryMode = "active-app",
                    targetForeground = "True",
                    historyPersisted = "True",
                    chars = "24"
                }
            }));
            reportPaths.Add(reportPath);
        }

        var result = RunScript(
            Path.Combine(RepositoryRoot, "scripts", "qualify-dictation-target-suite.ps1"),
            "-ReportPaths", string.Join(",", reportPaths));

        Assert.True(result.ExitCode != 0, result.Text);
        Assert.Contains("model identity", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static string RepositoryRoot => TestRepositoryLayout.Root;

    private static string CorpusDirectory => Path.Combine(RepositoryRoot, "qualification", "dictation-corpus");
    private static string TemplatePath => Path.Combine(CorpusDirectory, "windows-dictation-human-qualification.template.json");

    private static ScriptResult RunCorpusValidate(string manifestPath) =>
        RunScript(
            Path.Combine(RepositoryRoot, "scripts", "test-transcription-corpus.ps1"),
            "-ManifestPath", manifestPath,
            "-ValidateOnly");

    private static ScriptResult RunScript(string scriptPath, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepositoryRoot
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-Command");
        var escaped = arguments.Select(argument =>
            argument.Contains(' ') || argument.Contains('\'')
                ? "'" + argument.Replace("'", "''") + "'"
                : argument);
        start.ArgumentList.Add("& '" + scriptPath.Replace("'", "''") + "' " + string.Join(' ', escaped) + "; exit $LASTEXITCODE");

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start powershell.exe.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("powershell.exe did not exit.");
        }
        Task.WaitAll(stdoutTask, stderrTask);
        var text = string.Join(' ', stdoutTask.Result, stderrTask.Result);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return new ScriptResult(process.ExitCode, text);
    }

    private sealed record ScriptResult(int ExitCode, string Text);

    private sealed class TempCorpus : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "muesli-corpus-" + Guid.NewGuid().ToString("N"));

        public TempCorpus() => Directory.CreateDirectory(Path);

        public void WriteSilentWav(string fileName)
        {
            const int sampleRate = 16000;
            const short channels = 1;
            const short bits = 16;
            const int milliseconds = 250;
            var samples = sampleRate * milliseconds / 1000;
            var dataSize = samples * channels * (bits / 8);
            using var writer = new BinaryWriter(File.Create(System.IO.Path.Combine(Path, fileName)));
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * bits / 8);
            writer.Write((short)(channels * bits / 8));
            writer.Write(bits);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);
            writer.Write(new byte[dataSize]);
        }

        public string WriteManifest(
            string name,
            string reference,
            string provenance,
            string expectedOutcome = "transcript",
            bool includeReference = true,
            IReadOnlyList<string>? categories = null)
        {
            categories ??= RequiredCategories;
            var caseObject = new Dictionary<string, object?>
            {
                ["id"] = "short-command",
                ["audio"] = "case.wav",
                ["expectedOutcome"] = expectedOutcome,
                ["categories"] = categories,
                ["referenceProvenance"] = provenance,
                ["reviewedBy"] = "schema-contract-fixture",
                ["reviewedAt"] = "2026-08-18T00:00:00Z"
            };
            if (includeReference)
                caseObject["reference"] = reference;

            var payload = new Dictionary<string, object?>
            {
                ["schemaVersion"] = 2,
                ["name"] = name,
                ["defaultMaxWordErrorRate"] = 0.15,
                ["defaultMaxCharacterErrorRate"] = 0.08,
                ["defaultMaxRealtimeFactor"] = 0.20,
                ["cases"] = new object[] { caseObject }
            };
            var manifestPath = System.IO.Path.Combine(Path, name + ".json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(payload));
            return manifestPath;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }
}
