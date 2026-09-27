using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

/// <summary>
/// Builds a local-only support bundle for troubleshooting (launch DIAG-01 / L08). The bundle is a
/// single redacted text document assembled from environment identity, model/runtime status, audio
/// device categories, and a bounded slice of the application log. It never transmits anything and
/// never includes transcripts, meeting or window titles, credentials, raw audio, or Computer Use
/// values. Windows remains the only writer; this type is deliberately UI-free and file-system only
/// so the privacy contract is unit-testable.
/// </summary>
public sealed class SupportBundleService
{
    public const string BundleFormatVersion = "1";
    public const int DefaultLogByteBudget = 256 * 1024;
    public const int DefaultLogLineBudget = 400;
    public const int MaximumSampleExcerptLength = 180;
    public const int MaximumIncidentCategories = 20;
    public const string Unavailable = "unavailable";

    public SupportBundle Build(
        SupportBundleRequest request,
        string logDirectory,
        DateTimeOffset generatedAt,
        int logByteBudget = DefaultLogByteBudget,
        int logLineBudget = DefaultLogLineBudget)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        var capture = CaptureLogs(logDirectory, Math.Max(0, logByteBudget), Math.Max(0, logLineBudget));
        var incidents = SupportBundleIncidents.Classify(capture.Lines);
        var text = Render(request, capture, incidents, generatedAt);
        return new SupportBundle(text, incidents, capture.Lines.Count, capture.BytesRead);
    }

    private static SupportBundleLogCapture CaptureLogs(string logDirectory, int byteBudget, int lineBudget)
    {
        if (byteBudget == 0 || lineBudget == 0 || !Directory.Exists(logDirectory))
        {
            return SupportBundleLogCapture.Empty;
        }

        List<string> newestFirst;
        try
        {
            newestFirst = Directory
                .EnumerateFiles(logDirectory, "muesli-*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SupportBundleLogCapture.Empty;
        }

        var lines = new List<string>();
        long bytesRead = 0;
        var remaining = byteBudget;
        foreach (var path in newestFirst)
        {
            if (remaining <= 0)
            {
                break;
            }

            var chunk = TryReadTail(path, remaining);
            if (chunk is null)
            {
                continue;
            }

            bytesRead += chunk.BytesRead;
            remaining -= (int)chunk.BytesRead;
            var chunkLines = chunk.Text.Split('\n');
            if (!chunk.StartedAtFileStart && chunkLines.Length > 0)
            {
                // The first line is a fragment because the tail read began mid-line.
                chunkLines = chunkLines[1..];
            }

            foreach (var line in chunkLines)
            {
                lines.Add(line.TrimEnd('\r'));
            }
        }

        if (lines.Count > lineBudget)
        {
            lines = lines[^lineBudget..];
        }

        // Logs are chronological newest-last; hand the caller the bounded slice in reading order.
        return new SupportBundleLogCapture(lines, bytesRead);
    }

    private static SupportBundleLogChunk? TryReadTail(string path, int maximumBytes)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var take = (int)Math.Min(length, maximumBytes);
            var offset = length - take;
            stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[take];
            var read = stream.Read(buffer, 0, take);
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            return new SupportBundleLogChunk(text, read, offset == 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A locked or unreadable log must never fail the export. Skip it and continue.
            return null;
        }
    }

    private static string Render(
        SupportBundleRequest request,
        SupportBundleLogCapture capture,
        IReadOnlyList<SupportBundleIncident> incidents,
        DateTimeOffset generatedAt)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Muesli support bundle");
        builder.AppendLine($"Generated: {generatedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"Bundle format: {BundleFormatVersion}");
        builder.AppendLine("Local-only file. Muesli never transmits this bundle.");
        builder.AppendLine();

        builder.AppendLine("== Environment ==");
        builder.AppendLine($"App version: {Value(request.Environment.AppVersion)}");
        builder.AppendLine($"Runtime: {Value(request.Environment.RuntimeVersion)}");
        builder.AppendLine($"Operating system: {Value(request.Environment.OperatingSystem)}");
        builder.AppendLine($"Process architecture: {Value(request.Environment.ProcessArchitecture)}");
        builder.AppendLine($"Package identity: {Value(request.Environment.PackageIdentity)}");
        builder.AppendLine($"Theme: {Value(request.Settings.Theme)}");
        builder.AppendLine($"Summary provider: {Value(request.Settings.MeetingSummaryProvider)}");
        builder.AppendLine($"Crash reporting enabled: {YesNo(request.Settings.CrashReportingEnabled)}");
        builder.AppendLine();

        builder.AppendLine("== Model roles ==");
        builder.AppendLine($"Dictation model: {Value(request.Settings.DictationModelId)}");
        builder.AppendLine($"Final meeting/import model: {Value(request.Settings.FinalMeetingModelId)}");
        builder.AppendLine($"Live meeting model: {Value(request.Settings.LiveMeetingModelId)}");
        builder.AppendLine($"Local cleanup enabled: {YesNo(request.Settings.LocalCleanupEnabled)}");
        builder.AppendLine();

        builder.AppendLine("== Provider configuration ==");
        builder.AppendLine($"OpenAI key configured: {YesNo(request.Settings.OpenAiKeyConfigured)}");
        builder.AppendLine($"OpenRouter key configured: {YesNo(request.Settings.OpenRouterKeyConfigured)}");
        builder.AppendLine($"Ollama configured: {YesNo(request.Settings.OllamaConfigured)}");
        builder.AppendLine("Keys, tokens, and endpoints are never included.");
        builder.AppendLine();

        builder.AppendLine("== Audio devices ==");
        builder.AppendLine($"Active input devices: {request.Audio.InputDeviceCount}");
        builder.AppendLine($"Active output devices: {request.Audio.OutputDeviceCount}");
        builder.AppendLine();

        builder.AppendLine("== Model and runtime ==");
        builder.AppendLine(SupportBundleRedaction.Redact(request.Runtime?.Detail ?? "Runtime diagnostics unavailable."));
        builder.AppendLine();

        builder.AppendLine("== Recent incidents ==");
        if (incidents.Count == 0)
        {
            builder.AppendLine("No incidents in the captured log window.");
        }
        else
        {
            foreach (var incident in incidents)
            {
                var last = incident.LastOccurredUtc is { } timestamp
                    ? timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    : "unknown";
                builder.AppendLine($"- {incident.Category}: {incident.Count} occurrence(s), last {last}");
                if (!string.IsNullOrWhiteSpace(incident.SampleExcerpt))
                {
                    builder.AppendLine($"    sample: {incident.SampleExcerpt}");
                }
            }
        }
        builder.AppendLine();

        builder.AppendLine("== Recent logs (redacted, bounded) ==");
        if (capture.Lines.Count == 0)
        {
            builder.AppendLine("No log lines available.");
        }
        else
        {
            foreach (var line in capture.Lines)
            {
                builder.AppendLine(SupportBundleRedaction.Redact(line));
            }
        }

        return builder.ToString();
    }

    private static string Value(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Unavailable : value.Trim();

    private static string YesNo(bool value) => value ? "yes" : "no";
}

