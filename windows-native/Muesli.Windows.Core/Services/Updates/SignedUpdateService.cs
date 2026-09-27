using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace Muesli.Windows.Services;

public enum UpdateAvailability
{
    Disabled,
    UpToDate,
    UpdateAvailable,
    DowngradeRejected,
    UnsupportedClient,
    VerificationFailed,
    Offline,
    Cancelled,
    Error
}

public sealed record UpdateCheckResult(
    UpdateAvailability Availability,
    string? Message,
    string? Version = null,
    string? FailureCode = null,
    UpdateManifest? Manifest = null)
{
    public bool HasUpdate => Availability == UpdateAvailability.UpdateAvailable;
}

public sealed record UpdateDownloadResult(
    UpdateAvailability Availability,
    string? Message,
    string? StagedPackagePath = null,
    string? FailureCode = null);

public sealed record UpdateInstallResult(bool Succeeded, string? FailureCode, string? Message)
{
    public static UpdateInstallResult FailClosed(string code, string message) => new(false, code, message);
    public static UpdateInstallResult Ok() => new(true, null, null);
}

/// <summary>
/// The install boundary. Production installation is a deliberate fail-closed stub until the
/// Authenticode certificate and signing environment exist (D5); the state machine and tests do not
/// depend on a working installer.
/// </summary>
public interface IUpdateInstaller
{
    Task<UpdateInstallResult> InstallAsync(string stagedPackagePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// App-side update workflow (UPD-01): fetch and verify a signed manifest, reject downgrades,
/// download a package to a controlled staging directory, and record rollback intent. It never
/// installs or executes anything itself and never silently trusts an unsigned manifest.
/// </summary>
public sealed class SignedUpdateService(HttpClient httpClient, AppLogService? logService = null)
{
    private readonly HttpClient _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly AppLogService? _log = logService;

    public async Task<UpdateCheckResult> CheckAsync(
        string currentVersion,
        string? manifestUrl,
        string? signatureUrl,
        string? pinnedPublisherPublicKeySha256,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl) ||
            string.IsNullOrWhiteSpace(signatureUrl) ||
            string.IsNullOrWhiteSpace(pinnedPublisherPublicKeySha256))
        {
            return new UpdateCheckResult(
                UpdateAvailability.Disabled,
                "Automatic updates are not configured. Release notes remain the manual update channel.",
                FailureCode: "not-configured");
        }

        byte[] manifestBytes;
        byte[] signatureBytes;
        try
        {
            manifestBytes = await _http.GetByteArrayAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
            signatureBytes = await _http.GetByteArrayAsync(signatureUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new UpdateCheckResult(UpdateAvailability.Cancelled, "The update check was cancelled.", FailureCode: "cancelled");
        }
        catch (TaskCanceledException)
        {
            return new UpdateCheckResult(UpdateAvailability.Error, "The update check timed out.", FailureCode: "timeout");
        }
        catch (HttpRequestException)
        {
            return new UpdateCheckResult(UpdateAvailability.Offline, "No network connection; the update check could not complete.", FailureCode: "offline");
        }

        var verification = SignedUpdateVerifier.Verify(manifestBytes, signatureBytes, pinnedPublisherPublicKeySha256);
        if (!verification.IsVerified || verification.Manifest is null)
        {
            _log?.Info($"Update manifest rejected. code={verification.FailureCode}.");
            return new UpdateCheckResult(
                UpdateAvailability.VerificationFailed,
                verification.FailureMessage ?? "The update manifest could not be verified.",
                FailureCode: verification.FailureCode);
        }

        var manifest = verification.Manifest;
        if (!UpdateVersion.TryParse(currentVersion, out var current) || !UpdateVersion.TryParse(manifest.Version, out var available))
        {
            return new UpdateCheckResult(UpdateAvailability.Error, "An update version number was not valid.", FailureCode: "invalid-version", Manifest: manifest);
        }

        if (available < current)
        {
            return new UpdateCheckResult(
                UpdateAvailability.DowngradeRejected,
                $"The offered version {manifest.Version} is older than the running {currentVersion}.",
                available.ToString(3),
                "downgrade",
                manifest);
        }

        if (UpdateVersion.TryParse(manifest.MinimumSupportedVersion, out var minimum) && current < minimum)
        {
            return new UpdateCheckResult(
                UpdateAvailability.UnsupportedClient,
                $"This build is older than the supported minimum {manifest.MinimumSupportedVersion}.",
                available.ToString(3),
                "unsupported-client",
                manifest);
        }

        if (available == current)
        {
            return new UpdateCheckResult(UpdateAvailability.UpToDate, "Muesli is up to date.", available.ToString(3), Manifest: manifest);
        }

        return new UpdateCheckResult(UpdateAvailability.UpdateAvailable, $"Muesli {manifest.Version} is available.", available.ToString(3), Manifest: manifest);
    }

    public async Task<UpdateDownloadResult> DownloadAsync(
        UpdatePackage package,
        string? packageUrl,
        string stagingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(packageUrl) ||
            string.IsNullOrWhiteSpace(package.FileName) ||
            package.FileName != Path.GetFileName(package.FileName) ||
            package.FileName is "." or ".." ||
            package.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return new UpdateDownloadResult(UpdateAvailability.Error, "The update package location is not valid.", FailureCode: "invalid-package");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new UpdateDownloadResult(UpdateAvailability.Cancelled, "The update download was cancelled.", FailureCode: "cancelled");
        }

        try
        {
            Directory.CreateDirectory(stagingDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new UpdateDownloadResult(UpdateAvailability.Error, "The update staging directory could not be created.", FailureCode: "staging-failed");
        }

        var finalPath = Path.Combine(stagingDirectory, package.FileName);
        var temporaryPath = finalPath + ".partial";
        try
        {
            using var response = await _http
                .GetAsync(packageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var target = new FileStream(
                temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                await response.Content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                target.Flush(true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DeleteQuietly(temporaryPath);
            return new UpdateDownloadResult(UpdateAvailability.Cancelled, "The update download was cancelled.", FailureCode: "cancelled");
        }
        catch (TaskCanceledException)
        {
            DeleteQuietly(temporaryPath);
            return new UpdateDownloadResult(UpdateAvailability.Error, "The update download timed out.", FailureCode: "timeout");
        }
        catch (HttpRequestException)
        {
            DeleteQuietly(temporaryPath);
            return new UpdateDownloadResult(UpdateAvailability.Offline, "No network connection; the update could not be downloaded.", FailureCode: "offline");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(temporaryPath);
            return new UpdateDownloadResult(UpdateAvailability.Error, "The update could not be written to disk.", FailureCode: "write-failed");
        }

        var info = new FileInfo(temporaryPath);
        if (info.Length != package.Bytes || !string.Equals(SignedUpdateVerifier.HashFileHex(temporaryPath), SignedUpdateVerifier.NormalizeHash(package.Sha256), StringComparison.Ordinal))
        {
            DeleteQuietly(temporaryPath);
            _log?.Info($"Update package rejected. package={package.Name}; reason=hash-or-size-mismatch.");
            return new UpdateDownloadResult(
                UpdateAvailability.VerificationFailed,
                "The downloaded update did not match its signed hash and was discarded.",
                FailureCode: "tamper");
        }

        try
        {
            File.Move(temporaryPath, finalPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(temporaryPath);
            return new UpdateDownloadResult(UpdateAvailability.Error, "The verified update could not be staged.", FailureCode: "staging-failed");
        }

        _log?.Info($"Update package staged. package={package.Name}; bytes={package.Bytes}.");
        return new UpdateDownloadResult(UpdateAvailability.UpdateAvailable, "The update was downloaded and verified.", finalPath);
    }

    private static void DeleteQuietly(string path)
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
            // Best effort; a leftover .partial is harmless and never executed.
        }
    }
}

public sealed record UpdateRollbackEntry(
    string PreviousVersion,
    string? StagedPackagePath,
    string PackageSha256,
    string State,
    DateTimeOffset StartedAtUtc);

/// <summary>
/// Durable record of an in-flight install so a crash between "staged" and "installed" rolls back
/// to the running version instead of leaving a half-applied update. Recovery discards any staged
/// package it owns and reports that the previous version must keep running.
/// </summary>
public sealed class UpdateRollbackJournal(string journalPath)
{
    private readonly string _journalPath = Path.GetFullPath(journalPath);

    public UpdateRollbackEntry? Read()
    {
        try
        {
            if (!File.Exists(_journalPath))
            {
                return null;
            }

            return System.Text.Json.JsonSerializer.Deserialize<UpdateRollbackEntry>(
                File.ReadAllText(_journalPath),
                UpdateManifestContract.Json);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public void Begin(UpdateRollbackEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var directory = Path.GetDirectoryName(_journalPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_journalPath, System.Text.Json.JsonSerializer.Serialize(entry, UpdateManifestContract.Json));
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_journalPath))
            {
                File.Delete(_journalPath);
            }
        }
        catch (IOException)
        {
            // Leave the journal in place; recovery will try again next launch.
        }
    }

    /// <summary>
    /// Resolves an interrupted install. Returns true when a staged package was discarded and the
    /// previous version must continue running.
    /// </summary>
    public bool TryRecover(out UpdateRollbackEntry? entry)
    {
        entry = Read();
        if (entry is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(entry.StagedPackagePath))
        {
            try
            {
                var stagedPath = Path.GetFullPath(entry.StagedPackagePath);
                if (string.Equals(Path.GetDirectoryName(stagedPath), Path.GetDirectoryName(_journalPath), StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(stagedPath))
                {
                    File.Delete(stagedPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Best effort; the file is not executed and stays journal-tracked.
            }
        }

        Clear();
        return true;
    }
}
