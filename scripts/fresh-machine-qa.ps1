# Static fresh-profile / package QA for the shipping WinUI MSIX.
#
# This validates the package contents a clean machine would receive. It does not install anything
# and does not replace clean-VM install/upgrade/uninstall qualification; it records that gate
# explicitly as not performed.

param(
    [string]$MsixPath = "",
    [string]$ReportPath = "",
    [string]$ExpectedPublisher = ""
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "read-release-properties.ps1")
. (Join-Path $PSScriptRoot "release-common.ps1")
$release = Get-MuesliReleaseProperties -Root $root

if ([string]::IsNullOrWhiteSpace($MsixPath)) {
    $discovered = Get-MuesliLatestMsix -SearchRoot (Join-Path $root "artifacts\msix")
    if ($null -eq $discovered) { throw "No WinUI MSIX was found under artifacts\msix. Build it first." }
    $MsixPath = $discovered.FullName
}
$MsixPath = (Resolve-Path -LiteralPath $MsixPath).Path
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $root "artifacts\fresh-machine-qa.json"
}

$failures = [System.Collections.Generic.List[string]]::new()

# Payload, identity, native catalog and foreign-RID checks are owned by the MSIX smoke test.
$smokeReport = Join-Path ([IO.Path]::GetDirectoryName($ReportPath)) "msix-smoke-report.json"
$smokePassed = $true
try {
    & (Join-Path $PSScriptRoot "test-winui-msix.ps1") -MsixPath $MsixPath -ReportPath $smokeReport -SkipLaunch | Out-Null
} catch {
    $smokePassed = $false
    $failures.Add("MSIX smoke test failed: $($_.Exception.Message)")
}

$identity = Get-MuesliMsixManifestIdentity -MsixPath $MsixPath
$expectedVersion = "$($release.Version).0"
if ([string]$identity.Version -ne $expectedVersion) {
    $failures.Add("MSIX identity version '$($identity.Version)' does not match release '$expectedVersion'.")
}
$publisherIsPlaceholder = [string]$identity.Publisher -match '^CN=AppPublisher$'
if ($publisherIsPlaceholder) {
    $failures.Add("MSIX still carries the development publisher placeholder; public release requires the production publisher.")
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedPublisher) -and
    -not [string]::Equals([string]$identity.Publisher, $ExpectedPublisher.Trim(), [StringComparison]::OrdinalIgnoreCase)) {
    $failures.Add("MSIX publisher '$($identity.Publisher)' does not match expected '$ExpectedPublisher'.")
}

$report = [ordered]@{
    schemaVersion = 1
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    releaseVersion = $release.Version
    releaseChannel = $release.Channel
    msixPath = $MsixPath
    msixSha256 = Get-Sha256HexFromFile -Path $MsixPath
    identity = $identity
    publisherIsPlaceholder = $publisherIsPlaceholder
    structuralQaPassed = $smokePassed -and ($failures.Count -eq 0)
    passed = $failures.Count -eq 0
    failures = @($failures)
    cleanVmQualification = [ordered]@{
        status = "NotPerformed"
        prerequisite = "Run the signed MSIX through install/first-launch/upgrade/downgrade/uninstall on a clean Windows VM or Windows Sandbox."
    }
    manualChecksRemaining = @(
        "Onboarding once on a clean profile",
        "Explicit microphone permission request",
        "Startup registration enable/disable through the packaged startup task",
        "Dictation paste and meeting detection against real applications"
    )
}
Write-Utf8NoBomFile -Path $ReportPath -Content (($report | ConvertTo-Json -Depth 12) + "`n")
Write-Host "Fresh-machine static QA report: $ReportPath"
if ($failures.Count -gt 0) {
    throw "Fresh-machine static QA failed: $($failures -join '; ')"
}
Write-Host "Fresh-machine static QA passed for $MsixPath"
