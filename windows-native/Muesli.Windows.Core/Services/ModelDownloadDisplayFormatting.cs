namespace Muesli.Windows.Services;

/// <summary>macOS ModelDownloadDisplayFormatting (9136ebca): compact sizes, speed and time left.</summary>
public static class ModelDownloadDisplayFormatting
{
    public static string Bytes(long bytes)
    {
        var value = (double)Math.Max(0, bytes);
        if (value >= 1_000_000_000) return $"{value / 1_000_000_000:0.0} GB";
        if (value >= 1_000_000) return $"{value / 1_000_000:0.0} MB";
        if (value >= 1_000) return $"{value / 1_000:0.0} KB";
        return $"{bytes} B";
    }

    public static string? Eta(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return null;
        var rounded = Math.Round(seconds);
        if (rounded > 365d * 24 * 60 * 60) return null; // Beyond a year is not useful, and must not overflow.
        var total = (int)rounded;
        if (total < 60) return $"{total}s";
        var minutes = total / 60;
        if (minutes < 60) return $"{minutes}m {total % 60:00}s";
        return $"{minutes / 60}h {minutes % 60:00}m";
    }

    public static string Rate(double bytesPerSecond) =>
        bytesPerSecond <= 0 ? ""
        : bytesPerSecond >= 1_000_000_000 ? $"{bytesPerSecond / 1_000_000_000:0.0} GB/s"
        : $"{bytesPerSecond / 1_000_000:0.0} MB/s";
}

/// <summary>
/// macOS ModelDownloadCoordinator.emit: speed is the average since this transfer started, excluding bytes
/// already on disk from a resumed download; time left covers the transfer only, not extraction.
/// One meter per operation.
/// </summary>
public sealed class ModelTransferMeter(Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private string? _stage;
    private DateTimeOffset _startedAt;
    private long _initialBytes;

    public string Describe(ModelDownloadProgress progress)
    {
        var now = _clock();
        if (progress.Stage != _stage)
        {
            _stage = progress.Stage;
            _startedAt = now;
            _initialBytes = progress.BytesReceived;
        }
        // Only a sized, unfinished transfer has bytes, speed and time left; extraction and verification are phases.
        if (progress.TotalBytes is not > 0 || progress.BytesReceived >= progress.TotalBytes) return progress.DisplayText;
        var total = progress.TotalBytes.Value;
        var parts = new List<string>
        {
            progress.DisplayText,
            $"{ModelDownloadDisplayFormatting.Bytes(progress.BytesReceived)} / {ModelDownloadDisplayFormatting.Bytes(total)}",
        };
        var elapsed = (now - _startedAt).TotalSeconds;
        var speed = elapsed > 0 ? Math.Max(0, progress.BytesReceived - _initialBytes) / elapsed : 0;
        if (speed > 0)
        {
            parts.Add(ModelDownloadDisplayFormatting.Rate(speed));
            if (ModelDownloadDisplayFormatting.Eta((total - progress.BytesReceived) / speed) is { } eta) parts.Add($"~{eta} left");
        }
        return string.Join(" · ", parts);
    }
}
