# L05 MSIX signing. Signs the WinUI MSIX with an explicitly configured production certificate and
# a trusted RFC 3161 timestamp, verifies the signature and publisher identity, and writes a
# machine-readable signing record.
#
# -DryRun / -TestCertificate produce metadata-only fixture records for pipeline rehearsal. They are
# explicitly not Authenticode signatures and never satisfy the public-release gate.
#
# Public release fails closed without a valid production certificate, a publisher that matches the
# MSIX manifest, and a timestamp.

param(
    [string]$MsixPath = "",
    [string]$CertificateThumbprint = "",
    [string]$TimestampUrl = "https://timestamp.digicert.com",
    [string]$SignatureOutputDirectory = "",
    [string]$ExpectedPublisherSubject = "",
    [switch]$DryRun,
    [switch]$TestCertificate
)

$ErrorActionPreference = "Stop"
if ($DryRun -and $TestCertificate) {
    throw "Choose either -DryRun or -TestCertificate, not both."
}

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "read-release-properties.ps1")
. (Join-Path $PSScriptRoot "release-common.ps1")
$release = Get-MuesliReleaseProperties -Root $root

if ([string]::IsNullOrWhiteSpace($MsixPath)) {
    $discovered = Get-MuesliLatestMsix -SearchRoot (Join-Path $root "artifacts\msix")
    if ($null -eq $discovered) {
        throw "No Muesli Windows WinUI MSIX was found under artifacts\msix. Build the package first."
    }
    $MsixPath = $discovered.FullName
}
$MsixPath = [IO.Path]::GetFullPath($MsixPath)
if (-not (Test-Path -LiteralPath $MsixPath -PathType Leaf)) {
    throw "MSIX not found: $MsixPath"
}

$identity = Get-MuesliMsixManifestIdentity -MsixPath $MsixPath
if ([string]::IsNullOrWhiteSpace($SignatureOutputDirectory)) {
    $SignatureOutputDirectory = Join-Path $root "artifacts\signatures\msix"
}
New-Item -ItemType Directory -Force -Path $SignatureOutputDirectory | Out-Null
$recordPath = Join-Path $SignatureOutputDirectory "Muesli.Windows.WinUI.msix.signature.json"

function Find-MuesliSignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $sdkBin = "C:\Program Files (x86)\Windows Kits\10\bin"
    if (Test-Path -LiteralPath $sdkBin) {
        return Get-ChildItem -LiteralPath $sdkBin -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }
    return $null
}

function Write-FixtureSignatureRecord {
    param([ValidateSet("dry-run", "fixture")][string]$Mode)
    $record = [ordered]@{
        schemaVersion = 1
        target = [IO.Path]::GetFileName($MsixPath)
        targetSha256 = Get-Sha256HexFromFile -Path $MsixPath
        mode = $Mode
        status = if ($Mode -eq "fixture") { "FixtureValid" } else { "Simulated" }
        authenticode = $false
        timestamped = $false
        publisher = $identity.Publisher
        publisherIsPlaceholder = [string]$identity.Publisher -match '^CN=AppPublisher$'
        certificate = if ($Mode -eq "fixture") { "Muesli fixture signing certificate" } else { $null }
        generatedAtUtc = [DateTime]::UtcNow.ToString("o")
        note = "Metadata-only fixture record; this MSIX is NOT production-signed and must not be published."
    }
    Write-Utf8NoBomFile -Path $recordPath -Content (($record | ConvertTo-Json -Depth 8) + "`n")
    return $record
}

