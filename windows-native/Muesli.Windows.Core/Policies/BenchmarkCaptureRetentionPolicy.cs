namespace Muesli.Windows.Services;

public static class BenchmarkCaptureRetentionPolicy
{
    public const long MaximumBytes = 96L * 1024 * 1024;
    public const int MaximumDurationMs = 10 * 60 * 1000;

    public static bool ShouldRetain(long byteLength, int durationMs) =>
        byteLength > 44 &&
        byteLength <= MaximumBytes &&
        durationMs > 0 &&
        durationMs <= MaximumDurationMs;
}
