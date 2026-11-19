namespace Muesli.Windows.Core.Services;

/// <summary>
/// Decides, from the available physical memory read when a dictation starts, whether that
/// dictation decodes while the user is still speaking instead of in one pass after release.
/// With little memory free Windows pages the idle model out; one pass then pays for paging it
/// back in after the key is released, where the user sits waiting.
/// </summary>
public static class DictationMemoryPolicy
{
    /// <summary>
    /// A starting value, not a measured one: tune it from the "Dictation capture opened" log
    /// lines, which record the reading and the chosen path for every dictation. Windows keeps
    /// "available" memory low through standby caching, so the reading is only a rough signal.
    /// </summary>
    public const ulong LowMemoryThresholdBytes = 1536UL * 1024 * 1024;

    /// <summary>False when the reading could not be taken: an absent reading is never "low".</summary>
    public static bool IsLow(ulong? availableBytes) =>
        availableBytes is { } bytes && bytes < LowMemoryThresholdBytes;

    public static bool ShouldDecodeDuringCapture(ulong? availableBytes) => IsLow(availableBytes);

    /// <summary>The reading as it is logged; never a number that was not measured.</summary>
    public static string Describe(ulong? availableBytes) =>
        availableBytes is { } bytes ? (bytes / 1048576).ToString() : "unavailable";
}

/// <summary>
/// Decides whether a finished dictation tells the user why it was slow. The only cause the app
/// measures is low memory, so nothing is shown unless that dictation's own reading was low.
/// </summary>
public static class DictationSlowNotice
{
    public const string LowMemoryMessage = "Slow because your PC is low on memory";

    /// <summary>The wait after release must reach this before any notice, however short the audio.</summary>
    public const long MinimumRecognitionMs = 3000;

    /// <summary>
    /// Recognition time per unit of audio that counts as slow: three times the roughly 0.12
    /// measured for Parakeet v3 on this CPU.
    /// </summary>
    public const double SlowRealTimeFactor = 0.36;

    public static bool IsSlow(long audioMs, long recognitionMs) =>
        audioMs > 0 &&
        recognitionMs >= MinimumRecognitionMs &&
        recognitionMs > audioMs * SlowRealTimeFactor;

    /// <summary>
    /// The line to show, or null. A decode that started while the model was still loading is
    /// slow for that reason and stays silent.
    /// </summary>
    public static string? For(long audioMs, long recognitionMs, ulong? availableBytesAtStart, bool modelWarmupPending) =>
        !modelWarmupPending &&
        IsSlow(audioMs, recognitionMs) &&
        DictationMemoryPolicy.IsLow(availableBytesAtStart)
            ? LowMemoryMessage
            : null;
}
