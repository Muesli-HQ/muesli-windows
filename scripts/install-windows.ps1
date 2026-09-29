# Installs the shipping WinUI MSIX for the current user.
#
# A production/public install requires a valid signed MSIX. Unsigned development sideloading is
# allowed only with -AllowUnsignedDevelopment and Developer Mode enabled, and is never release
# evidence.

param(
    [string]$MsixPath = "",
    [string]$ExpectedPublisher = "",
    [switch]$AllowUnsignedDevelopment
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "release-common.ps1")

if ([string]::IsNullOrWhiteSpace($MsixPath)) {
    $discovered = Get-MuesliLatestMsix -SearchRoot (Join-Path $root "artifacts\msix")
    if ($null -eq $discovered) { throw "No WinUI MSIX was found under artifacts\msix. Build it first." }
    $MsixPath = $discovered.FullName
}
$MsixPath = (Resolve-Path -LiteralPath $MsixPath).Path

$identity = Get-MuesliMsixManifestIdentity -MsixPath $MsixPath
$signature = Get-AuthenticodeSignature -LiteralPath $MsixPath
$signed = $null -ne $signature -and [string]$signature.Status -eq "Valid"

if ($signed) {
    if (-not [string]::IsNullOrWhiteSpace($ExpectedPublisher) -and
        -not [string]::Equals([string]$identity.Publisher, $ExpectedPublisher.Trim(), [StringComparison]::OrdinalIgnoreCase)) {
        throw "MSIX publisher '$($identity.Publisher)' does not match expected publisher '$ExpectedPublisher'."
    }
    Add-AppxPackage -Path $MsixPath
    Write-Host "Installed signed Muesli MSIX ($($identity.Name) $($identity.Version), publisher $($identity.Publisher))."
    return
}

if (-not $AllowUnsignedDevelopment) {
    $status = if ($null -eq $signature) { "Unavailable" } else { [string]$signature.Status }
    throw "Refusing to install the uncertified MSIX (Authenticode status '$status'). Public install requires a validly signed MSIX. For local development, pass -AllowUnsignedDevelopment with Developer Mode enabled."
}

$devMode = $false
try {
    $devMode = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Name AllowDevelopmentWithoutDevLicense -ErrorAction Stop).AllowDevelopmentWithoutDevLicense -eq 1
} catch {
    $devMode = $false
}
if (-not $devMode) {
    throw "Unsigned development sideloading requires Developer Mode. Enable it or install a signed MSIX."
}

Add-AppxPackage -Path $MsixPath
Write-Warning "Installed an UNSIGNED development MSIX. This is not production or release evidence."
