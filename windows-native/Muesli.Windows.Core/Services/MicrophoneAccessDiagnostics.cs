namespace Muesli.Windows.Core.Services;

/// <summary>
/// Turns a microphone start-up failure into something the user can act on.
/// </summary>
/// <remarks>
/// A packaged build without the <c>microphone</c> device capability, or one the user has blocked
/// in Windows privacy settings, fails inside
/// <c>WasapiCapture.InitializeCaptureDevice</c> with <c>E_ACCESSDENIED</c>. Reported as a generic
/// "dictation failed" that is indistinguishable from a broken shortcut, so it is classified here.
/// </remarks>
public static class MicrophoneAccessDiagnostics
{
    /// <summary>E_ACCESSDENIED, as surfaced by the WASAPI audio client.</summary>
    public const int AccessDeniedHResult = unchecked((int)0x80070005);

    /// <summary>The Windows settings page that grants microphone access.</summary>
    public const string PrivacySettingsUri = "ms-settings:privacy-microphone";

    /// <summary>True when the failure is Windows refusing microphone access, not a device fault.</summary>
    public static bool IsAccessDenied(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException) return true;
            if (current.HResult == AccessDeniedHResult) return true;
        }
        return false;
    }

    /// <summary>
    /// A status line for a failed microphone start: actionable and specific when Windows denied
    /// access, otherwise the underlying device error.
    /// </summary>
    public static string DescribeFailure(Exception exception) =>
        IsAccessDenied(exception)
            ? "Microphone blocked for Muesli. Open Windows microphone privacy settings to allow it."
            : $"Could not start microphone: {exception.Message}";
}
