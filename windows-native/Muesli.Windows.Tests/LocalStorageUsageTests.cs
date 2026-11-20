using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>MAC-11 (macOS LocalStorageUsageTests): categories, overlapping roots once, links never followed.</summary>
public sealed class LocalStorageUsageTests
{
    [Fact]
    public void CategorizesFilesWithoutCountingLinksOrOverlappingRootsTwice()
    {
        using var directory = new TestDirectory();
        var root = directory.Path;
        void Write(string path, int count)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, new byte[count]);
        }
        Write("models/speech/weights.bin", 20_000);
        Write("models/cleanup/model.gguf", 8_000);
        Write("profile/streaming-models/nemotron/model.onnx", 3_000);
        Write("profile/captures/recordings/m1/mic.wav", 6_000);
        Write("profile/captures/meeting-system-1.wav", 500);
        Write("profile/captures/videos/clip.mp4", 9_000);
        Write("profile/captures/videos/clip.mp4.pending.json", 50);
        Write("profile/captures/last-dictation.wav", 700);
        Write("profile/data/muesli.db", 4_000);
        Write("profile/data/muesli.db-wal", 1_000);
        Write("profile/data/windows-dictations.json", 300);
        Write("profile/data/muesli.db.before-repair", 2_000);
        Write("profile/cache/waveforms/a.json", 100);
        Write("profile/windows-settings.json", 200);
        Write("outside/private.bin", 10_000);
        try { Directory.CreateSymbolicLink(Path.Combine(root, "profile", "link"), Path.Combine(root, "outside")); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* no symlink privilege */ }

        var models = Path.Combine(root, "models");
        var usage = LocalStorageUsage.Scan(Path.Combine(root, "profile"), [models, Path.Combine(models, "speech")]);

        Assert.Equal(20_000 + 8_000 + 3_000, usage[LocalStorageUsage.Category.Models]);
        Assert.Equal(6_000 + 500, usage[LocalStorageUsage.Category.MeetingAudio]);
        Assert.Equal(9_000, usage[LocalStorageUsage.Category.MeetingVideo]);
        Assert.Equal(4_000 + 1_000 + 300, usage[LocalStorageUsage.Category.Database]);
        Assert.Equal(100, usage[LocalStorageUsage.Category.WaveformCache]);
        Assert.Equal(50 + 700 + 2_000 + 200, usage[LocalStorageUsage.Category.Other]);
        Assert.Equal(["speech", "cleanup", "nemotron"], usage.Models.Select(model => model.Name));
        Assert.Equal(0, usage.UnavailableLocations);
        Assert.NotNull(usage.FreeBytes);
    }

    [Fact]
    public void MissingFoldersAreNotCreated()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var usage = LocalStorageUsage.Scan(missing, [Path.Combine(missing, "models")]);
        Assert.Equal(0, usage.TotalBytes);
        Assert.False(Directory.Exists(missing));
    }
}