function Invoke-ProductionMsixSign {
    param([string]$SignToolPath, $Certificate)

    $arguments = @("sign", "/fd", "SHA256")
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $arguments += @("/tr", $TimestampUrl, "/td", "SHA256")
    } else {
        throw "Production MSIX signing requires a -TimestampUrl; unsigned-timestamp releases are refused."
    }
    # Never use signtool's automatic certificate selection. The release is tied to the explicit
    # publisher certificate whose subject must match the MSIX manifest Identity.Publisher.
    $arguments += @("/sha1", (Normalize-MuesliThumbprint -Thumbprint $CertificateThumbprint))
    $arguments += $MsixPath
    & $SignToolPath @arguments
    if ($LASTEXITCODE -ne 0) { throw "signtool sign failed for $MsixPath." }

    & $SignToolPath verify /pa /v $MsixPath
    if ($LASTEXITCODE -ne 0) { throw "signtool verify failed for $MsixPath." }

    $verified = Assert-MuesliSignedMsix -MsixPath $MsixPath -ExpectedPublisher $identity.Publisher
    if ($verified.signerSubject -ne [string]$Certificate.Subject) {
        throw "Verified signer '$($verified.signerSubject)' does not match the configured certificate '$($Certificate.Subject)'."
    }
    return [ordered]@{
        schemaVersion = 1
        target = [IO.Path]::GetFileName($MsixPath)
        targetSha256 = Get-Sha256HexFromFile -Path $MsixPath
        mode = "production"
        status = "Valid"
        authenticode = $true
        timestamped = [bool]$verified.timestamped
        publisher = $identity.Publisher
        publisherIsPlaceholder = $false
        certificate = (Normalize-MuesliThumbprint -Thumbprint $CertificateThumbprint)
        certificateSubject = [string]$Certificate.Subject
        publisherPublicKeySha256 = Get-MuesliPublicKeySha256 -SubjectPublicKeyInfo $Certificate.ExportSubjectPublicKeyInfo()
        codeSigningEku = "1.3.6.1.5.5.7.3.3"
        generatedAtUtc = [DateTime]::UtcNow.ToString("o")
    }
}

$mode = if ($DryRun) { "dry-run" } elseif ($TestCertificate) { "fixture" } else { "production" }
$result = $null
if ($mode -eq "production") {
    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        throw "Production MSIX signing requires an explicit -CertificateThumbprint; automatic certificate selection is forbidden."
    }
    $CertificateThumbprint = Normalize-MuesliThumbprint -Thumbprint $CertificateThumbprint
    $signingCertificate = Get-MuesliCertificateByThumbprint -Thumbprint $CertificateThumbprint
    Assert-MuesliCodeSigningCertificate -Certificate $signingCertificate -ExpectedSubject $ExpectedPublisherSubject | Out-Null
    # The manifest publisher must equal the certificate subject, or the MSIX will not install.
    if (-not [string]::Equals([string]$signingCertificate.Subject, [string]$identity.Publisher, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Signing certificate subject '$($signingCertificate.Subject)' must exactly match the MSIX manifest publisher '$($identity.Publisher)'."
    }
    $signToolPath = Find-MuesliSignTool
    if ([string]::IsNullOrWhiteSpace($signToolPath)) {
        throw "signtool.exe was not found. Install the Windows SDK or use -DryRun/-TestCertificate for a non-production fixture rehearsal."
    }
    $result = Invoke-ProductionMsixSign -SignToolPath $signToolPath -Certificate $signingCertificate
} else {
    $result = Write-FixtureSignatureRecord -Mode $mode
}

$report = [ordered]@{
    schemaVersion = 1
    module = "L05"
    version = $release.Version
    channel = $release.Channel
    mode = $mode
    authenticodeVerified = $mode -eq "production"
    fixtureOnly = $mode -ne "production"
    msixPath = $MsixPath
    publisher = $identity.Publisher
    publisherIsPlaceholder = [bool]$result.publisherIsPlaceholder
    targets = @($result)
    generatedAtUtc = [DateTime]::UtcNow.ToString("o")
}
$reportPath = Join-Path $SignatureOutputDirectory "signing-report.json"
Write-Utf8NoBomFile -Path $reportPath -Content (($report | ConvertTo-Json -Depth 10) + "`n")
Write-Host "Signing report: $reportPath (mode=$mode status=$($result.status))"