public sealed record SupportBundleEnvironment(
    string AppVersion,
    string RuntimeVersion,
    string OperatingSystem,
    string ProcessArchitecture,
    string PackageIdentity);

public sealed record SupportBundleAudioSummary(int InputDeviceCount, int OutputDeviceCount)
{
    public static SupportBundleAudioSummary Unavailable { get; } = new(0, 0);
}

public sealed record SupportBundleSettingsSummary(
    string Theme,
    string DictationModelId,
    string FinalMeetingModelId,
    string? LiveMeetingModelId,
    bool LocalCleanupEnabled,
    string MeetingSummaryProvider,
    bool CrashReportingEnabled,
    bool OpenAiKeyConfigured,
    bool OpenRouterKeyConfigured,
    bool OllamaConfigured);

public sealed record SupportBundleRequest(
    SupportBundleEnvironment Environment,
    SupportBundleAudioSummary Audio,
    SupportBundleSettingsSummary Settings,
    RuntimeDiagnostics? Runtime);

public sealed record SupportBundleIncident(
    string Category,
    int Count,
    DateTimeOffset? LastOccurredUtc,
    string SampleExcerpt);

public sealed record SupportBundle(
    string Text,
    IReadOnlyList<SupportBundleIncident> Incidents,
    int LogLinesIncluded,
    long LogBytesIncluded);

internal sealed record SupportBundleLogChunk(string Text, long BytesRead, bool StartedAtFileStart);

internal sealed record SupportBundleLogCapture(IReadOnlyList<string> Lines, long BytesRead)
{
    public static SupportBundleLogCapture Empty { get; } = new([], 0);
}

