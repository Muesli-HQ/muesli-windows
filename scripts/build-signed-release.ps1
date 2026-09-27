# Signed Windows release orchestration for the WinUI MSIX.
#
# Order: package unsigned MSIX -> sign MSIX -> verify publisher/signature/timestamp -> inventory and
# hashes -> report. Production fails closed without an explicit certificate that matches the MSIX
# manifest publisher and a trusted timestamp. -DryRun / -TestCertificate rehearse the order with a
# metadata-only fixture record and never satisfy the public-release gate.

param(
    [string]$Configuration = "Release",
    [switch]$AllowDirty,
    [switch]$DryRun,
    [switch]$TestCertificate,
    [switch]$SkipTests,
    [string]$CertificateThumbprint = "",
    [string]$ExpectedPublisherSubject = "",
    [string]$TimestampUrl = "https://timestamp.digicert.com",
    [string]$SignatureOutputDirectory = ""
)

$ErrorActionPreference = "Stop"
if ($DryRun -and $TestCertificate) {
    throw "Choose either -DryRun or -TestCertificate, not both."
}

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location -LiteralPath $root
. (Join-Path $PSScriptRoot "read-release-properties.ps1")
. (Join-Path $PSScriptRoot "release-common.ps1")
$release = Get-MuesliReleaseProperties -Root $root
$artifactsDir = Join-Path $root "artifacts"
New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null

$mode = if ($DryRun) { "dry-run" } elseif ($TestCertificate) { "fixture" } else { "production" }
if ($mode -eq "production") {
    # Fail closed before any packaging when the production publisher is missing or still the
    # development placeholder.
    Assert-MuesliProductionPublisher -Publisher $ExpectedPublisherSubject
}

$tree = Assert-MuesliCleanReleaseInputs -Root $root -AllowDirty:$AllowDirty
$sdk = Assert-MuesliPinnedSdk -Root $root
Assert-MuesliModelSourcesPresent -Root $root | Out-Null
$sharedCore = Assert-MuesliSharedCoreRevision -Root $root -AllowUnpinnedDevOverride
$env:MUESLI_SHARED_CORE_MODE = if ($sharedCore.BridgeRequired) { 'shared-core' } else { 'managed-fallback' }
if (-not $sharedCore.BridgeRequired) {
    # Ships the managed-fallback text processor; the Swift bridge is optional and stale staging is removed.
    Write-Host "Shared core mode: $($sharedCore.Mode). Swift bridge is optional."
}

Write-Host "Building unsigned WinUI MSIX (mode=$mode)..."
& (Join-Path $PSScriptRoot "package-winui-msix.ps1") -Configuration $Configuration -OutputDirectory (Join-Path $artifactsDir "msix") | Out-Null
$msix = Get-MuesliLatestMsix -SearchRoot (Join-Path $artifactsDir "msix")
if ($null -eq $msix) { throw "MSIX packaging reported success but produced no .msix." }
$msixPath = $msix.FullName

Write-Host "Signing MSIX (mode=$mode)..."
$signArgs = @{
    MsixPath = $msixPath
    TimestampUrl = $TimestampUrl
    SignatureOutputDirectory = if ([string]::IsNullOrWhiteSpace($SignatureOutputDirectory)) { Join-Path $artifactsDir "signatures\msix" } else { $SignatureOutputDirectory }
}
if ($DryRun) { $signArgs.DryRun = $true }
if ($TestCertificate) { $signArgs.TestCertificate = $true }
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) { $signArgs.CertificateThumbprint = $CertificateThumbprint }
if (-not [string]::IsNullOrWhiteSpace($ExpectedPublisherSubject)) { $signArgs.ExpectedPublisherSubject = $ExpectedPublisherSubject }
& (Join-Path $PSScriptRoot "sign-windows-release.ps1") @signArgs
$signingReportPath = Join-Path $signArgs.SignatureOutputDirectory "signing-report.json"

Write-Host "Validating publisher and signature..."
$identity = Get-MuesliMsixManifestIdentity -MsixPath $msixPath
$publisherIsPlaceholder = [string]$identity.Publisher -match '^CN=AppPublisher$'
$signatureVerified = $false
if ($mode -eq "production") {
    Assert-MuesliMsixPublisher -MsixPath $msixPath -ExpectedPublisher $ExpectedPublisherSubject | Out-Null
    Assert-MuesliSignedMsix -MsixPath $msixPath -ExpectedPublisher $ExpectedPublisherSubject | Out-Null
    $signatureVerified = $true
}

Write-Host "Writing content and native inventories..."
$inventoryPath = Join-Path $artifactsDir "package-content-inventory.json"
& (Join-Path $PSScriptRoot "write-package-content-inventory.ps1") -ZipPath $msixPath -OutputPath $inventoryPath
$nativeInventoryPath = Join-Path $artifactsDir "native-runtime-inventory.json"
& (Join-Path $PSScriptRoot "test-winui-msix.ps1") -MsixPath $msixPath -ReportPath (Join-Path $artifactsDir "msix-smoke-report.json") -NativeInventoryPath $nativeInventoryPath -SkipLaunch | Out-Null

$hashTargets = @(
    @{ name = "msix"; path = $msixPath },
    @{ name = "contentInventory"; path = $inventoryPath },
    @{ name = "nativeInventory"; path = $nativeInventoryPath },
    @{ name = "signingReport"; path = $signingReportPath }
)
$hashes = [ordered]@{
    schemaVersion = 1
    module = "L05"
    version = $release.Version
    channel = $release.Channel
    sdk = $sdk
    gitHead = $tree.Head
    signingMode = $mode
    authenticodeVerified = $signatureVerified
    files = @()
}
foreach ($target in $hashTargets) {
    if (-not (Test-Path -LiteralPath $target.path -PathType Leaf)) { throw "Expected release evidence is missing: $($target.path)" }
    $hashes.files += [ordered]@{
        name = $target.name
        fileName = [IO.Path]::GetFileName($target.path)
        bytes = (Get-Item -LiteralPath $target.path).Length
        sha256 = Get-Sha256HexFromFile -Path $target.path
    }
}
$hashesPath = Join-Path $artifactsDir "signed-release-hashes.json"
Write-Utf8NoBomFile -Path $hashesPath -Content (($hashes | ConvertTo-Json -Depth 10) + "`n")

$report = [ordered]@{
    schemaVersion = 1
    module = "L05"
    signed = $signatureVerified
    fixtureOnly = $mode -ne "production"
    signingMode = $mode
    version = $release.Version
    channel = $release.Channel
    configuration = $Configuration
    sdk = $sdk
    gitHead = $tree.Head
    dirtyTree = -not $tree.Clean
    allowDirty = [bool]$AllowDirty
    msixPath = $msixPath
    msixIdentity = $identity
    publisherIsPlaceholder = $publisherIsPlaceholder
    signatureVerified = $signatureVerified
    artifactOrder = @("package-msix", "sign-msix", "verify", "inventory", "hashes")
    legalProductGates = @(Get-MuesliReleaseLegalProductGates -Root $root)
    artifacts = @(
        [IO.Path]::GetFileName($msixPath),
        [IO.Path]::GetFileName($hashesPath),
        "signatures/msix/signing-report.json",
        "package-content-inventory.json",
        "native-runtime-inventory.json"
    )
    generatedAtUtc = [DateTime]::UtcNow.ToString("o")
}
$reportPath = Join-Path $artifactsDir "signed-release-report.json"
Write-Utf8NoBomFile -Path $reportPath -Content (($report | ConvertTo-Json -Depth 12) + "`n")
Write-Host "Signed release orchestration complete (mode=$mode). Report: $reportPath"
