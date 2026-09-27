using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Microsoft.Data.Sqlite;

namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// Startup-only JSON → SQLite cutover. Call from <c>FeatureServiceScope.CreateProduction</c> when
/// <see cref="PersistenceCutoverGate.IsEnabled"/> is true — never from FeatureRuntime.
/// </summary>
/// <remarks>
/// The composition root calls <see cref="EnsureMigrated"/> before it opens the SQLite adapter.
/// A failure is surfaced as a redacted startup error and does not fall back to JSON after the
/// enabled cutover was attempted.
/// </remarks>
public sealed class PersistenceCutover : IPersistenceCutover
{
    public const string JsonSnapshotDirectoryPrefix = "json-history-pre-sqlite-";
    /// <summary>
    /// The capture root is deliberately under the profile. Windows' normal AppData ACLs protect
    /// it from other users, unlike the machine-wide temporary directory. It is hidden because it
    /// is an implementation detail and can contain plaintext history until startup scavenging.
    /// </summary>
    public const string JsonCaptureStagingDirectoryName = ".muesli-json-cutover-staging";
    public const string JsonCaptureStagingDirectoryPrefix = "capture-";
    public const string DictionaryFileName = "windows-dictionary.json";

    private const string CaptureLockFileName = ".capture.lock";
    private const string ProfileCutoverLockFileName = ".muesli-json-cutover.lock";
    private const int DefaultCaptureAttemptCount = 3;

    private static readonly string[] HistoryFileNames =
    [
        JsonHistorySnapshotReader.DictationsFileName,
        JsonHistorySnapshotReader.MeetingsFileName,
        JsonHistorySnapshotReader.FoldersFileName,
        JsonHistorySnapshotReader.TemplatesFileName,
        DictionaryFileName
    ];

    private readonly string _jsonDirectory;
    private readonly string _databasePath;
    private readonly Action<string>? _report;
    private readonly PersistenceCutoverOptions _options;
    private readonly JsonToSqliteMigrationService _migration;

