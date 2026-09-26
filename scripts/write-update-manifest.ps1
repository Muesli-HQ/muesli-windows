<##
.SYNOPSIS
Writes a cryptographically signed update-channel manifest.

Fixture/dry-run modes use an ephemeral RSA key and clearly mark the detached
signature as FixtureOnly. Production mode requires a certificate thumbprint and
private key; no production certificate is bundled or inferred.
##>
param(
    [Parameter(Mandatory = $true)]
    [string]$PortablePackagePath,
    [string]$InstallerPath = "",
    [string]$ContentInventoryPath = "",
    [string]$SigningReportPath = "",
    [string]$OutputPath = "",
    [ValidateSet("unsigned", "dry-run", "fixture", "production")]
    [string]$SigningMode = "unsigned",
    [string]$ManifestCertificateThumbprint = "",
    [string]$ExpectedPublisherPublicKeySha256 = "",
    [string]$ExpectedPublisherCertificateThumbprint = "",
    [string]$ExpectedPublisherSubject = "",
    [string]$MinimumSupportedVersion = "",
    [string]$ReleaseNotes = ""
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "read-release-properties.ps1")
. (Join-Path $PSScriptRoot "release-common.ps1")
$release = Get-MuesliReleaseProperties -Root $root

function Get-ArtifactRecord {
    param([string]$Path, [string]$Name)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "$Name artifact was not found: $resolved"
    }
    return [ordered]@{
        name = $Name
        fileName = [IO.Path]::GetFileName($resolved)
        bytes = (Get-Item -LiteralPath $resolved).Length
        sha256 = Get-Sha256HexFromFile -Path $resolved
    }
}

$portable = Get-ArtifactRecord -Path $PortablePackagePath -Name "portableZip"
$installer = Get-ArtifactRecord -Path $InstallerPath -Name "installer"
$contentDigest = $null
if (-not [string]::IsNullOrWhiteSpace($ContentInventoryPath)) {
    if (-not (Test-Path -LiteralPath $ContentInventoryPath -PathType Leaf)) {
        throw "Content inventory was not found: $ContentInventoryPath"
    }
    $inventory = Get-Content -LiteralPath $ContentInventoryPath -Raw | ConvertFrom-Json
    $contentDigest = [string]$inventory.contentDigest
    if ([string]::IsNullOrWhiteSpace($contentDigest)) {
        throw "Content inventory has no contentDigest: $ContentInventoryPath"
    }
}

$signingReport = $null
if (-not [string]::IsNullOrWhiteSpace($SigningReportPath)) {
    if (-not (Test-Path -LiteralPath $SigningReportPath -PathType Leaf)) {
        throw "Signing report was not found: $SigningReportPath"
    }
    $signingReport = Get-Content -LiteralPath $SigningReportPath -Raw | ConvertFrom-Json
}
if ($SigningMode -eq "production" -and ($null -eq $signingReport -or -not [bool]$signingReport.authenticodeVerified)) {
    throw "Production update manifest requires a signing report with authenticodeVerified=true."
}
if ($SigningMode -eq "production" -and [string]::IsNullOrWhiteSpace($ManifestCertificateThumbprint)) {
    throw "Production update manifest signing requires -ManifestCertificateThumbprint."
}
if ($SigningMode -ne "unsigned" -and [string]::IsNullOrWhiteSpace($ExpectedPublisherPublicKeySha256) -and
    [string]::IsNullOrWhiteSpace($ExpectedPublisherCertificateThumbprint) -and
    $SigningMode -eq "production") {
    throw "Production update manifest signing requires an independently pinned publisher key hash or certificate thumbprint."
}

