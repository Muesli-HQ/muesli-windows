using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Muesli.Windows.Services.Interop;

/// <summary>Status codes returned by the shared Swift dictation bridge.</summary>
public enum SwiftDictationErrorCode
{
    Ok = 0,
    InvalidArgument = 1,
    InvalidHandle = 2,
    InvalidUtf8 = 3,
    InvalidJson = 4,
    BufferTooSmall = 5,
    StoreError = 6,
    IoError = 7,
    UnsupportedVersion = 8,
    Unknown = 255,
}

/// <summary>Domain-level failure carrying the native error code and diagnostic message.</summary>
public sealed class SwiftDictationBridgeException : Exception
{
    public SwiftDictationBridgeException(SwiftDictationErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public SwiftDictationErrorCode Code { get; }
}

/// <summary>The fields the shared Swift <c>DictationStore</c> supports for an insert.</summary>
public sealed record SharedDictationInsert(
    string Text,
    double DurationSeconds,
    string AppContext = "",
    string Source = "dictation",
    string? TargetAppName = null,
    string? TargetAppBundleId = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? EndedAt = null);

/// <summary>
/// Explicit, co-located database location for the shared core. Deliberately a separate file from the
/// Windows <c>muesli.db</c>: the shared Swift schema and the Windows SQLite schema are not
/// interchangeable (see the persistence compatibility table in the interop README).
/// </summary>
public static class SharedDictationCorePaths
{
    public const string DatabaseFileName = "muesli-core.db";

    public static string DatabasePathFor(string dataDirectory) =>
        Path.Combine(Path.GetFullPath(dataDirectory), DatabaseFileName);
}

/// <summary>
/// Narrow public adapter over the shared Swift dictation ABI: insert one dictation and read recent
/// dictations. No native handle, pointer or JSON escapes this type.
/// </summary>
public sealed class SharedDictationStore : IDisposable
{
    public const uint ExpectedAbiVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly SwiftDictationStoreHandle _handle;
    private readonly Action<string>? _log;
    private bool _disposed;

    public SharedDictationStore(string databasePath, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        _log = log;

        var abiVersion = SwiftDictationCoreNative.muesli_core_bridge_abi_version();
        if (abiVersion != ExpectedAbiVersion)
        {
            throw new SwiftDictationBridgeException(
                SwiftDictationErrorCode.UnsupportedVersion,
                $"Shared Swift dictation bridge ABI {abiVersion} is not supported; expected {ExpectedAbiVersion}.");
        }

        var pathBytes = Encoding.UTF8.GetBytes(DatabasePath);
        var code = SwiftDictationCoreNative.muesli_core_bridge_open(pathBytes, pathBytes.Length, out var pointer);
        if (code != (int)SwiftDictationErrorCode.Ok || pointer == IntPtr.Zero)
        {
            throw BuildException(code, "open");
        }

        _handle = new SwiftDictationStoreHandle();
        _handle.Set(pointer);
        _log?.Invoke($"Shared Swift dictation backend active: bridge ABI {abiVersion}, database '{DatabasePath}'.");
    }

    public string DatabasePath { get; }

    /// <summary>Inserts one dictation and returns the shared-core row id.</summary>
    public long Insert(SharedDictationInsert insert)
    {
        ArgumentNullException.ThrowIfNull(insert);
        ThrowIfDisposed();

        var request = new InsertRequestDto(
            insert.Text,
            insert.DurationSeconds,
            insert.AppContext,
            insert.Source,
            insert.TargetAppName,
            insert.TargetAppBundleId,
            FormatInstant(insert.StartedAt),
            FormatInstant(insert.EndedAt));
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOptions));
        var code = SwiftDictationCoreNative.muesli_core_bridge_insert_dictation(
            _handle, bytes, bytes.Length, out var id);
        if (code != (int)SwiftDictationErrorCode.Ok)
        {
            throw BuildException(code, "insert");
        }

