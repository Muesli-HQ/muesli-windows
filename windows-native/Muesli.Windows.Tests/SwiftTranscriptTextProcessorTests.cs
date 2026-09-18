using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Muesli.Windows.Core.Insights;
using Muesli.Windows.Services.Text;
using Xunit;

namespace Muesli.Windows.Tests;

[CollectionDefinition("TranscriptTextProcessing", DisableParallelization = true)]
public sealed class TranscriptTextProcessingCollection
{
}

/// <summary>
/// Native text-processing bridge tests. The bridge is staged beside the test assembly by the
/// project's build target, so these exercise the real shared Swift implementation.
/// </summary>
[Collection("TranscriptTextProcessing")]
public sealed class SwiftTranscriptTextProcessorTests
{
    private static string BridgePath() =>
        Path.Combine(AppContext.BaseDirectory, "MuesliCoreABI.dll");

    private static readonly string[] Corpus =
    {
        "",
        "   ",
        "\n\t \r\n",
        "hello",
        "  hello   world  ",
        "a\tb",
        "a\nb\rc\r\nd",
        "first\n\nsecond paragraph",
        "don't can't won't",
        "it’s a “quote”—really",
        "3.14 42 1,000",
        "!!! ??? ...",
        "Zürich café 東京 Москва नमस्ते",
        "emoji 🎙️ stays",
        "a\u00A0\u00A0b",
        "line\u2028break",
        string.Concat(Enumerable.Repeat("word  \n", 5000)),
    };

    [Fact]
    public void NativeProcessorIsActiveWhenStaged()
    {
        using var processor = new SwiftTranscriptTextProcessor(BridgePath(), new ManagedTranscriptTextProcessor());
        Assert.True(processor.IsNative);
        Assert.False(processor.FallbackActivated);
        Assert.NotEqual(0u, processor.Capabilities & SwiftTextProcessingNative.CapabilityTextProcessing);
    }

    [Fact]
    public void SwiftMatchesManagedAcrossCorpus()
    {
        using var swift = new SwiftTranscriptTextProcessor(BridgePath(), new ManagedTranscriptTextProcessor());
        var managed = new ManagedTranscriptTextProcessor();
        foreach (var input in Corpus)
        {
            Assert.Equal(managed.NormalizeTranscript(input), swift.NormalizeTranscript(input));
            Assert.Equal(managed.CountWords(input), swift.CountWords(input));
        }
    }

    [Fact]
    public void NormalizationContractExamples()
    {
        using var processor = new SwiftTranscriptTextProcessor(BridgePath(), new ManagedTranscriptTextProcessor());
        Assert.Equal("hello world", processor.NormalizeTranscript("  hello   world \n"));
        Assert.Equal("first second paragraph", processor.NormalizeTranscript("first\n\nsecond paragraph"));
        Assert.Equal("a b", processor.NormalizeTranscript("a\u00A0\u00A0b"));
        Assert.Equal("", processor.NormalizeTranscript("   "));
        Assert.Equal(4, processor.CountWords("one two   three\nfour"));
    }

    [Fact]
    public void ConcurrentNormalizationIsSafeAndDeterministic()
    {
        using var processor = new SwiftTranscriptTextProcessor(BridgePath(), new ManagedTranscriptTextProcessor());
        var results = new string[64];
        Parallel.For(0, results.Length, index =>
        {
            results[index] = processor.NormalizeTranscript($"  thread {index}   text ");
        });
        Assert.Equal(Enumerable.Range(0, results.Length).Select(i => $"thread {i} text"), results);
    }

    [Fact]
    public void NativeWorkDoesNotDependOnPathVariable()
    {
        var original = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            using var processor = new SwiftTranscriptTextProcessor(BridgePath(), new ManagedTranscriptTextProcessor());
            Assert.Equal("hello world", processor.NormalizeTranscript(" hello   world "));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", original);
        }
    }

    [Fact]
    public void CapabilityGateDistinguishesPersistenceFromTextProcessing()
    {
        Assert.False(SwiftTextProcessingNative.CapabilitiesIncludeTextProcessing(1u)); // persistence only
        Assert.True(SwiftTextProcessingNative.CapabilitiesIncludeTextProcessing(2u));  // text only
        Assert.True(SwiftTextProcessingNative.CapabilitiesIncludeTextProcessing(3u));  // both
    }

    [Fact]
    public void MissingBridgeFallsBackToManagedWithDiagnostic()
    {
        using var directory = new TempDirectory();
        var active = TranscriptTextProcessingBootstrap.Initialize(log: null, applicationDirectory: directory.Path);
        try
        {
            Assert.False(active);
            Assert.False(TranscriptTextProcessing.NativeActive);
            Assert.Contains("parity-safe-managed", TranscriptTextProcessingBootstrap.LastDiagnostic);
            Assert.Equal("fallback ok", TranscriptTextProcessing.Current.NormalizeTranscript("  fallback   ok "));
        }
        finally
        {
            TranscriptTextProcessing.ResetToManaged();
        }
    }

    [Fact]
    public void UnloadableBridgeFallsBackToManaged()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "MuesliCoreABI.dll"), "not a portable executable");
        var active = TranscriptTextProcessingBootstrap.Initialize(log: null, applicationDirectory: directory.Path);
        try
        {
            Assert.False(active);
            Assert.False(TranscriptTextProcessing.NativeActive);
        }
        finally
        {
            TranscriptTextProcessing.ResetToManaged();
        }
    }

    [Fact]
    public void BootstrapActivatesNativeWhenBridgeIsValid()
    {
        var active = TranscriptTextProcessingBootstrap.Initialize(
            log: null, applicationDirectory: AppContext.BaseDirectory, allowManagedFallback: false);
        try
        {
            Assert.True(active);
            Assert.True(TranscriptTextProcessing.NativeActive);
            Assert.Contains("native=active", TranscriptTextProcessingBootstrap.LastDiagnostic);
            Assert.Equal("one two three", TranscriptTextProcessing.Current.NormalizeTranscript(" one  two\nthree "));
            Assert.Equal(3, LibraryMetrics.CountWords(" one  two\nthree "));
        }
        finally
        {
            TranscriptTextProcessing.ResetToManaged();
        }
    }

    [Fact]
    public void LibraryMetricsUsesActiveProcessor()
    {
        TranscriptTextProcessing.Initialize(new ManagedTranscriptTextProcessor());
        Assert.Equal(3, LibraryMetrics.CountWords("one two three"));
        Assert.Equal(0, LibraryMetrics.CountWords(null));
        Assert.Equal(0, LibraryMetrics.CountWords("   "));
    }
}