$manifestSignatureStatus = if ($SigningMode -eq "unsigned") { "Unsigned" } elseif ($SigningMode -eq "production") { "Valid" } else { "FixtureOnly" }
$manifest = [ordered]@{
    schemaVersion = 1
    contract = "muesli.windows.update-channel"
    product = "Muesli for Windows"
    channel = $release.Channel
    version = $release.Version
    runtime = "win-x64"
    minimumSupportedVersion = if ([string]::IsNullOrWhiteSpace($MinimumSupportedVersion)) { $null } else { $MinimumSupportedVersion.Trim().TrimStart('v') }
    releaseNotes = if ([string]::IsNullOrWhiteSpace($ReleaseNotes)) { $null } else { $ReleaseNotes }
    signing = [ordered]@{
        mode = $SigningMode
        status = $manifestSignatureStatus
        algorithm = "RSA-SHA256"
        signaturePresent = $SigningMode -ne "unsigned"
        authenticodeRequired = $SigningMode -eq "production"
        authenticodeVerified = $SigningMode -eq "production"
        publisherPublicKeySha256 = ""
        publisherCertificateThumbprint = ""
        publisherSubject = ""
        detachedSignatureFile = ""
    }
    packages = @($portable, $installer) | Where-Object { $null -ne $_ }
    portableContentDigest = $contentDigest
    generatedAtUtc = [DateTime]::UtcNow.ToString("o")
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $root "artifacts\update-channel-$($release.Channel).json"
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$detachedPath = "$OutputPath.signature.json"
$manifest.signing.detachedSignatureFile = [IO.Path]::GetFileName($detachedPath)
Write-Utf8NoBomFile -Path $OutputPath -Content (($manifest | ConvertTo-Json -Depth 10) + "`n")

$rsa = $null
$certificate = $null
try {
    if ($SigningMode -eq "production") {
        $normalizedThumbprint = Normalize-MuesliThumbprint -Thumbprint $ManifestCertificateThumbprint
        $certificate = Get-MuesliCertificateByThumbprint -Thumbprint $normalizedThumbprint
        Assert-MuesliCodeSigningCertificate -Certificate $certificate -ExpectedSubject $ExpectedPublisherSubject | Out-Null
        $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
        if ($null -eq $rsa) { throw "Manifest signing certificate has no RSA private key." }
    } elseif ($SigningMode -ne "unsigned") {
        $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    }

    $signatureBase64 = ""
    $publicKeyBase64 = ""
    if ($null -ne $rsa) {
        $publicKeyBase64 = [Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo())
    }
    $publicKeyBytes = if ([string]::IsNullOrWhiteSpace($publicKeyBase64)) { [byte[]]@() } else { [Convert]::FromBase64String($publicKeyBase64) }
    $publisherKeyHash = if ($publicKeyBytes.Length -eq 0) { "" } else { Get-MuesliPublicKeySha256 -SubjectPublicKeyInfo $publicKeyBytes }
    $publisherThumbprint = if ($null -eq $certificate) { "" } else { Normalize-MuesliThumbprint -Thumbprint ([string]$certificate.Thumbprint) }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedPublisherPublicKeySha256) -and
        $publisherKeyHash -ne (Normalize-MuesliSha256 -Hash $ExpectedPublisherPublicKeySha256)) {
        throw "Generated manifest publisher key hash '$publisherKeyHash' did not match the independently supplied expected hash."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedPublisherCertificateThumbprint) -and
        $publisherThumbprint -ne (Normalize-MuesliThumbprint -Thumbprint $ExpectedPublisherCertificateThumbprint)) {
        throw "Generated manifest certificate thumbprint '$publisherThumbprint' did not match the independently supplied expected thumbprint."
    }
    $manifest.signing.publisherPublicKeySha256 = $publisherKeyHash
    $manifest.signing.publisherCertificateThumbprint = $publisherThumbprint
    $manifest.signing.publisherSubject = if ($null -eq $certificate) { "" } else { [string]$certificate.Subject }
    # The manifest is signed after publisher metadata is finalized so the
    # detached signature covers the key pin fields too.
    Write-Utf8NoBomFile -Path $OutputPath -Content (($manifest | ConvertTo-Json -Depth 10) + "`n")
    if ($null -ne $rsa) {
        $manifestBytes = [IO.File]::ReadAllBytes($OutputPath)
        $signatureBase64 = [Convert]::ToBase64String($rsa.SignData(
            $manifestBytes,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1))
    }
    $detached = [ordered]@{
        schemaVersion = 1
        contract = "muesli.windows.update-channel.detached-signature"
        manifestFileName = [IO.Path]::GetFileName($OutputPath)
        manifestSha256 = Get-Sha256HexFromFile -Path $OutputPath
        mode = $SigningMode
        status = $manifestSignatureStatus
        algorithm = "RSA-SHA256"
        signatureBase64 = $signatureBase64
        publicKeySpkiBase64 = $publicKeyBase64
        publisherPublicKeySha256 = $publisherKeyHash
        publisherCertificateThumbprint = $publisherThumbprint
        publisherSubject = if ($null -eq $certificate) { "" } else { [string]$certificate.Subject }
        authenticode = $SigningMode -eq "production"
        note = if ($SigningMode -eq "production") { "Manifest signature is verified with the configured release certificate public key." } elseif ($SigningMode -eq "unsigned") { "Unsigned manifest; no signature is authorized." } else { "Fixture/dry-run signature only; does not authorize a production update." }
        generatedAtUtc = [DateTime]::UtcNow.ToString("o")
    }
    Write-Utf8NoBomFile -Path $detachedPath -Content (($detached | ConvertTo-Json -Depth 8) + "`n")
} finally {
    if ($null -ne $rsa) { $rsa.Dispose() }
}

Write-Host "Wrote update manifest: $OutputPath (mode=$SigningMode status=$manifestSignatureStatus)"
Write-Host "Wrote detached update-manifest signature: $detachedPath"
