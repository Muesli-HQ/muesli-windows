using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Muesli.Windows.Services;

/// <summary>
/// Stores sampled meeting waveforms outside the recording directory. A cache entry is
/// versioned and tied to the source path and file metadata, so replacing a recording
/// cannot reuse the old shape.
/// </summary>
public interface IWaveformPeakCache
{
    Task<IReadOnlyList<double>?> TryGetAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        string sourcePath,
        IReadOnlyList<double> peaks,
        CancellationToken cancellationToken = default);

    Task RemoveForSourceAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);

    Task<int> CleanupAsync(CancellationToken cancellationToken = default);
}

public sealed class WaveformPeakCache : IWaveformPeakCache
{
    public const int CurrentVersion = 1;
    public const int BucketCount = 72;

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public WaveformPeakCache(string? rootPath = null)
    {
        RootPath = Path.GetFullPath(rootPath ?? Path.Combine(
            Muesli.Windows.Core.Profiles.MuesliProfilePaths.Current().RootDirectory,
            "cache",
            "waveforms"));
    }

    public string RootPath { get; }

    public async Task<IReadOnlyList<double>?> TryGetAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var path = GetCachePath(sourcePath);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<WaveformCacheDocument>(json, _jsonOptions);
            if (!IsValid(document, sourcePath))
            {
                TryDelete(path!);
                return null;
            }

