using System.Security.Cryptography;

namespace Muesli.Windows.Tests;

public sealed class ModelAndSingleInstanceTests
{
    [Fact]
    public void DownloadedModelArtifactsUsePinnedUpstreamHashes()
    {
        Assert.Matches("^[A-F0-9]{64}$", NativeParakeetClient.ModelArchiveSha256);
        Assert.Matches("^[A-F0-9]{64}$", NativeDiarizationClient.SegmentationArchiveSha256);
        Assert.Matches("^[A-F0-9]{64}$", NativeDiarizationClient.SegmentationModelSha256);
        Assert.Matches("^[A-F0-9]{64}$", NativeDiarizationClient.EmbeddingModelSha256);
    }

    [Fact]
    public void TranscriptionCatalogHasUniqueSafeModelDefinitions()
    {
        var models = TranscriptionModelCatalog.Models;
        Assert.Equal(12, models.Count);
        Assert.Equal(models.Count, models.Select(model => model.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(models, model =>
        {
            Assert.False(string.IsNullOrWhiteSpace(model.DisplayName));
            Assert.Matches("^[A-F0-9]{64}$", model.ArchiveSha256);
            Assert.NotEmpty(model.RequiredFiles);
            Assert.All(model.RequiredFiles, relativePath =>
            {
                Assert.False(Path.IsPathRooted(relativePath));
                Assert.DoesNotContain("..", relativePath, StringComparison.Ordinal);
            });
        });
        Assert.Equal(TranscriptionModelCatalog.DefaultModelId, TranscriptionModelCatalog.NormalizeId("unknown"));
        Assert.Equal("whisper-small-en", TranscriptionModelCatalog.NormalizeId("WHISPER-SMALL-EN"));
    }

    [Fact]
    public void HashVerificationHandlesCorrectWrongAndMissingFiles()
    {
        using var directory = new TestDirectory();
        var path = directory.File("model.onnx");
        File.WriteAllText(path, "model");
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("model")));

        Assert.True(NativeDiarizationClient.VerifySha256(path, expected).Matches);
        Assert.False(NativeDiarizationClient.VerifySha256(path, new string('0', 64)).Matches);
        Assert.False(NativeDiarizationClient.VerifySha256(directory.File("missing"), expected).Matches);
    }

    [Theory]
    [InlineData("../escape.onnx")]
    [InlineData("folder/../../escape.onnx")]
    [InlineData("C:/escape.onnx")]
    [InlineData("/escape.onnx")]
    public void ArchiveTraversalIsRejected(string entry)
    {
        using var directory = new TestDirectory();
        Assert.Throws<InvalidDataException>(() => SafeArchiveExtractor.ResolveEntryDestination(directory.Path, entry));
    }

    [Fact]
    public void ArchiveDestinationRemainsInsideCacheRoot()
    {
        using var directory = new TestDirectory();
        var destination = SafeArchiveExtractor.ResolveEntryDestination(directory.Path, "model/files/model.onnx");
        Assert.StartsWith(directory.Path + System.IO.Path.DirectorySeparatorChar, destination, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InterruptedModelSetupArtifactsAreCleanedWithoutTouchingModels()
    {
        using var directory = new TestDirectory();
        var model = directory.File("parakeet-model.onnx");
        var partial = directory.File(".parakeet.abc.partial");
        var download = directory.File(".parakeet.def.download");
        var unrelated = directory.File(".other.abc.partial");
        var staging = directory.File(".parakeet.ghi.staging");
        File.WriteAllText(model, "keep");
        File.WriteAllText(partial, "partial");
        File.WriteAllText(download, "partial");
        File.WriteAllText(unrelated, "keep");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "part"), "partial");

        ModelSetupArtifactCleaner.Cleanup(directory.Path, "parakeet");

        Assert.True(File.Exists(model));
        Assert.True(File.Exists(unrelated));
        Assert.False(File.Exists(partial));
        Assert.False(File.Exists(download));
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task CrossProcessModelLockSerializesAndHonorsCancellation()
    {
        using var directory = new TestDirectory();
        var lockPath = directory.File("model.lock");
        await using var first = await CrossProcessFileLock.AcquireAsync(
            lockPath,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CrossProcessFileLock.AcquireAsync(
            lockPath,
            TimeSpan.FromSeconds(1),
            cancellation.Token));
    }

    [Fact]
    public void SingleInstanceDecisionKeepsHeadlessCommandsIndependent()
    {
        Assert.True(SingleInstanceCoordinator.IsHeadlessCommand(["Muesli.exe", "--diagnose-native"]));
        Assert.True(SingleInstanceCoordinator.IsHeadlessCommand(["--benchmark-meeting"]));
        Assert.False(SingleInstanceCoordinator.IsHeadlessCommand(["Muesli.exe", "--background"]));
        Assert.True(SingleInstanceCoordinator.IsActivationMessage("activate-dashboard\r\n"));
        Assert.False(SingleInstanceCoordinator.IsActivationMessage("unknown"));
    }
}
