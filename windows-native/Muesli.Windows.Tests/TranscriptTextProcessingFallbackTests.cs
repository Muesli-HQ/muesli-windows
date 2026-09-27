using System.IO;
using Muesli.Windows.Services.Text;

namespace Muesli.Windows.Tests;

/// <summary>
/// Proves the shipping decision recorded in <c>windows-native/shared-core.lock.json</c>: when no
/// approved shared Swift ABI exists, the release ships the parity-tested managed text processor and
/// startup must degrade to it instead of failing.
/// </summary>
[Collection("TranscriptTextProcessing")]
public sealed class TranscriptTextProcessingFallbackTests
{
    [Fact]
    public void BootstrapInstallsTheManagedProcessorWhenTheBridgeIsMissing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "muesli-no-bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.False(File.Exists(Path.Combine(directory, "MuesliCoreABI.dll")));

            var native = TranscriptTextProcessingBootstrap.Initialize(
                applicationDirectory: directory,
                allowManagedFallback: true);

            Assert.False(native);
            Assert.Contains("fallback=parity-safe-managed", TranscriptTextProcessingBootstrap.LastDiagnostic, StringComparison.Ordinal);
            Assert.False(TranscriptTextProcessing.Current.IsNative);
            Assert.Equal(3, TranscriptTextProcessing.Current.CountWords("one two three"));
        }
        finally
        {
            TranscriptTextProcessing.ResetToManaged();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BootstrapFailsClosedWhenTheBridgeIsMissingAndFallbackIsForbidden()
    {
        var directory = Path.Combine(Path.GetTempPath(), "muesli-no-bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Throws<DllNotFoundException>(() => TranscriptTextProcessingBootstrap.Initialize(
                applicationDirectory: directory,
                allowManagedFallback: false));
        }
        finally
        {
            TranscriptTextProcessing.ResetToManaged();
            Directory.Delete(directory, recursive: true);
        }
    }
}
