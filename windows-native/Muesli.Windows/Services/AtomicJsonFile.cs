using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Muesli.Windows.Services;

public sealed class AtomicJsonFile
{
    public const int CurrentSchemaVersion = 1;
    private const int IoAttemptCount = 5;
    private static readonly ConcurrentDictionary<string, object> FileLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private readonly Action<string>? _report;

    public AtomicJsonFile(Action<string>? report = null) => _report = report;

    public AtomicJsonLoadResult<T> Load<T>(string path, T fallback)
    {
        path = Path.GetFullPath(path);
        lock (FileLocks.GetOrAdd(path, static _ => new object()))
        {
            if (!File.Exists(path))
                return new(fallback, false, false, null);

            try
            {
                return new(DeserializeWithRetry<T>(path), false, false, null);
            }
            catch (JsonException)
            {
                var quarantinedPath = Quarantine(path);
                var backupPath = $"{path}.bak";
                if (File.Exists(backupPath))
                {
                    try
                    {
                        var recovered = DeserializeWithRetry<T>(backupPath);
                        RestoreBackup(backupPath, path);
                        var warning = $"Recovered {Path.GetFileName(path)} from backup. The unreadable original was preserved as {Path.GetFileName(quarantinedPath)}.";
                        _report?.Invoke(warning);
                        return new(recovered, true, true, warning);
                    }
                    catch (JsonException)
                    {
                        var backupQuarantine = Quarantine(backupPath);
                        var warning = $"Could not read {Path.GetFileName(path)} or its backup. They were preserved as {Path.GetFileName(quarantinedPath)} and {Path.GetFileName(backupQuarantine)}.";
                        _report?.Invoke(warning);
                        return new(fallback, false, true, warning);
                    }
                }

                var noBackupWarning = $"Could not read {Path.GetFileName(path)}. It was preserved as {Path.GetFileName(quarantinedPath)}; no backup was available.";
                _report?.Invoke(noBackupWarning);
                return new(fallback, false, true, noBackupWarning);
            }
        }
    }

    public void Save<T>(string path, T value, AtomicJsonSaveMode saveMode = AtomicJsonSaveMode.Recoverable)
    {
        path = Path.GetFullPath(path);
        lock (FileLocks.GetOrAdd(path, static _ => new object()))
        {
            var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The JSON path has no parent directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                var envelope = new AtomicJsonEnvelope<T>(CurrentSchemaVersion, value);
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOptions));
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                if (saveMode == AtomicJsonSaveMode.PrivacySensitive)
                    PurgeOlderArtifacts(path, temporaryPath);

                if (File.Exists(path))
                    File.Replace(temporaryPath, path, saveMode == AtomicJsonSaveMode.Recoverable ? $"{path}.bak" : null, ignoreMetadataErrors: true);
                else
                    File.Move(temporaryPath, path);
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
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, "data", out var data))
        {
            if (!TryGetProperty(root, "schemaVersion", out var schemaVersion) || !schemaVersion.TryGetInt32(out var version) || version < 1 || version > CurrentSchemaVersion)
                throw new JsonException("The persisted JSON has an unsupported schema version.");
            return data.Deserialize<T>(JsonOptions) ?? throw new JsonException("The persisted data payload was null.");
        }

        // Version 0 files stored the value directly. Keep them readable and migrate on save.
        return root.Deserialize<T>(JsonOptions) ?? throw new JsonException("The persisted JSON value was null.");
    }

    private static T DeserializeWithRetry<T>(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return Deserialize<T>(path); }
            catch (Exception exception) when (IsTransientIoException(exception) && attempt < IoAttemptCount) { Thread.Sleep(50 * attempt); }
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
        var quarantinePath = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileName(path)}.corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json");
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
        finally { TryDelete(temporaryPath); }
    }

    private static void PurgeOlderArtifacts(string path, string excludedPath)
    {
        var directory = Path.GetDirectoryName(path)!;
        var fileName = Path.GetFileName(path);
        foreach (var candidate in Directory.EnumerateFiles(directory))
        {
            if (Path.GetFullPath(candidate).Equals(Path.GetFullPath(excludedPath), StringComparison.OrdinalIgnoreCase) || !IsArtifact(Path.GetFileName(candidate), fileName))
                continue;
            DeleteWithRetry(candidate);
        }
    }

    private static bool IsArtifact(string candidateName, string fileName) =>
        candidateName.Equals($"{fileName}.bak", StringComparison.OrdinalIgnoreCase) ||
        (candidateName.StartsWith($".{fileName}.", StringComparison.OrdinalIgnoreCase) && candidateName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) ||
        (candidateName.StartsWith($"{fileName}.", StringComparison.OrdinalIgnoreCase) && (candidateName.EndsWith(".recovery.tmp", StringComparison.OrdinalIgnoreCase) || candidateName.Contains(".corrupt-", StringComparison.OrdinalIgnoreCase)));

    private static bool IsTransientIoException(Exception exception) => exception is IOException or UnauthorizedAccessException;

    private static void DeleteWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (Exception exception) when (IsTransientIoException(exception) && attempt < IoAttemptCount) { Thread.Sleep(50 * attempt); }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* A stale temp file is safer than losing the destination. */ }
    }
}

public enum AtomicJsonSaveMode { Recoverable, PrivacySensitive }
public sealed record AtomicJsonLoadResult<T>(T Value, bool RecoveredFromBackup, bool HadCorruption, string? Warning);
public sealed record AtomicJsonEnvelope<T>(int SchemaVersion, T Data);