            return document!.Peaks;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A corrupt or partially written cache must never prevent playback.
            TryDelete(path!);
            return null;
        }
    }

    public async Task SaveAsync(
        string sourcePath,
        IReadOnlyList<double> peaks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peaks);
        if (peaks.Count != BucketCount || peaks.Any(peak => !double.IsFinite(peak)))
        {
            throw new ArgumentException($"Waveform cache entries must contain {BucketCount} finite peaks.", nameof(peaks));
        }

        var metadata = ReadSourceMetadata(sourcePath);
        Directory.CreateDirectory(RootPath);
        var destination = GetCachePath(sourcePath, metadata, BucketCount)
            ?? throw new FileNotFoundException("Waveform source was not found.", sourcePath);
        var document = new WaveformCacheDocument(
            CurrentVersion,
            metadata.Path,
            metadata.Length,
            metadata.LastWriteUtcTicks,
            BucketCount,
            peaks.ToArray());
        var json = JsonSerializer.Serialize(document, _jsonOptions);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public async Task RemoveForSourceAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var canonical = Canonicalize(sourcePath);
        if (!Directory.Exists(RootPath))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(RootPath, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                var document = JsonSerializer.Deserialize<WaveformCacheDocument>(json, _jsonOptions);
                if (document is not null && string.Equals(document.SourcePath, canonical, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(file);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Cleanup is best effort; a later sweep can remove the bad entry.
            }
        }
    }

    public async Task<int> CleanupAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RootPath))
        {
            return 0;
        }

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(RootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                if (TryDelete(file)) removed++;
                continue;
            }
            if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                var document = JsonSerializer.Deserialize<WaveformCacheDocument>(json, _jsonOptions);
                var currentPath = document is null
                    ? null
                    : GetCachePath(document.SourcePath, ReadSourceMetadataOrNull(document.SourcePath), document.Buckets);
                if (document is null || currentPath is null || !string.Equals(
                        Path.GetFullPath(file),
                        Path.GetFullPath(currentPath),
                        StringComparison.OrdinalIgnoreCase) || !IsValid(document, document.SourcePath))
                {
                    if (TryDelete(file)) removed++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                if (TryDelete(file)) removed++;
            }
        }

        return removed;
    }

    internal string? GetCachePath(string sourcePath)
    {
        var metadata = ReadSourceMetadataOrNull(sourcePath);
        return metadata is null ? null : GetCachePath(sourcePath, metadata, BucketCount);
    }

    private string? GetCachePath(string sourcePath, SourceMetadata? metadata, int buckets)
    {
        if (metadata is null)
        {
            return null;
        }
        var identity = $"{metadata.Path}\n{metadata.Length}\n{metadata.LastWriteUtcTicks}\n{buckets}\n{CurrentVersion}";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return Path.Combine(RootPath, digest + ".json");
    }

    private static SourceMetadata ReadSourceMetadata(string sourcePath)
    {
        var canonical = Canonicalize(sourcePath);
        var info = new FileInfo(canonical);
        info.Refresh();
        if (!info.Exists)
        {
            throw new FileNotFoundException("Waveform source was not found.", canonical);
        }
        return new SourceMetadata(canonical, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static SourceMetadata? ReadSourceMetadataOrNull(string sourcePath)
    {
        try { return ReadSourceMetadata(sourcePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string Canonicalize(string sourcePath) => Path.GetFullPath(sourcePath);

    private bool IsValid(WaveformCacheDocument? document, string sourcePath)
    {
        if (document is null || document.Version != CurrentVersion || document.Buckets != BucketCount ||
            document.Peaks is null || document.Peaks.Length != BucketCount ||
            document.Peaks.Any(peak => !double.IsFinite(peak)))
        {
            return false;
        }

        var metadata = ReadSourceMetadataOrNull(sourcePath);
        return metadata is not null &&
               string.Equals(document.SourcePath, metadata.Path, StringComparison.OrdinalIgnoreCase) &&
               document.Length == metadata.Length &&
               document.LastWriteUtcTicks == metadata.LastWriteUtcTicks;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private sealed record SourceMetadata(string Path, long Length, long LastWriteUtcTicks);

    private sealed record WaveformCacheDocument(
        int Version,
        string SourcePath,
        long Length,
        long LastWriteUtcTicks,
        int Buckets,
        double[] Peaks);
}

public static class WaveformPeakSampler
{
    public const int BucketCount = WaveformPeakCache.BucketCount;

    public static Task<IReadOnlyList<double>> SampleAsync(
        string sourcePath,
        CancellationToken cancellationToken = default) =>
        SampleAsync(sourcePath, BucketCount, cancellationToken);

    public static Task<IReadOnlyList<double>> SampleAsync(
        string sourcePath,
        int buckets,
        CancellationToken cancellationToken = default)
    {
        if (buckets <= 0) throw new ArgumentOutOfRangeException(nameof(buckets));
        var path = Path.GetFullPath(sourcePath);
        if (!File.Exists(path)) throw new FileNotFoundException("Waveform source was not found.", path);
        return Task.Run(() => Sample(path, buckets, cancellationToken), cancellationToken);
    }

    private static IReadOnlyList<double> Sample(string path, int buckets, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new AudioFileReader(path);
        var peaks = new double[buckets];
        var blockAlign = Math.Max(1, reader.WaveFormat.BlockAlign);
        var channels = Math.Max(1, reader.WaveFormat.Channels);
        var totalFrames = reader.Length / blockAlign;
        if (totalFrames <= 0)
        {
            return peaks;
        }

        var provider = reader.ToSampleProvider();
        var buffer = new float[Math.Max(channels, 4096 * channels)];
        long frameIndex = 0;
        var peakValues = new float[buckets];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = provider.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            for (var sampleIndex = 0; sampleIndex < read; sampleIndex++)
            {
                var frame = frameIndex + sampleIndex / channels;
                var bucket = (int)Math.Min(buckets - 1, frame * buckets / totalFrames);
                var sample = Math.Abs(buffer[sampleIndex]);
                if (float.IsFinite(sample))
                {
                    peakValues[bucket] = Math.Max(peakValues[bucket], sample);
                }
            }
            frameIndex += read / channels;
        }

        for (var index = 0; index < peaks.Length; index++)
        {
            peaks[index] = peakValues[index] <= 0
                ? 4.8
                : 4 + Math.Clamp(peakValues[index], 0.04f, 1f) * 20;
        }
        return peaks;
    }
}
