using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Muesli.Windows.Services;

public sealed class AtomicJsonFile
{
    public const int CurrentSchemaVersion = 1;
    private const int IoAttemptCount = 5;

    private static readonly ConcurrentDictionary<string, object> FileLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Action<string>? _report;
    private readonly Action<string>? _beforeReplace;

    public AtomicJsonFile(Action<string>? report = null, Action<string>? beforeReplace = null)
    {
        _report = report;
        _beforeReplace = beforeReplace;
    }

    public AtomicJsonLoadResult<T> Load<T>(string path, T fallback)
    {
        path = Path.GetFullPath(path);
        lock (FileLocks.GetOrAdd(path, static _ => new object()))
        {
            if (!File.Exists(path))
            {
                return new AtomicJsonLoadResult<T>(fallback, false, false, null);
            }

            try
            {
                return new AtomicJsonLoadResult<T>(DeserializeWithRetry<T>(path), false, false, null);
            }
            catch (JsonException)
            {
                var quarantinedPath = Quarantine(path);
                var backupPath = BackupPath(path);
                if (File.Exists(backupPath))
                {
                    try
                    {
                        var recovered = DeserializeWithRetry<T>(backupPath);
                        RestoreBackup(backupPath, path);
                        var warning =
                            $"Recovered {Path.GetFileName(path)} from its backup after preserving the unreadable file as {Path.GetFileName(quarantinedPath)}.";
                        _report?.Invoke(warning);
                        return new AtomicJsonLoadResult<T>(recovered, true, true, warning);
                    }
                    catch (JsonException)
                    {
                        var backupQuarantine = Quarantine(backupPath);
                        var warning =
                            $"Could not read {Path.GetFileName(path)} or its backup. Both were preserved as {Path.GetFileName(quarantinedPath)} and {Path.GetFileName(backupQuarantine)}.";
                        _report?.Invoke(warning);
                        return new AtomicJsonLoadResult<T>(fallback, false, true, warning);
                    }
                }

                var noBackupWarning =
                    $"Could not read {Path.GetFileName(path)}. The original was preserved as {Path.GetFileName(quarantinedPath)}; no backup was available.";
                _report?.Invoke(noBackupWarning);
                return new AtomicJsonLoadResult<T>(fallback, false, true, noBackupWarning);
            }
        }
    }

    public void Save<T>(
        string path,
        T value,
        AtomicJsonSaveMode saveMode = AtomicJsonSaveMode.Recoverable)
    {
        path = Path.GetFullPath(path);
        lock (FileLocks.GetOrAdd(path, static _ => new object()))
        {
            var directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("The JSON path has no parent directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            var backupPath = BackupPath(path);

            try
            {
                var envelope = new AtomicJsonEnvelope<T>(CurrentSchemaVersion, value);
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOptions));
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           4096,
                           FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                if (saveMode == AtomicJsonSaveMode.PrivacySensitive)
                {
                    // Do not commit the sanitized/deleted value unless every older artifact
                    // can be removed. If cleanup is temporarily blocked, the original file
                    // remains authoritative and the privacy-sensitive operation can retry.
                    PurgeArtifacts(path, temporaryPath);
                }

                _beforeReplace?.Invoke(path);
                if (File.Exists(path))
                {
                    var destinationBackupPath = saveMode == AtomicJsonSaveMode.Recoverable
                        ? backupPath
                        : null;
                    File.Replace(temporaryPath, path, destinationBackupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }

            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
    }

    private static T Deserialize<T>(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object &&
            TryGetProperty(root, "data", out var data))
        {
            if (!TryGetProperty(root, "schemaVersion", out var schemaVersion) ||
                !schemaVersion.TryGetInt32(out var version) ||
                version < 1 ||
                version > CurrentSchemaVersion)
            {
                throw new JsonException("The persisted JSON envelope has an unsupported schema version.");
            }

            return data.Deserialize<T>(JsonOptions)
                   ?? throw new JsonException("The persisted data payload was null.");
        }

        // Version 0 files were the value itself (arrays for history and an object for settings).
        return root.Deserialize<T>(JsonOptions)
               ?? throw new JsonException("The persisted JSON value was null.");
    }

    private static string BackupPath(string path) => $"{path}.bak";

    private static T DeserializeWithRetry<T>(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return Deserialize<T>(path);
            }
            catch (Exception exception) when (IsTransientIoException(exception) && attempt < IoAttemptCount)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string Quarantine(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var fileName = Path.GetFileName(path);
        var quarantinePath = Path.Combine(
            directory,
            $"{fileName}.corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json");
        File.Move(path, quarantinePath);
        return quarantinePath;
    }

    private static void RestoreBackup(string backupPath, string targetPath)
    {
        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.recovery.tmp";
        try
        {
            File.Copy(backupPath, temporaryPath, overwrite: false);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static bool IsTransientIoException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    private static void PurgeArtifacts(string path, string excludedPath)
    {
        var directory = Path.GetDirectoryName(path)!;
        var fileName = Path.GetFileName(path);
        var excludedFullPath = Path.GetFullPath(excludedPath);
        foreach (var artifactPath in Directory.EnumerateFiles(directory)
                     .Where(candidate =>
                         !Path.GetFullPath(candidate).Equals(excludedFullPath, StringComparison.OrdinalIgnoreCase) &&
                         IsArtifact(Path.GetFileName(candidate), fileName)))
        {
            DeleteWithRetry(artifactPath);
        }
    }

    private static bool IsArtifact(string candidateName, string fileName)
    {
        if (candidateName.Equals($"{fileName}.bak", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (candidateName.StartsWith($".{fileName}.", StringComparison.OrdinalIgnoreCase) &&
            candidateName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidateName.StartsWith($"{fileName}.", StringComparison.OrdinalIgnoreCase) &&
               (candidateName.EndsWith(".recovery.tmp", StringComparison.OrdinalIgnoreCase) ||
                candidateName.Contains(".corrupt-", StringComparison.OrdinalIgnoreCase));
    }

    private static void DeleteWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                return;
            }
            catch (Exception exception) when (IsTransientIoException(exception) && attempt < IoAttemptCount)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A stale same-directory temp file is safer than losing the destination.
        }
    }
}

public enum AtomicJsonSaveMode
{
    Recoverable,
    PrivacySensitive
}

public sealed record AtomicJsonLoadResult<T>(
    T Value,
    bool RecoveredFromBackup,
    bool HadCorruption,
    string? Warning);

public sealed record AtomicJsonEnvelope<T>(int SchemaVersion, T Data);
