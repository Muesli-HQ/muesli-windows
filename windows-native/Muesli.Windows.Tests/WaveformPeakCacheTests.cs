using Muesli.Windows.Services;
using NAudio.Wave;

namespace Muesli.Windows.Tests;

public sealed class WaveformPeakCacheTests
{
    [Fact]
    public async Task SamplerProducesExactly72FinitePeaksAndCacheRoundTrips()
    {
        using var directory = new TestDirectory();
        var source = directory.File("meeting.wav");
        WritePcmWav(source, Enumerable.Repeat((short)900, 16000).ToArray());
        var cache = new WaveformPeakCache(directory.File("waveforms"));

        var sampled = await WaveformPeakSampler.SampleAsync(source);
        Assert.Equal(WaveformPeakCache.BucketCount, sampled.Count);
        Assert.All(sampled, peak => Assert.True(double.IsFinite(peak)));

        await cache.SaveAsync(source, sampled);
        var restored = await cache.TryGetAsync(source);

        Assert.NotNull(restored);
        Assert.Equal(sampled, restored);
        Assert.Single(Directory.EnumerateFiles(cache.RootPath, "*.json"));
        Assert.Empty(Directory.EnumerateFiles(cache.RootPath, "*.tmp"));
    }

    [Fact]
    public async Task CacheMissesWhenSourceMetadataChanges()
    {
        using var directory = new TestDirectory();
        var source = directory.File("meeting.wav");
        WritePcmWav(source, Enumerable.Repeat((short)900, 16000).ToArray());
        var cache = new WaveformPeakCache(directory.File("waveforms"));
        await cache.SaveAsync(source, Enumerable.Repeat(1d, WaveformPeakCache.BucketCount).ToArray());

        using (var append = new FileStream(source, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            append.Write([0, 0]);
        }
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(1));

        Assert.Null(await cache.TryGetAsync(source));
    }

    [Fact]
    public async Task CorruptCacheIsIgnoredAndRemoved()
    {
        using var directory = new TestDirectory();
        var source = directory.File("meeting.wav");
        WritePcmWav(source, Enumerable.Repeat((short)900, 16000).ToArray());
        var cache = new WaveformPeakCache(directory.File("waveforms"));
        await cache.SaveAsync(source, Enumerable.Repeat(1d, WaveformPeakCache.BucketCount).ToArray());
        var cacheFile = Assert.Single(Directory.EnumerateFiles(cache.RootPath, "*.json"));
        await File.WriteAllTextAsync(cacheFile, "{ not valid json");

        Assert.Null(await cache.TryGetAsync(source));
        Assert.False(File.Exists(cacheFile));
    }

    [Fact]
    public async Task RemovingSourceAndCleanupDeleteWaveformArtifacts()
    {
        using var directory = new TestDirectory();
        var source = directory.File("meeting.wav");
        WritePcmWav(source, Enumerable.Repeat((short)900, 16000).ToArray());
        var cache = new WaveformPeakCache(directory.File("waveforms"));
        await cache.SaveAsync(source, Enumerable.Repeat(1d, WaveformPeakCache.BucketCount).ToArray());

        await cache.RemoveForSourceAsync(source);

        Assert.Empty(Directory.EnumerateFiles(cache.RootPath, "*.json"));
        Assert.Equal(0, await cache.CleanupAsync());
    }

    [Fact]
    public async Task CanceledSamplingDoesNotProducePartialCache()
    {
        using var directory = new TestDirectory();
        var source = directory.File("meeting.wav");
        WritePcmWav(source, Enumerable.Repeat((short)900, 16000 * 3).ToArray());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WaveformPeakSampler.SampleAsync(source, cancellation.Token));
    }

    [Fact]
    public async Task MissingOrCorruptAudioIsRejectedWithoutCacheArtifacts()
    {
        using var directory = new TestDirectory();
        var missing = directory.File("missing.wav");
        await Assert.ThrowsAsync<FileNotFoundException>(() => WaveformPeakSampler.SampleAsync(missing));

        var corrupt = directory.File("corrupt.wav");
        await File.WriteAllBytesAsync(corrupt, [1, 2, 3, 4]);
        await Assert.ThrowsAnyAsync<Exception>(() => WaveformPeakSampler.SampleAsync(corrupt));
    }

    [Fact]
    public void MeetingBenchmarkReuseGateRequiresOnlyRequestedStreams()
    {
        var scriptPath = Path.Combine(FindRepositoryRoot(), "scripts", "benchmark-native-meeting.ps1");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("$requiresAsrReuse = -not [string]::IsNullOrWhiteSpace($MicAudioPath) -or", script);
        Assert.Contains("-not [string]::IsNullOrWhiteSpace($SystemAudioPath)", script);
        Assert.Contains("$requiresDiarizationReuse = -not [string]::IsNullOrWhiteSpace($SystemAudioPath)", script);
        Assert.Contains("$requiresAsrReuse -and -not $result.AsrModelReused", script);
        Assert.Contains("$requiresDiarizationReuse -and -not $result.DiarizationModelReused", script);
        Assert.DoesNotContain("-not $result.AsrModelReused -or -not $result.DiarizationModelReused", script);
    }

    private static string FindRepositoryRoot() => TestRepositoryLayout.Root;

    private static void WritePcmWav(string path, short[] samples)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(16000, 16, 1));
        var bytes = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        writer.Write(bytes, 0, bytes.Length);
    }
}