/// <summary>
/// Classifies redacted <c>ERROR</c> log lines into coarse, privacy-safe incident categories. Only
/// the category, count, timestamp, and a redacted excerpt are surfaced; this is not a second log.
/// </summary>
internal static class SupportBundleIncidents
{
    private static readonly (string Category, string[] Markers)[] Rules =
    {
        ("Unhandled UI exception", ["Unhandled UI exception", "Unhandled WinUI exception"]),
        ("XAML parse", ["XamlParseException", "XAML parse"]),
        ("Transcription", ["transcription", "sherpa", "recognizer", "onnx", "asr"]),
        ("Audio capture", ["capture", "microphone", "loopback", "wave in", "waveout", "audio"]),
        ("Clipboard or paste", ["clipboard", "paste", "insert at cursor"]),
        ("Model lifecycle", ["model", "download", "checksum", "verify", "install"]),
        ("Summary or provider", ["summary", "openai", "openrouter", "ollama", "provider"]),
        ("Update or package", ["update", "installer", "msix", "package"]),
        ("Meeting detection", ["detection", "detected", "notification"]),
        ("Persistence", ["sqlite", "database", "persist", "migration", "journal"])
    };

    public static IReadOnlyList<SupportBundleIncident> Classify(IReadOnlyList<string> lines)
    {
        var accumulators = new Dictionary<string, IncidentAccumulator>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (!TryParseErrorLine(line, out var timestamp, out var message))
            {
                continue;
            }

            var category = ClassifyCategory(message);
            if (!accumulators.TryGetValue(category, out var accumulator))
            {
                accumulator = new IncidentAccumulator();
                accumulators[category] = accumulator;
            }

            accumulator.Count++;
            if (timestamp is { } parsed && (accumulator.Last is null || parsed > accumulator.Last))
            {
                accumulator.Last = parsed;
            }

            if (string.IsNullOrEmpty(accumulator.Excerpt))
            {
                accumulator.Excerpt = SupportBundleRedaction.Truncate(
                    SupportBundleRedaction.Redact(message),
                    SupportBundleService.MaximumSampleExcerptLength);
            }
        }

        return accumulators
            .OrderByDescending(pair => pair.Value.Count)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(SupportBundleService.MaximumIncidentCategories)
            .Select(pair => new SupportBundleIncident(
                pair.Key,
                pair.Value.Count,
                pair.Value.Last,
                pair.Value.Excerpt))
            .ToList();
    }

    internal static string ClassifyCategory(string message)
    {
        foreach (var (category, markers) in Rules)
        {
            foreach (var marker in markers)
            {
                if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return category;
                }
            }
        }

        return "Application";
    }

    private static bool TryParseErrorLine(string line, out DateTimeOffset? timestamp, out string message)
    {
        timestamp = null;
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        // Expected shape: "[yyyy-MM-dd HH:mm:ss.fff] ERROR detail".
        var close = line.IndexOf(']');
        if (close <= 1 || close + 1 >= line.Length)
        {
            return false;
        }

        var bracket = line[1..close];
        if (DateTimeOffset.TryParseExact(
                bracket,
                "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var parsed))
        {
            timestamp = parsed;
        }

        var remainder = line[(close + 1)..].TrimStart();
        const string error = "ERROR";
        if (!remainder.StartsWith(error, StringComparison.Ordinal))
        {
            return false;
        }

        message = remainder[error.Length..].TrimStart();
        return true;
    }

    private sealed class IncidentAccumulator
    {
        public int Count { get; set; }
        public DateTimeOffset? Last { get; set; }
        public string Excerpt { get; set; } = string.Empty;
    }
}

/// <summary>
/// Redaction applied to everything the support bundle emits. Builds on
/// <see cref="AppLogService.Redact"/> (URLs, meeting codes, window titles) and additionally strips
/// credential-shaped tokens and local user-profile segments so a bundle can be shared for support.
/// </summary>
public static class SupportBundleRedaction
{
    private static readonly Regex SecretAssignment = new(
        @"(?<label>\b(?:api[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret|password|passwd|authorization)\b)\s*(?:[:=]\s*)?(?<value>[A-Za-z0-9\-_\./+=]{6,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex OpenAiStyleKey = new(
        @"\bsk-[A-Za-z0-9_\-]{6,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex BearerToken = new(
        @"\bBearer\s+[A-Za-z0-9\-_\./+=]{6,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WindowsUserSegment = new(
        @"(?<=[A-Za-z]:\\Users\\)[^\\\s""']+",
        RegexOptions.CultureInvariant);

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var redacted = AppLogService.Redact(value);
        redacted = OpenAiStyleKey.Replace(redacted, "[secret-redacted]");
        redacted = BearerToken.Replace(redacted, "[secret-redacted]");
        redacted = SecretAssignment.Replace(redacted, "${label}=[secret-redacted]");
        redacted = WindowsUserSegment.Replace(redacted, "<user>");
        return redacted;
    }

    public static string Truncate(string value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maximumLength)
        {
            return value;
        }

        return value[..maximumLength] + "…";
    }
}
