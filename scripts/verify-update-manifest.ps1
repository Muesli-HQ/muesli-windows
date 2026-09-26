<##
.SYNOPSIS
Verifies update-manifest structure, package hashes, and its detached signature record.
##>
param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,
    [ValidateSet("unsigned", "dry-run", "fixture", "production")]
    [string]$ExpectedSigningMode = "",
    [Alias("ExpectedPublisherKeyHash")]
    [string]$ExpectedPublisherPublicKeySha256 = "",
    [string]$ExpectedPublisherCertificateThumbprint = ""
)

$ErrorActionPreference = "Stop"
$resolvedManifest = [IO.Path]::GetFullPath($ManifestPath)
if (-not (Test-Path -LiteralPath $resolvedManifest -PathType Leaf)) {
    throw "Update manifest was not found: $resolvedManifest"
}
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "release-common.ps1")
$manifest = Get-Content -LiteralPath $resolvedManifest -Raw | ConvertFrom-Json
if ([int]$manifest.schemaVersion -ne 1 -or [string]$manifest.contract -ne "muesli.windows.update-channel") {
    throw "Unsupported update manifest contract or schema."
}
$minimumSupported = [string]$manifest.minimumSupportedVersion
if (-not [string]::IsNullOrWhiteSpace($minimumSupported)) {
    $parsedMinimum = $null
    if (-not [version]::TryParse($minimumSupported.Trim().TrimStart('v'), [ref]$parsedMinimum)) {
        throw "Update manifest minimumSupportedVersion is not a valid version: '$minimumSupported'."
    }
}
$mode = [string]$manifest.signing.mode
if (-not [string]::IsNullOrWhiteSpace($ExpectedSigningMode) -and $mode -ne $ExpectedSigningMode) {
    throw "Update manifest signing mode '$mode' did not match expected '$ExpectedSigningMode'."
}

$manifestDirectory = Split-Path -Parent $resolvedManifest
foreach ($package in @($manifest.packages)) {
    $packagePath = Join-Path $manifestDirectory ([string]$package.fileName)
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "Update package is missing: $packagePath"
    }
    $actualHash = Get-Sha256HexFromFile -Path $packagePath
    if ($actualHash -ne ([string]$package.sha256).ToLowerInvariant()) {
        throw "Update package hash mismatch for $($package.fileName)."
    }
    if ((Get-Item -LiteralPath $packagePath).Length -ne [long]$package.bytes) {
        throw "Update package byte count mismatch for $($package.fileName)."
    }
}

$signatureName = [string]$manifest.signing.detachedSignatureFile
if ([string]::IsNullOrWhiteSpace($signatureName)) {
    throw "Update manifest does not name a detached signature record."
}
$signaturePath = Join-Path $manifestDirectory $signatureName
if (-not (Test-Path -LiteralPath $signaturePath -PathType Leaf)) {
    throw "Detached update-manifest record is missing: $signaturePath"
}
$signature = Get-Content -LiteralPath $signaturePath -Raw | ConvertFrom-Json
$actualManifestHash = Get-Sha256HexFromFile -Path $resolvedManifest
if ([string]$signature.manifestSha256 -ne $actualManifestHash) {
    throw "Detached update-manifest record does not match the manifest hash."
}
if ([string]$signature.mode -ne $mode -or [string]$signature.status -ne [string]$manifest.signing.status) {
    throw "Detached update-manifest record disagrees with manifest signing metadata."
}
if ([string]$manifest.signing.algorithm -ne "RSA-SHA256" -or [string]$signature.algorithm -ne "RSA-SHA256") {
    throw "Unsupported update-manifest signature algorithm."
}
if ($mode -ne "unsigned") {
    if (-not [bool]$manifest.signing.signaturePresent -or
        [string]::IsNullOrWhiteSpace([string]$signature.signatureBase64) -or
        [string]::IsNullOrWhiteSpace([string]$signature.publicKeySpkiBase64)) {
        throw "Signed update manifest is missing its detached RSA signature or public key."
    }
    $rsa = [System.Security.Cryptography.RSA]::Create()
    try {
        $bytesRead = 0
        $publicKey = [Convert]::FromBase64String([string]$signature.publicKeySpkiBase64)
        $rsa.ImportSubjectPublicKeyInfo($publicKey, [ref]$bytesRead)
        $computedPublisherKeyHash = Get-MuesliPublicKeySha256 -SubjectPublicKeyInfo $publicKey
        if ($computedPublisherKeyHash -ne (Normalize-MuesliSha256 -Hash ([string]$signature.publisherPublicKeySha256))) {
            throw "Detached update-manifest public-key hash does not match the embedded public key."
        }
        if ($computedPublisherKeyHash -ne (Normalize-MuesliSha256 -Hash ([string]$manifest.signing.publisherPublicKeySha256))) {
            throw "Manifest publisher public-key hash does not match the detached signature record."
        }
        $hasExternalPublisherPin = -not [string]::IsNullOrWhiteSpace($ExpectedPublisherPublicKeySha256) -or
            -not [string]::IsNullOrWhiteSpace($ExpectedPublisherCertificateThumbprint)
        if (-not $hasExternalPublisherPin) {
            throw "Signed update manifests require an independently supplied publisher public-key SHA-256 or certificate thumbprint."
        }
        if (-not [string]::IsNullOrWhiteSpace($ExpectedPublisherPublicKeySha256) -and
            $computedPublisherKeyHash -ne (Normalize-MuesliSha256 -Hash $ExpectedPublisherPublicKeySha256)) {
            throw "Update manifest publisher public-key hash did not match the independently supplied pin."
        }
        $embeddedCertificateThumbprint = [string]$signature.publisherCertificateThumbprint
        if (([string]$manifest.signing.publisherCertificateThumbprint) -ne $embeddedCertificateThumbprint) {
            throw "Manifest certificate thumbprint does not match the detached signature record."
        }
        if (([string]$manifest.signing.publisherSubject) -ne ([string]$signature.publisherSubject)) {
            throw "Manifest publisher subject does not match the detached signature record."
        }
        if (-not [string]::IsNullOrWhiteSpace($ExpectedPublisherCertificateThumbprint) -and
            (Normalize-MuesliThumbprint -Thumbprint $embeddedCertificateThumbprint) -ne
            (Normalize-MuesliThumbprint -Thumbprint $ExpectedPublisherCertificateThumbprint)) {
            throw "Update manifest certificate thumbprint did not match the independently supplied pin."
        }
        $signatureBytes = [Convert]::FromBase64String([string]$signature.signatureBase64)
        $manifestBytes = [IO.File]::ReadAllBytes($resolvedManifest)
        if (-not $rsa.VerifyData(
                $manifestBytes,
                $signatureBytes,
                [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)) {
            throw "Detached update-manifest RSA signature verification failed."
        }
    } finally {
        $rsa.Dispose()
    }
}
if ($mode -eq "production" -and (-not [bool]$manifest.signing.authenticodeVerified -or -not [bool]$signature.authenticode)) {
    throw "Production update manifest is not Authenticode-verified."
}

Write-Host "Update manifest verified: $resolvedManifest (mode=$mode status=$($manifest.signing.status))"