    public PersistenceCutover(
        string jsonDirectory,
        string? databasePath = null,
        Action<string>? report = null,
        PersistenceCutoverOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonDirectory);
        _jsonDirectory = Path.GetFullPath(jsonDirectory);
        _databasePath = Path.GetFullPath(databasePath ?? PersistencePaths.DatabasePathFor(jsonDirectory));
        _report = report;
        _options = options ?? new PersistenceCutoverOptions();
        _migration = new JsonToSqliteMigrationService(
            _jsonDirectory,
            _databasePath,
            SafeReport,
            ValidateDatabasePathBeforeOpen);
    }

    public string JsonDirectory => _jsonDirectory;

    public string DatabasePath => _databasePath;

    public JsonToSqliteMigrationResult EnsureMigrated()
    {
        FileStream? profileLock = null;
        try
        {
            try
            {
                // A terminated process cannot run Dispose. Scavenge only the profile-scoped capture
                // root while holding the same profile lock used by a live capture, so another Muesli
                // process cannot lose an in-progress snapshot.
                profileLock = AcquireProfileCutoverLock();
                CleanupStaleCaptureDirectories();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                const string failure = "The JSON history capture area could not be prepared, so nothing was imported.";
                // Keep the user-facing failure deliberately generic, but retain the exception
                // category and safe diagnostic in the startup log. This is essential for a
                // packaged host where the profile may be redirected and a modal warning alone
                // cannot explain which protected staging invariant rejected the path.
                SafeReport($"{failure} detail={exception.GetType().Name}: {exception.Message}");
                return Failed(failure);
            }

        bool databaseExistedBeforeRun;
        try
        {
            databaseExistedBeforeRun = ValidateDatabasePathBeforeOpen();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _ = exception;
            const string failure = "The SQLite history path could not be inspected safely, so nothing was imported.";
            SafeReport(failure);
            return Failed(failure);
        }

        if (databaseExistedBeforeRun)
        {
            try
            {
                if (!ValidateDatabasePathBeforeOpen())
                {
                    throw new IOException("The SQLite history disappeared before it could be opened.");
                }

                using var existing = MuesliDatabase.Open(_databasePath);
                var existingState = PersistenceCutoverState.Read(existing);
                if (existingState.Authority == PersistenceAuthority.Sqlite)
                {
                    // Once SQLite is authoritative, the retained JSON is a rollback snapshot, not
                    // an input. Do not read it, compare it, re-import it, or fall back to it after a
                    // restart when the snapshot has naturally diverged from live history.
                    return AlreadyAuthoritative(existing, existingState);
                }
            }
            catch (PersistenceSchemaException)
            {
                SafeReport("The SQLite history was written by a newer Muesli build and was not opened.");
                return Failed("The SQLite history was written by a newer Muesli build and was not opened.");
            }
            catch (Exception exception) when (exception is PersistenceException or SqliteException or IOException)
            {
                SafeReport("The SQLite history could not be opened, so nothing was imported.");
                return Failed("The SQLite history could not be opened, so nothing was imported.");
            }
        }

        CapturedJsonHistory captured;
        try
        {
            captured = CaptureJsonHistory(
                profileLock ?? throw new InvalidOperationException("The profile cutover lock was not acquired."));
            profileLock = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _ = exception;
            const string failure = "The JSON history could not be captured, so nothing was imported.";
            SafeReport(failure);
            return Failed(failure);
        }

        try
        {
            try
            {
                JsonHistorySnapshot snapshot;
                try
                {
                    snapshot = JsonHistorySnapshotReader.Read(captured.StagingDirectory);
                }
                catch (InvalidDataException exception)
                {
                    return Failed(SafeFailure(exception.Message));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _ = exception;
                    const string failure = "The captured JSON history could not be read, so nothing was imported.";
                    SafeReport(failure);
                    return Failed(failure);
                }

                var warnings = snapshot.Problems
            .Select(problem => SafeFailure(problem.Message))
            .ToList();
                if (snapshot.HasBlockingProblems)
                {
                    var blocking = snapshot.Problems.Where(problem => problem.Blocking).Select(problem => problem.Message);
                    return Failed(
                        SafeFailure(
                            "The JSON history could not be read in full, so nothing was imported. " +
                            string.Join(" ", blocking)),
                        warnings: warnings);
                }

                var plan = MigrationPlan.Build(snapshot, warnings);

                string? snapshotDirectory;
                try
                {
                    snapshotDirectory = SnapshotJsonHistory(captured.StagingDirectory);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _ = exception;
                    const string failure =
                        "The captured JSON history could not be retained, so nothing was imported.";
                    SafeReport(failure);
                    return Failed(failure, plan.Warnings.ToList()) with
                    {
                        SourceFingerprint = plan.Fingerprint,
                        Counts = plan.Counts
                    };
                }
                if (_options.FailAfterJsonSnapshot)
                {
                    return new JsonToSqliteMigrationResult
                    {
                        Outcome = JsonMigrationOutcome.Failed,
                        DatabasePath = _databasePath,
                        SourceFingerprint = plan.Fingerprint,
                        Counts = plan.Counts,
                        Warnings = plan.Warnings,
                        JsonSnapshotDirectory = snapshotDirectory,
                        Failure = "Cutover was stopped after the JSON snapshot and before import."
                    };
                }

                JsonToSqliteMigrationResult migrated;
                try
                {
                    migrated = _migration.Migrate(plan);
                }
                catch (Exception exception) when (exception is PersistenceException or SqliteException or IOException)
                {
                    if (!databaseExistedBeforeRun)
                    {
                        DeleteDatabaseAndSidecars();
                    }

                    return Failed(
                        "The JSON history was not imported because SQLite could not complete the cutover.",
                        warnings: plan.Warnings.ToList()) with
                    {
                        SourceFingerprint = plan.Fingerprint,
                        Counts = plan.Counts,
                        JsonSnapshotDirectory = snapshotDirectory
                    };
                }

                if (!migrated.Succeeded)
                {
                    if (!databaseExistedBeforeRun && migrated.CreatedDatabase)
                    {
                        DeleteDatabaseAndSidecars();
                    }

                    return migrated with
                    {
                        JsonSnapshotDirectory = snapshotDirectory ?? migrated.JsonSnapshotDirectory,
                        Warnings = migrated.Warnings.Select(SafeFailure).ToList(),
                        Failure = SafeFailure(migrated.Failure ?? "The JSON history was not imported.")
                    };
                }

                // JsonToSqliteMigrationService verifies all normalized rows before returning. The state
                // transition is the next transaction, so a crash before this point leaves authority at
                // JSON and the next startup can safely retry against the retained source.
                try
                {
                    _options.BeforeAuthorityCommit?.Invoke();
                    // A zero-row history legitimately creates its SQLite file during this open;
                    // the validator still rejects directories, reparse points, and unsafe parents.
                    ValidateDatabasePathBeforeOpen();

                    using var database = MuesliDatabase.Open(_databasePath);
                    var state = PersistenceCutoverState.Read(database);
                    if (state.Authority == PersistenceAuthority.Sqlite)
                    {
                        return AlreadyAuthoritative(database, state);
                    }

                    PersistenceCutoverState.SetSqliteAuthority(
                        database,
                        plan.Fingerprint,
                        migrated.BackupPath);
                }
                catch (Exception exception) when (exception is PersistenceException or SqliteException or IOException)
                {
                    if (!databaseExistedBeforeRun)
                    {
                        DeleteDatabaseAndSidecars();
                    }

                    return Failed(
                        "The JSON history was imported but SQLite authority could not be committed; the cutover was not activated.",
                        warnings: plan.Warnings.ToList()) with
                    {
                        SourceFingerprint = plan.Fingerprint,
                        Counts = plan.Counts,
                        JsonSnapshotDirectory = snapshotDirectory
                    };
                }

                return migrated with
                {
                    Outcome = JsonMigrationOutcome.Migrated,
                    CreatedDatabase = migrated.CreatedDatabase || !databaseExistedBeforeRun,
                    SourceFingerprint = plan.Fingerprint,
                    Counts = plan.Counts,
                    Warnings = migrated.Warnings.Select(SafeFailure).ToList(),
                    JsonSnapshotDirectory = snapshotDirectory ?? migrated.JsonSnapshotDirectory
                };
            }
            finally
            {
                captured.Dispose();
            }
        }
        catch (CaptureCleanupException)
        {
            const string failure = "The captured JSON history could not be cleaned up safely, so the cutover was stopped.";
            SafeReport(failure);
            return Failed(failure);
        }
        catch (UnsafePathException)
        {
            const string failure = "The captured JSON history path was unsafe, so the cutover was stopped.";
            SafeReport(failure);
            return Failed(failure);
        }
    }
    finally
    {
        profileLock?.Dispose();
    }
    }

    public void Rollback(JsonToSqliteMigrationResult result)
    {
        if (result.Outcome != JsonMigrationOutcome.Migrated)
        {
            SafeReport("Rollback left JSON and the database as they were after the failed or no-op cutover.");
            return;
        }

        using var profileLock = AcquireProfileCutoverLock();
        EnsureNoReparseAncestors(_databasePath);
        var databaseKind = GetPathKind(_databasePath);
        if (databaseKind == PathKind.ReparsePoint || databaseKind == PathKind.Directory)
        {
            throw new PersistenceException("The SQLite history path is not a regular file.");
        }

        if (databaseKind == PathKind.RegularFile)
        {
            using var database = MuesliDatabase.Open(_databasePath);
            var state = PersistenceCutoverState.Read(database);
            if (state.Authority == PersistenceAuthority.Sqlite &&
                state.FirstPostCutoverWriteAtUtc is not null)
            {
                throw new PersistenceException(
                    "JSON rollback is unavailable after the first SQLite write; restore an explicit SQLite backup instead.");
            }
        }

        _migration.Rollback(result);
        SafeReport("Rolled back SQLite; JSON history files and the pre-sqlite snapshot were left in place.");
    }

    private JsonToSqliteMigrationResult Failed(string failure, IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Outcome = JsonMigrationOutcome.Failed,
            DatabasePath = _databasePath,
            Failure = failure,
            Warnings = warnings ?? []
        };

    private bool ValidateDatabasePathBeforeOpen()
    {
        EnsureNoReparseAncestors(_databasePath);
        var databaseKind = GetPathKind(_databasePath);
        if (databaseKind is PathKind.Directory or PathKind.ReparsePoint)
        {
            throw new IOException("The SQLite history path is not a regular file.");
        }

        return databaseKind == PathKind.RegularFile;
    }

    private CapturedJsonHistory CaptureJsonHistory(FileStream profileLock)
    {
        CleanupStaleCaptureDirectories();
        var attemptCount = _options.CaptureAttemptCount > 0
            ? _options.CaptureAttemptCount
            : DefaultCaptureAttemptCount;

        for (var attempt = 1; attempt <= attemptCount; attempt++)
        {
            var capture = CreateCaptureDirectory();
            try
            {
                _options.CaptureDirectoryCreated?.Invoke(capture.Directory);
                var before = ReadCaptureManifest(_jsonDirectory);
                foreach (var name in EnumerateCaptureFileNames())
                {
                    var expected = before[name];
                    CaptureFile(
                        Path.Combine(_jsonDirectory, name),
                        Path.Combine(capture.Directory, name),
                        expected);
                    _options.AfterCaptureFile?.Invoke(name);
                }

                var after = ReadCaptureManifest(_jsonDirectory);
                if (!CaptureManifestsEqual(before, after))
                {
                    throw new CaptureChangedException();
                }

                return new CapturedJsonHistory(
                    capture.Directory,
                    profileLock,
                    capture.Lock);
            }
            catch (CaptureChangedException exception) when (attempt < attemptCount)
            {
                _ = exception;
                capture.Dispose();
            }
            catch (IOException) when (attempt < attemptCount)
            {
                capture.Dispose();
            }
            catch
            {
                capture.Dispose();
                throw;
            }
        }

        throw new IOException(
            "The legacy JSON history changed while it was being captured; the cutover was stopped.");
    }

    private FileStream AcquireProfileCutoverLock()
    {
        // This lock is the inter-process cutover boundary for legitimate Muesli instances. A
        // same-user process that can replace profile paths already has the profile's ACL rights;
        // static reparse checks still fail closed, while this lock prevents app-process races.
        EnsureDirectoryPathSafe(_jsonDirectory, allowMissing: true);
        Directory.CreateDirectory(_jsonDirectory);
        EnsureDirectoryPathSafe(_jsonDirectory, allowMissing: false);
        var lockPath = Path.Combine(_jsonDirectory, ProfileCutoverLockFileName);
        EnsureNoReparseAncestors(lockPath);
        var lockKind = GetPathKind(lockPath);
        if (lockKind is PathKind.Directory or PathKind.ReparsePoint)
        {
            throw new IOException("The profile cutover lock is not a regular file.");
        }

        var stream = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            1,
            FileOptions.WriteThrough);
        try
        {
            EnsureHandleBoundToPath(stream.SafeFileHandle, lockPath, _jsonDirectory);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        TryMarkHidden(lockPath);
        return stream;
    }

    private void CleanupStaleCaptureDirectories()
    {
        var root = CaptureStagingRoot;
        EnsureDirectoryPathSafe(root, allowMissing: true);
        if (GetPathKind(root) == PathKind.Missing)
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(
                     root,
                     JsonCaptureStagingDirectoryPrefix + "*",
                     SearchOption.TopDirectoryOnly))
        {
            EnsureContainedDirectory(root, directory);
            if (CaptureDirectoryIsActive(directory))
            {
                continue;
            }

            DeleteDirectory(directory, root);
        }
    }

    private string CaptureStagingRoot =>
        Path.Combine(_jsonDirectory, JsonCaptureStagingDirectoryName);

    private enum PathKind
    {
        Missing,
        RegularFile,
        Directory,
        ReparsePoint
    }

    private static PathKind GetPathKind(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return PathKind.ReparsePoint;
            }

            return attributes.HasFlag(FileAttributes.Directory)
                ? PathKind.Directory
                : PathKind.RegularFile;
        }
        catch (FileNotFoundException)
        {
            return PathKind.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return PathKind.Missing;
        }
    }

    private static void EnsureNoReparseAncestors(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var current = fullPath;
        while (!string.IsNullOrEmpty(current))
        {
            var kind = GetPathKind(current);
            if (kind == PathKind.ReparsePoint)
            {
                throw new UnsafePathException($"The persistence path contains an unsupported reparse point: {Path.GetFileName(current)}.");
            }

            if (kind == PathKind.RegularFile &&
                !string.Equals(current, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnsafePathException("The persistence path contains a regular file where a directory was expected.");
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }
    }

    private static void EnsureDirectoryPathSafe(string path, bool allowMissing)
    {
        EnsureNoReparseAncestors(path);
        var kind = GetPathKind(path);
        if (kind == PathKind.Missing && allowMissing)
        {
            return;
        }

        if (kind != PathKind.Directory)
        {
            throw new IOException("The persistence staging path is not a regular directory.");
        }
    }

    private static void EnsureContainedDirectory(string parent, string child)
    {
        if (!IsContainedPath(parent, child))
        {
            throw new IOException("The persistence staging path escaped its profile directory.");
        }

        EnsureDirectoryPathSafe(child, allowMissing: false);
    }

    private static bool IsContainedPath(string parent, string child)
    {
        var fullParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        var fullChild = Path.GetFullPath(child);
        return fullChild.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureHandleBoundToPath(
        SafeFileHandle handle,
        string expectedPath,
        string profileRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var finalPath = NormalizeFinalPath(GetFinalPath(handle));
        var expected = Path.GetFullPath(expectedPath);
        if (!string.Equals(finalPath, expected, StringComparison.OrdinalIgnoreCase) ||
            !IsContainedPath(profileRoot, finalPath))
        {
            throw new UnsafePathException(
                $"An opened persistence handle resolved outside its expected profile path. " +
                $"expected={expected}; actual={finalPath}; profile={Path.GetFullPath(profileRoot)}.");
        }
    }

    private static SafeFileHandle OpenDirectoryNoFollow(string path, string profileRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("No-follow directory handles require Windows.");
        }

        var handle = CreateFile(
            path,
            FileReadAttributes,
            // Keep the directory from being renamed or replaced while its flat contents are
            // inspected. The handle is released immediately before Directory.Delete.
            FileShare.ReadWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"The staging directory could not be opened safely (Win32 error {error}).");
        }

        try
        {
            if (!GetFileInformationByHandle(handle, out var info))
            {
                throw new IOException("The staging directory handle could not be inspected safely.");
            }

            if ((info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
            {
                throw new UnsafePathException("Refusing to open a reparse-point staging directory.");
            }

            EnsureHandleBoundToPath(handle, path, profileRoot);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var capacity = 512u;
        while (capacity <= 32 * 1024)
        {
            var buffer = new StringBuilder((int)capacity);
            var length = GetFinalPathNameByHandle(handle, buffer, capacity, FileNameNormalized);
            if (length == 0)
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException($"The persistence handle path could not be verified (Win32 error {error}).");
            }

            if (length < capacity - 1)
            {
                return buffer.ToString();
            }

            capacity *= 2;
        }

        throw new IOException("The persistence handle path was too long to verify safely.");
    }

    private static string NormalizeFinalPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            path = @"\\" + path[7..];
        }
        else if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            path = path[4..];
        }

        return Path.GetFullPath(path);
    }

    private const uint FileReadAttributes = 0x00000080;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileNameNormalized = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    private static bool CaptureDirectoryIsActive(string directory)
    {
        var marker = Path.Combine(directory, CaptureLockFileName);
        var markerKind = GetPathKind(marker);
        if (markerKind == PathKind.Missing)
        {
            return false;
        }

        if (markerKind != PathKind.RegularFile)
        {
            throw new IOException("The capture marker is not a regular file.");
        }

        try
        {
            // A live capture opens the marker with FileShare.Read. An exclusive probe therefore
            // fails while another process still owns the staging directory. A stale marker can be
            // opened and the directory is safe to remove after this short probe is released.
            using var probe = new FileStream(
                marker,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.SequentialScan);
            EnsureHandleBoundToPath(probe.SafeFileHandle, marker, Path.GetDirectoryName(directory)!);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Unknown ownership is treated as live. A later startup can retry once access is
            // available; deleting a directory we cannot inspect would be unsafe.
            return true;
        }
    }

    private CaptureDirectory CreateCaptureDirectory()
    {
        EnsureDirectoryPathSafe(_jsonDirectory, allowMissing: false);
        EnsureDirectoryPathSafe(CaptureStagingRoot, allowMissing: true);
        Directory.CreateDirectory(CaptureStagingRoot);
        EnsureDirectoryPathSafe(CaptureStagingRoot, allowMissing: false);
        TryMarkHidden(CaptureStagingRoot);

        var directory = Path.Combine(
            CaptureStagingRoot,
            $"{JsonCaptureStagingDirectoryPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        EnsureContainedDirectory(CaptureStagingRoot, directory);
        TryMarkHidden(directory);

        try
        {
            var marker = Path.Combine(directory, CaptureLockFileName);
            EnsureNoReparseAncestors(marker);
            if (GetPathKind(marker) != PathKind.Missing)
            {
                throw new IOException("The capture marker already exists or is not a regular file.");
            }

            var stream = new FileStream(
                marker,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                1,
                FileOptions.WriteThrough);
            EnsureHandleBoundToPath(stream.SafeFileHandle, marker, CaptureStagingRoot);
            TryMarkHidden(marker);
            return new CaptureDirectory(directory, stream);
        }
        catch
        {
            DeleteDirectory(directory, CaptureStagingRoot);
            throw;
        }
    }

    private static IEnumerable<string> EnumerateCaptureFileNames()
    {
        foreach (var name in HistoryFileNames)
        {
            yield return name;
            yield return name + ".bak";
        }
    }

    private IReadOnlyDictionary<string, CaptureFileFingerprint> ReadCaptureManifest(
        string directory)
    {
        EnsureDirectoryPathSafe(directory, allowMissing: false);
        var manifest = new Dictionary<string, CaptureFileFingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in EnumerateCaptureFileNames())
        {
            var path = Path.Combine(directory, name);
            EnsureNoReparseAncestors(path);
            var kind = GetPathKind(path);
            if (kind == PathKind.Missing)
            {
                manifest[name] = CaptureFileFingerprint.Missing;
                continue;
            }

            if (kind != PathKind.RegularFile)
            {
                throw new IOException($"The JSON history input is not a regular file: {name}.");
            }

            var info = new FileInfo(path);
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.SequentialScan);
            EnsureHandleBoundToPath(stream.SafeFileHandle, path, _jsonDirectory);
            var hash = SHA256.HashData(stream);
            manifest[name] = new CaptureFileFingerprint(
                Exists: true,
                Length: stream.Length,
                LastWriteTimeUtc: info.LastWriteTimeUtc,
                Hash: Convert.ToHexString(hash));
        }

        return manifest;
    }

    private static bool CaptureManifestsEqual(
        IReadOnlyDictionary<string, CaptureFileFingerprint> left,
        IReadOnlyDictionary<string, CaptureFileFingerprint> right) =>
        EnumerateCaptureFileNames().All(name => left[name].Equals(right[name]));

    private void CaptureFile(
        string source,
        string destination,
        CaptureFileFingerprint expected)
    {
        EnsureNoReparseAncestors(source);
        var sourceKind = GetPathKind(source);
        if (!expected.Exists)
        {
            if (sourceKind == PathKind.Missing)
            {
                return;
            }

            if (sourceKind != PathKind.RegularFile)
            {
                throw new IOException("The JSON history input is not a regular file.");
            }

            throw new CaptureChangedException();
        }

        if (sourceKind == PathKind.Missing)
        {
            throw new CaptureChangedException();
        }

        if (sourceKind != PathKind.RegularFile)
        {
            throw new IOException("The JSON history input is not a regular file.");
        }

        var destinationDirectory = Path.GetDirectoryName(destination)!;
        EnsureDirectoryPathSafe(destinationDirectory, allowMissing: false);
        EnsureNoReparseAncestors(destination);
        if (GetPathKind(destination) != PathKind.Missing)
        {
            throw new IOException("The capture destination already exists or is not a regular file.");
        }

        using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        EnsureHandleBoundToPath(input.SafeFileHandle, source, _jsonDirectory);
        using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.SequentialScan | FileOptions.WriteThrough);
        EnsureHandleBoundToPath(output.SafeFileHandle, destination, CaptureStagingRoot);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        var length = 0L;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            digest.AppendData(buffer, 0, read);
            length += read;
        }

        output.Flush(flushToDisk: true);
        var hash = Convert.ToHexString(digest.GetHashAndReset());
        if (length != expected.Length || !string.Equals(hash, expected.Hash, StringComparison.Ordinal))
        {
            throw new CaptureChangedException();
        }

        File.SetLastWriteTimeUtc(destination, expected.LastWriteTimeUtc);
    }

    private string? SnapshotJsonHistory(string capturedDirectory)
    {
        EnsureContainedDirectory(CaptureStagingRoot, capturedDirectory);
        var sources = HistoryFileNames
            .SelectMany(name => new[]
            {
                Path.Combine(capturedDirectory, name),
                Path.Combine(capturedDirectory, name + ".bak")
            })
            .Where(path =>
            {
                var kind = GetPathKind(path);
                if (kind == PathKind.Missing)
                {
                    return false;
                }

                if (kind != PathKind.RegularFile)
                {
                    throw new IOException("The captured JSON history contains a non-regular file.");
                }

                EnsureNoReparseAncestors(path);
                return true;
            })
            .ToList();
        if (sources.Count == 0)
        {
            return ExistingSnapshotDirectory();
        }

        var directory = Path.Combine(
            _jsonDirectory,
            $"{JsonSnapshotDirectoryPrefix}{DateTime.UtcNow:yyyyMMddTHHmmssfff}Z");
        EnsureNoReparseAncestors(directory);
        if (GetPathKind(directory) != PathKind.Missing)
        {
            throw new IOException("The retained JSON snapshot destination already exists.");
        }

        Directory.CreateDirectory(directory);
        EnsureDirectoryPathSafe(directory, allowMissing: false);
        foreach (var source in sources)
        {
            var destination = Path.Combine(directory, Path.GetFileName(source));
            EnsureNoReparseAncestors(destination);
            if (GetPathKind(destination) != PathKind.Missing)
            {
                throw new IOException("The retained JSON snapshot destination already exists.");
            }

            CopyFileHandleBound(source, destination, _jsonDirectory);
        }

        SafeReport($"Kept a JSON history snapshot as {Path.GetFileName(directory)}.");
        return directory;
    }

    private static void CopyFileHandleBound(string source, string destination, string profileRoot)
    {
        using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        EnsureHandleBoundToPath(input.SafeFileHandle, source, profileRoot);
        using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.SequentialScan | FileOptions.WriteThrough);
        EnsureHandleBoundToPath(output.SafeFileHandle, destination, profileRoot);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private sealed class CaptureChangedException : IOException
    {
    }

    private sealed class CaptureCleanupException(string message, Exception innerException)
        : IOException(message, innerException)
    {
    }

    private sealed record CaptureFileFingerprint(
        bool Exists,
        long Length,
        DateTime LastWriteTimeUtc,
        string Hash)
    {
        public static CaptureFileFingerprint Missing { get; } =
            new(false, 0, DateTime.MinValue, string.Empty);
    }

    private sealed class CaptureDirectory(string directory, FileStream captureLock) : IDisposable
    {
        public string Directory { get; } = directory;

        public FileStream Lock { get; } = captureLock;

        public void Dispose()
        {
            Lock.Dispose();
            DeleteDirectory(Directory, Path.GetDirectoryName(Directory));
        }
    }

    private sealed class CapturedJsonHistory(
        string stagingDirectory,
        FileStream profileLock,
        FileStream captureLock) : IDisposable
    {
        public string StagingDirectory { get; } = stagingDirectory;

        public void Dispose()
        {
            Exception? cleanupFailure = null;
            try
            {
                captureLock.Dispose();
                DeleteDirectory(StagingDirectory, Path.GetDirectoryName(StagingDirectory));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                cleanupFailure = exception;
            }
            finally
            {
                try
                {
                    profileLock.Dispose();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    cleanupFailure ??= exception;
                }
            }

            if (cleanupFailure is not null)
            {
                throw new CaptureCleanupException(
                    "The profile-scoped capture cleanup could not complete safely.",
                    cleanupFailure);
            }
        }
    }

    private static void DeleteDirectory(string directory, string? expectedParent = null)
    {
        try
        {
            if (expectedParent is not null && !IsContainedPath(expectedParent, directory))
            {
                throw new UnsafePathException("Refusing to delete outside the profile staging directory.");
            }

            EnsureNoReparseAncestors(directory);
            var kind = GetPathKind(directory);
            if (kind == PathKind.Missing)
            {
                return;
            }

            if (kind == PathKind.ReparsePoint)
            {
                // Never recurse through a junction/symlink. The caller will fail closed, leaving
                // the suspicious entry for an operator to inspect rather than touching its target.
                throw new UnsafePathException("Refusing to delete a reparse-point staging directory.");
            }

            if (kind != PathKind.Directory)
            {
                throw new UnsafePathException("Refusing to delete a non-directory staging entry.");
            }

            DeleteDirectoryContentsNoFollow(
                directory,
                expectedParent ?? Path.GetDirectoryName(directory)!);

            // The no-delete-sharing handle is released by the flat-content pass before the
            // directory itself is removed. Revalidate the path after that hand-off so a
            // concurrent replacement is treated as unsafe rather than recursively followed.
            EnsureNoReparseAncestors(directory);
            if (GetPathKind(directory) != PathKind.Directory)
            {
                throw new UnsafePathException("The staging directory changed while it was being removed.");
            }

            Directory.Delete(directory, recursive: false);
        }
        catch (IOException exception) when (exception is not UnsafePathException)
        {
            // The staging copy is best-effort cleanup only; it is not user history and the next
            // cutover receives a fresh, uniquely named snapshot.
        }
        catch (UnauthorizedAccessException)
        {
            // See the IOException case above.
        }
    }

    private static void DeleteDirectoryContentsNoFollow(string directory, string profileRoot)
    {
        EnsureContainedDirectory(profileRoot, directory);
        using var directoryHandle = OpenDirectoryNoFollow(directory, profileRoot);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (!IsContainedPath(directory, entry))
            {
                throw new UnsafePathException("The staging directory contents escaped their parent.");
            }

            var kind = GetPathKind(entry);
            if (kind == PathKind.Missing)
            {
                continue;
            }

            if (kind == PathKind.ReparsePoint)
            {
                throw new UnsafePathException("Refusing to recurse through a reparse-point staging entry.");
            }

            if (kind == PathKind.Directory)
            {
                throw new UnsafePathException("Refusing to recurse through a nested staging directory.");
            }

            if (kind != PathKind.RegularFile)
            {
                throw new UnsafePathException("Refusing to delete a non-regular staging entry.");
            }

            EnsureNoReparseAncestors(entry);
            File.Delete(entry);
        }
    }

    private static void TryMarkHidden(string path)
    {
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch (IOException)
        {
            // The profile location and startup scavenger are the security boundary. Hidden is a
            // usability hardening and must not turn a valid cutover into a failure.
        }
        catch (UnauthorizedAccessException)
        {
            // See the IOException case above.
        }
    }

    private sealed class UnsafePathException(string message) : IOException(message)
    {
    }

    private string? ExistingSnapshotDirectory()
    {
        EnsureDirectoryPathSafe(_jsonDirectory, allowMissing: true);
        if (GetPathKind(_jsonDirectory) == PathKind.Missing)
        {
            return null;
        }

        var snapshots = new List<string>();
        foreach (var path in Directory.EnumerateDirectories(
                     _jsonDirectory,
                     JsonSnapshotDirectoryPrefix + "*",
                     SearchOption.TopDirectoryOnly))
        {
            EnsureContainedDirectory(_jsonDirectory, path);
            snapshots.Add(path);
        }

        return snapshots.OrderBy(path => path, StringComparer.Ordinal).LastOrDefault();
    }

    private JsonToSqliteMigrationResult AlreadyAuthoritative(
        MuesliDatabase database,
        PersistenceCutoverStateRecord state)
    {
        var counts = database.Read(connection => new MigrationCounts(
            Db.Command(connection, "SELECT count(*) FROM dictations;").ScalarInt32(),
            Db.Command(connection, "SELECT count(*) FROM meetings;").ScalarInt32(),
            Db.Command(connection, "SELECT count(*) FROM folders;").ScalarInt32(),
            Db.Command(connection, "SELECT count(*) FROM templates;").ScalarInt32()));
        return new JsonToSqliteMigrationResult
        {
            Outcome = JsonMigrationOutcome.AlreadyCurrent,
            DatabasePath = _databasePath,
            BackupPath = state.RetainedBackupPath,
            SourceFingerprint = state.SourceFingerprint,
            Counts = counts,
            JsonSnapshotDirectory = ExistingSnapshotDirectory()
        };
    }

    private void DeleteDatabaseAndSidecars()
    {
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            try
            {
                EnsureNoReparseAncestors(path);
                var kind = GetPathKind(path);
                if (kind == PathKind.Missing)
                {
                    continue;
                }

                if (kind != PathKind.RegularFile)
                {
                    SafeReport("A SQLite cleanup artifact was not a regular file and was left in place.");
                    continue;
                }

                File.Delete(path);
            }
            catch (IOException)
            {
                SafeReport("A SQLite cleanup artifact could not be removed and was left in place.");
            }
            catch (UnauthorizedAccessException)
            {
                SafeReport("A SQLite cleanup artifact could not be removed and was left in place.");
            }
        }
    }

    private void SafeReport(string message) => _report?.Invoke(Sanitize(message));

    private static string SafeFailure(string message) => Sanitize(message);

    /// <summary>
    /// Cutover messages may name files and opaque ids. They must not echo transcript, title, or path
    /// payloads from the history itself.
    /// </summary>
    internal static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        // Parser and provider messages occasionally quote the offending value. Keep the useful
        // diagnostic category but never echo a title, transcript, path, or opaque record id.
        return Regex.Replace(message, "(?<=['\\\"]).*?(?=['\\\"])", "[redacted]", RegexOptions.CultureInvariant);
    }
}
