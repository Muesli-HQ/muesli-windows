using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class SupportBundleTests
{
    private static readonly DateTimeOffset GeneratedAt =
        new(2026, 9, 26, 12, 0, 0, TimeSpan.FromHours(-7));

    [Fact]
    public void BundleDescribesEnvironmentRolesAndDeviceCategories()
    {
        using var directory = new TestDirectory();
        var bundle = new SupportBundleService().Build(Request(), directory.Path, GeneratedAt);

        Assert.Contains("App version: 0.3.0", bundle.Text, StringComparison.Ordinal);
        Assert.Contains("Package identity: Muesli 0.3.0.0 (X64)", bundle.Text, StringComparison.Ordinal);
        Assert.Contains("Dictation model: parakeet-v3", bundle.Text, StringComparison.Ordinal);
        Assert.Contains("Active input devices: 2", bundle.Text, StringComparison.Ordinal);
        Assert.Contains("Active output devices: 3", bundle.Text, StringComparison.Ordinal);
        Assert.Contains("OpenAI key configured: yes", bundle.Text, StringComparison.Ordinal);
        Assert.Contains("Bundle format: 1", bundle.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void BundleRedactsUrlsTitlesUserPathsAndCredentials()
    {
        using var directory = new TestDirectory();
        var runtime = Diagnostics(
            "runtime loaded from C:\\Users\\secretuser\\.cache\\muesli; api_key=sk-abcdef1234567890; " +
            "url=https://meet.google.com/abc-defg-hij; targetWindowTitle=Budget.xlsx - Excel");
        var bundle = new SupportBundleService().Build(Request(runtime), directory.Path, GeneratedAt);

        Assert.DoesNotContain("meet.google.com", bundle.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Budget.xlsx", bundle.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("secretuser", bundle.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-abcdef1234567890", bundle.Text, StringComparison.Ordinal);
        Assert.Contains("[secret-redacted]", bundle.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void BundleNeverEmitsNetworkUrlsOrEndpoints()
    {
        using var directory = new TestDirectory();
        var bundle = new SupportBundleService().Build(Request(), directory.Path, GeneratedAt);

        Assert.DoesNotContain("http://", bundle.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", bundle.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localhost:11434", bundle.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LargeLogIsBoundedToTheNewestBytes()
    {
        using var directory = new TestDirectory();
        var log = directory.File("muesli-2026-09-26.log");
        var builder = new StringBuilder();
        builder.Append("[2026-09-26 08:00:00.000] INFO OLD-MARKER-BEGIN\n");
        for (var index = 0; index < 4000; index++)
        {
            builder.Append($"[2026-09-26 08:{index % 60:00}:00.000] INFO filler line {index}\n");
        }

        builder.Append("[2026-09-26 23:59:59.000] ERROR NEW-MARKER-END clipboard failed\n");
        File.WriteAllText(log, builder.ToString());

        var bundle = new SupportBundleService().Build(
            Request(),
            directory.Path,
            GeneratedAt,
            logByteBudget: 4096,
            logLineBudget: 200);

        Assert.DoesNotContain("OLD-MARKER-BEGIN", bundle.Text, StringComparison.Ordinal);
        Assert.Contains("NEW-MARKER-END", bundle.Text, StringComparison.Ordinal);
        Assert.True(bundle.LogBytesIncluded <= 4096, $"included {bundle.LogBytesIncluded} bytes");
        Assert.True(bundle.LogLinesIncluded <= 200, $"included {bundle.LogLinesIncluded} lines");
    }

    [Fact]
    public void LockedLogDoesNotFailTheExport()
    {
        using var directory = new TestDirectory();
        var log = directory.File("muesli-2026-09-26.log");
        File.WriteAllText(log, "[2026-09-26 12:00:00.000] ERROR something happened\n");

        using (new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var bundle = new SupportBundleService().Build(Request(), directory.Path, GeneratedAt);
            Assert.Contains("No log lines available.", bundle.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ErrorLinesAreAggregatedIntoPrivacySafeIncidentCategories()
    {
        using var directory = new TestDirectory();
        var log = directory.File("muesli-2026-09-26.log");
        File.WriteAllLines(log,
        [
            "[2026-09-26 09:00:00.000] INFO startup complete",
            "[2026-09-26 09:01:00.000] ERROR clipboard paste failed for targetWindowTitle=Secret.docx - Word",
            "[2026-09-26 09:02:00.000] ERROR clipboard paste failed again",
            "[2026-09-26 09:03:00.000] ERROR sherpa recognizer returned no speech"
        ]);

        var bundle = new SupportBundleService().Build(Request(), directory.Path, GeneratedAt);

        var clipboard = Assert.Single(bundle.Incidents, i => i.Category == "Clipboard or paste");
        Assert.Equal(2, clipboard.Count);
        Assert.Equal("2026-09-26 09:02:00", clipboard.LastOccurredUtc?.ToString("yyyy-MM-dd HH:mm:ss"));
        Assert.DoesNotContain("Secret.docx", clipboard.SampleExcerpt, StringComparison.Ordinal);
        Assert.Contains(bundle.Incidents, i => i.Category == "Transcription");
    }

    [Fact]
    public void SupportBundleRedactionTruncatesLongExcerpts()
    {
        var longLine = new string('x', 400);
        var truncated = SupportBundleRedaction.Truncate(longLine, SupportBundleService.MaximumSampleExcerptLength);
        Assert.Equal(SupportBundleService.MaximumSampleExcerptLength + 1, truncated.Length);
        Assert.EndsWith("…", truncated, StringComparison.Ordinal);
    }

    [Fact]
    public void SupportBundleServiceHasNoNetworkTransmissionPath()
    {
        var source = File.ReadAllText(
            TestRepositoryLayout.Combine(
                "windows-native", "Muesli.Windows.Core", "Services", "SupportBundleService.cs"));

        Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Net", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WebRequest", source, StringComparison.Ordinal);
    }

    private static SupportBundleRequest Request(RuntimeDiagnostics? runtime = null) => new(
        new SupportBundleEnvironment(
            "0.3.0",
            ".NET 10.0.0",
            "Microsoft Windows 11.0.26100",
            "X64",
            "Muesli 0.3.0.0 (X64)"),
        new SupportBundleAudioSummary(2, 3),
        new SupportBundleSettingsSummary(
            "dark",
            "parakeet-v3",
            "parakeet-v3",
            null,
            false,
            "local",
            false,
            OpenAiKeyConfigured: true,
            OpenRouterKeyConfigured: false,
            OllamaConfigured: true),
        runtime ?? Diagnostics("Execution provider: cpu"));

    private static RuntimeDiagnostics Diagnostics(string detail) => new(
        "Native Sherpa ONNX cpu",
        RuntimeReady: true,
        ModelReady: true,
        FinalMeetingModelReady: true,
        DictationModelName: "Parakeet",
        FinalMeetingModelName: "Parakeet",
        Acceleration: "CPU",
        DictationModelStatus: "Ready",
        DiarizationStatus: "Ready",
        QwenCleanupStatus: "Disabled",
        ModelCacheDirectory: "cache",
        ModelCacheBytes: 1024,
        Detail: detail);
}
