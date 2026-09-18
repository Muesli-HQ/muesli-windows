using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

public sealed class AppLogService
{
    private const long MaximumRetainedBytes = 25L * 1024 * 1024;
    private static readonly object WriteGate = new();

    public AppLogService()
    {
        ApplyRetention();
    }

    /// <summary>
    /// Resolved on each access so an isolated <c>MUESLI_PROFILE_ROOT</c> set at process start is
    /// honoured. Capturing the path in the constructor wrote preview logs into the default
    /// profile whenever the host constructed logging before applying the override.
    /// </summary>
    public string LogDirectory =>
        Muesli.Windows.Core.Profiles.MuesliProfilePaths.Current().LogDirectory;

    public string CurrentLogPath => Path.Combine(LogDirectory, $"muesli-{DateTime.Now:yyyy-MM-dd}.log");

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception? exception = null)
    {
        var detail = exception is null ? message : $"{message}{Environment.NewLine}{exception}";
        Write("ERROR", detail);
    }

    public void OpenLogDirectory()
    {
        Directory.CreateDirectory(LogDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = LogDirectory,
            UseShellExecute = true
        });
    }

    private void Write(string level, string message)
    {
        try
        {
            lock (WriteGate)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(
                    CurrentLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {level} {Redact(message)}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break dictation, startup, or shutdown.
        }
    }

    public static string Redact(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        var redacted = Regex.Replace(
            message,
            @"https?://[^\s|;]+",
            "[url-redacted]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        redacted = Regex.Replace(
            redacted,
            @"\b[a-z]{3}-[a-z]{4}-[a-z]{3}\b",
            "[meeting-code-redacted]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        redacted = Regex.Replace(
            redacted,
            @"(?<=targetWindowTitle=)[^;\r\n]*",
            "[title-redacted]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return redacted;
    }

    public static string SensitiveTextFingerprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    public static IReadOnlyList<string> SelectLogsForDeletion(
        IEnumerable<LogRetentionEntry> entries,
        DateTimeOffset now,
        TimeSpan maximumAge,
        long maximumBytes)
    {
        var files = entries.OrderByDescending(entry => entry.LastWriteUtc).ToList();
        var delete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Where(file => now - file.LastWriteUtc > maximumAge))
        {
            delete.Add(file.Path);
        }

        long retainedBytes = 0;
        foreach (var file in files)
        {
            if (delete.Contains(file.Path))
            {
                continue;
            }
            retainedBytes += file.Length;
            if (retainedBytes > maximumBytes)
            {
                delete.Add(file.Path);
            }
        }
        return delete.ToList();
    }

    private void ApplyRetention()
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
            {
                return;
            }
            var entries = Directory.EnumerateFiles(LogDirectory, "muesli-*.log", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Select(file => new LogRetentionEntry(file.FullName, file.Length, file.LastWriteTimeUtc))
                .ToList();
            foreach (var path in SelectLogsForDeletion(entries, DateTimeOffset.UtcNow, TimeSpan.FromDays(14), MaximumRetainedBytes))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Retention is best-effort and can never block startup or logging.
        }
    }
}

public sealed record LogRetentionEntry(string Path, long Length, DateTimeOffset LastWriteUtc);
