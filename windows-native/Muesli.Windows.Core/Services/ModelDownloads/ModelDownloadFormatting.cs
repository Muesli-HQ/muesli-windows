using System.Globalization;

namespace Muesli.Windows.Services;

/// <summary>
/// User-facing download text, ported from the macOS ModelDownloadDisplayFormatting and the Models
/// card detail line so both apps read the same.
/// </summary>
public static class ModelDownloadFormatting
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>Decimal units with one decimal place: "640.0 MB", "1.2 GB".</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1_000_000_000 => string.Format(Culture, "{0:0.0} GB", bytes / 1_000_000_000d),
        >= 1_000_000 => string.Format(Culture, "{0:0.0} MB", bytes / 1_000_000d),
        >= 1_000 => string.Format(Culture, "{0:0.0} KB", bytes / 1_000d),
        _ => string.Format(Culture, "{0} B", Math.Max(0, bytes))
    };

    /// <summary>"12.4 MB/s" (or GB/s at a gigabyte per second); empty when there is no rate yet.</summary>
    public static string Rate(double bytesPerSecond) => bytesPerSecond switch
    {
        <= 0 or double.NaN => "",
        >= 1_000_000_000 => string.Format(Culture, "{0:0.0} GB/s", bytesPerSecond / 1_000_000_000d),
        _ => string.Format(Culture, "{0:0.0} MB/s", bytesPerSecond / 1_000_000d)
    };

    /// <summary>"26s", "4m 05s", "1h 07m"; empty when unknown or implausibly long.</summary>
    public static string Remaining(TimeSpan? remaining)
    {
        if (remaining is not { } value || value < TimeSpan.Zero || value > TimeSpan.FromDays(365)) return "";
        var seconds = (long)Math.Ceiling(value.TotalSeconds);
        if (seconds < 60) return string.Format(Culture, "{0}s", seconds);
        if (seconds < 3600) return string.Format(Culture, "{0}m {1:00}s", seconds / 60, seconds % 60);
        return string.Format(Culture, "{0}h {1:00}m", seconds / 3600, seconds % 3600 / 60);
    }

    /// <summary>The phase label shown above the detail line.</summary>
    public static string PhaseLabel(ModelDownloadPhase phase) => phase switch
    {
        ModelDownloadPhase.Downloading => "Downloading",
        ModelDownloadPhase.Verifying => "Verifying",
        ModelDownloadPhase.Ready => "Ready",
        ModelDownloadPhase.Paused => "Download paused",
        ModelDownloadPhase.Failed => "Failed",
        _ => ""
    };

    /// <summary>
    /// The card detail line, e.g. "3 of 12 files · 312.0 MB / 640.0 MB · 328.0 MB left · 12.4 MB/s ·
    /// 26s left · retry 1/3". Outside an active transfer the message replaces rate and time.
    /// </summary>
    public static string DetailLine(ModelDownloadSnapshot snapshot)
    {
        var parts = new List<string>();
        if (snapshot.TotalFiles > 1)
        {
            parts.Add(string.Format(Culture, "{0} of {1} files", snapshot.CompletedFiles, snapshot.TotalFiles));
        }

        if (snapshot.TotalBytes is { } total && total > 0)
        {
            parts.Add($"{Bytes(snapshot.CompletedBytes)} / {Bytes(total)}");
            var left = total - snapshot.CompletedBytes;
            if (left > 0 && snapshot.Phase == ModelDownloadPhase.Downloading) parts.Add($"{Bytes(left)} left");
        }
        else if (snapshot.CompletedBytes > 0)
        {
            parts.Add(Bytes(snapshot.CompletedBytes));
        }

        if (snapshot.Phase == ModelDownloadPhase.Downloading)
        {
            var rate = Rate(snapshot.BytesPerSecond);
            if (rate.Length > 0) parts.Add(rate);
            var remaining = Remaining(snapshot.TimeRemaining);
            if (remaining.Length > 0) parts.Add($"{remaining} left");
            if (snapshot.RetryCount > 0)
            {
                parts.Add(string.Format(Culture, "retry {0}/{1}", Math.Min(snapshot.RetryCount, ModelDownloadCoordinator.MaxAttempts - 1), ModelDownloadCoordinator.MaxAttempts - 1));
            }
        }
        else if (!string.IsNullOrWhiteSpace(snapshot.Message))
        {
            parts.Add(snapshot.Message);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Compact text for the floating pill and sidebar: "Downloading Parakeet v3 · 42% · ~4m 05s left".</summary>
    public static string Compact(string modelName, ModelDownloadSnapshot snapshot)
    {
        if (snapshot.Phase != ModelDownloadPhase.Downloading)
        {
            return string.IsNullOrWhiteSpace(snapshot.Message) ? $"Preparing {modelName}…" : snapshot.Message;
        }

        var parts = new List<string> { $"Downloading {modelName}" };
        if (snapshot.Fraction is { } fraction) parts.Add(string.Format(Culture, "{0:0}%", fraction * 100));
        var remaining = Remaining(snapshot.TimeRemaining);
        if (remaining.Length > 0) parts.Add($"~{remaining} left");
        return string.Join(" · ", parts);
    }
}
