namespace Muesli.Windows.Services;

/// <summary>
/// Production install boundary for the app-side updater (UPD-01). Installing a signed MSIX and
/// validating its Authenticode chain against the release publisher requires the production
/// certificate and signing environment that do not exist yet (D5). Until then this fails closed
/// with a named reason instead of pretending to update, and the manual release channel remains the
/// supported path.
/// </summary>
public sealed class WindowsProductionUpdateInstaller : IUpdateInstaller
{
    public const string NotConfiguredCode = "production-signing-not-configured";

    public Task<UpdateInstallResult> InstallAsync(
        string stagedPackagePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = stagedPackagePath;
        return Task.FromResult(UpdateInstallResult.FailClosed(
            NotConfiguredCode,
            "Automatic installation is unavailable until the production Authenticode certificate and signing environment are configured. Install the signed release manually."));
    }
}
