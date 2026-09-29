namespace Muesli.Windows.Core.Services;

/// <summary>
/// A presentation-only driver for the floating dictation indicator, used by UI Automation to
/// render the states that are otherwise reachable only through real microphone capture and
/// transcription (preparing, recording, transcribing, success, error).
/// </summary>
/// <remarks>
/// <para>
/// This exists because the live indicator states are produced by real hardware. It drives the
/// indicator's <em>presentation</em> through the same rendering path the product uses; it never
/// fabricates transcripts, history, or any other product data, and it cannot start, stop, or alter
/// a real dictation.
/// </para>
/// <para>
/// It is inert unless <c>--indicator-probe</c> is passed on the command line. A packaged AUMID
/// activation does not inherit the launcher's environment, which is why this is a command-line
/// flag and a file in the profile rather than an environment variable — the same reason
/// <c>--profile-root</c> exists.
/// </para>
/// <para>
/// A state driven this way is a <em>simulated presentation state</em>, never evidence that the
/// underlying capture or transcription service works.
/// </para>
/// </remarks>
public static class FloatingIndicatorProbe
{
    /// <summary>The command-line flag that arms the probe.</summary>
    public const string Flag = "--indicator-probe";

    /// <summary>The file, inside the active profile root, the probe polls for a state name.</summary>
    public const string StateFileName = "indicator-probe.state";

    /// <summary>
    /// The status text published for a probe-driven success pill. Deliberately self-identifying so
    /// a probe run can never be mistaken for a real dictation outcome in a screenshot.
    /// </summary>
    public const string SuccessMessage = "Probe: dictation inserted";

    /// <summary>The status text published for a probe-driven error pill.</summary>
    public const string ErrorMessage = "Probe: dictation failed";

    /// <summary>True when <paramref name="args"/> contains the probe flag.</summary>
    public static bool IsEnabled(IReadOnlyList<string>? args)
    {
        if (args is null) return false;
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index]?.Trim(), Flag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>True when the current process was started with the probe flag.</summary>
    public static bool IsEnabledForCurrentProcess() =>
        IsEnabled(Environment.GetCommandLineArgs());

    /// <summary>
    /// Parses a state name written into the probe file. Returns <c>null</c> for anything
    /// unrecognised so a partially written or stale file leaves the indicator alone.
    /// </summary>
    public static FloatingIndicatorState? ParseState(string? text) =>
        (text ?? "").Trim().ToLowerInvariant() switch
        {
            "idle" => FloatingIndicatorState.Idle,
            "preparing" => FloatingIndicatorState.Preparing,
            "recording" => FloatingIndicatorState.Recording,
            "transcribing" => FloatingIndicatorState.Transcribing,
            "success" => FloatingIndicatorState.Success,
            "error" => FloatingIndicatorState.Error,
            _ => null
        };

    /// <summary>
    /// The status text a probe-driven state publishes, matching what the real pipeline would set
    /// for that state so the classifier and the pill render exactly as they do in production.
    /// </summary>
    public static string StatusFor(FloatingIndicatorState state) =>
        state switch
        {
            FloatingIndicatorState.Preparing => "Preparing microphone…",
            FloatingIndicatorState.Recording => "Listening",
            FloatingIndicatorState.Transcribing => "Transcribing locally…",
            FloatingIndicatorState.Success => SuccessMessage,
            FloatingIndicatorState.Error => ErrorMessage,
            _ => "Ready"
        };
}