        return id;
    }

    /// <summary>Reads up to <paramref name="limit"/> most recent dictations, newest first.</summary>
    public IReadOnlyList<PersistedDictation> Recent(int limit)
    {
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be at least 1.");
        }

        ThrowIfDisposed();

        var probe = SwiftDictationCoreNative.muesli_core_bridge_recent_dictations(_handle, limit, null, 0, out var required);
        if (probe != (int)SwiftDictationErrorCode.BufferTooSmall)
        {
            throw BuildException(probe, "recent");
        }

        // A concurrent insert can grow the response between the capacity probe and the read; the
        // bridge reports the new required size, so retry with it rather than failing the caller.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (required <= 0)
            {
                return Array.Empty<PersistedDictation>();
            }

            var buffer = new byte[required];
            var code = SwiftDictationCoreNative.muesli_core_bridge_recent_dictations(
                _handle, limit, buffer, buffer.Length, out var written);
            if (code == (int)SwiftDictationErrorCode.BufferTooSmall)
            {
                required = written;
                continue;
            }

            if (code != (int)SwiftDictationErrorCode.Ok)
            {
                throw BuildException(code, "recent");
            }

            var response = JsonSerializer.Deserialize<RecentResponseDto>(buffer.AsSpan(0, written), JsonOptions)
                ?? throw new SwiftDictationBridgeException(
                    SwiftDictationErrorCode.InvalidJson, "Shared Swift bridge returned an unreadable response.");

            var results = new List<PersistedDictation>(response.Records.Length);
            foreach (var record in response.Records)
            {
                results.Add(new PersistedDictation(
                    record.Id.ToString(CultureInfo.InvariantCulture),
                    ParseTimestamp(record.Timestamp),
                    record.RawText,
                    (int)Math.Round((record.DurationSeconds ?? 0) * 1000d),
                    // ModelProfile is a Windows-only field with no shared-core column; the shared
                    // schema cannot represent it yet (documented compatibility gap).
                    ModelProfile: ""));
            }

            return results;
        }

        throw new SwiftDictationBridgeException(
            SwiftDictationErrorCode.BufferTooSmall, "Shared Swift history kept changing during read.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
        _log?.Invoke("Shared Swift dictation backend closed.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static string? FormatInstant(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static DateTime ParseTimestamp(string value) =>
        DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : DateTime.UnixEpoch;

    private static SwiftDictationBridgeException BuildException(int code, string operation)
    {
        var message = $"Shared Swift dictation bridge '{operation}' failed with code {code}.";
        string? detail = null;
        try
        {
            var probe = SwiftDictationCoreNative.muesli_core_bridge_last_error(null, 0, out var required);
            if (probe == (int)SwiftDictationErrorCode.BufferTooSmall && required > 0)
            {
                var buffer = new byte[required];
                if (SwiftDictationCoreNative.muesli_core_bridge_last_error(buffer, buffer.Length, out var written)
                    == (int)SwiftDictationErrorCode.Ok)
                {
                    var dto = JsonSerializer.Deserialize<LastErrorDto>(buffer.AsSpan(0, written), JsonOptions);
                    detail = dto?.Message;
                }
            }
        }
        catch
        {
            // Diagnostics must never mask the original failure.
        }

        return new SwiftDictationBridgeException((SwiftDictationErrorCode)code, message + (detail is null ? "" : $" {detail}"));
    }

    private sealed record InsertRequestDto(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("durationSeconds")] double DurationSeconds,
        [property: JsonPropertyName("appContext")] string AppContext,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("targetAppName")] string? TargetAppName,
        [property: JsonPropertyName("targetAppBundleId")] string? TargetAppBundleId,
        [property: JsonPropertyName("startedAt")] string? StartedAt,
        [property: JsonPropertyName("endedAt")] string? EndedAt);

    private sealed record RecordDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("timestamp")] string Timestamp,
        [property: JsonPropertyName("durationSeconds")] double? DurationSeconds,
        [property: JsonPropertyName("rawText")] string RawText,
        [property: JsonPropertyName("appContext")] string AppContext,
        [property: JsonPropertyName("wordCount")] int WordCount,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("targetAppName")] string? TargetAppName,
        [property: JsonPropertyName("targetAppBundleId")] string? TargetAppBundleId);

    private sealed record RecentResponseDto(
        [property: JsonPropertyName("records")] RecordDto[] Records);

    private sealed record LastErrorDto(
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("message")] string Message);
}
