namespace Muesli.Windows.Services;

/// <summary>
/// Per-field checks for values typed into Settings. Each returns a short message for the user, or
/// <c>null</c> when the value can be saved. Settings save as they are edited, so a value the
/// store would silently replace with a default is refused here instead.
/// </summary>
public static class SettingsFieldValidation
{
    /// <summary>An absolute http or https address.</summary>
    public static string? HttpEndpoint(string? value)
    {
        var candidate = value?.Trim() ?? "";
        return Uri.TryCreate(candidate, UriKind.Absolute, out var endpoint) &&
               (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps) &&
               endpoint.Host.Length > 0
            ? null
            : "Enter an address that starts with http:// or https://.";
    }

    /// <summary>An http address on this machine's loopback interface.</summary>
    public static string? LoopbackEndpoint(string? value)
    {
        var candidate = value?.Trim() ?? "";
        return Uri.TryCreate(candidate, UriKind.Absolute, out var endpoint) &&
               endpoint.Scheme == Uri.UriSchemeHttp &&
               endpoint.IsLoopback
            ? null
            : "Enter an http:// address on this PC, such as http://127.0.0.1:9222.";
    }

    /// <summary>Empty, or a full folder path on this PC. The folder does not have to exist yet.</summary>
    public static string? ExportDirectory(string? value)
    {
        var candidate = value?.Trim() ?? "";
        if (candidate.Length == 0) return null;
        return IsFullPath(candidate) ? null : "Enter a full folder path, such as C:\\Notes.";
    }

    /// <summary>Empty, or the full path of an existing .exe.</summary>
    public static string? HookExecutable(string? value)
    {
        var candidate = value?.Trim() ?? "";
        if (candidate.Length == 0) return null;
        if (!IsFullPath(candidate) ||
            !string.Equals(Path.GetExtension(candidate), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return "Enter the full path to an .exe file.";
        }

        return File.Exists(candidate) ? null : "No program was found at that path.";
    }

    private static bool IsFullPath(string candidate) =>
        candidate.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(candidate);
}
