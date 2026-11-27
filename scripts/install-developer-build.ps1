<#
.SYNOPSIS
    Installs an unsigned Muesli for Windows developer build for the current user.

.DESCRIPTION
    Windows refuses to install an unsigned .msix by double-click. With Developer Mode on, it does
    allow registering an unpacked package, which is what this script does:

      1. checks the .msix against SHA256SUMS.txt when that file sits next to it,
      2. unpacks it into %LOCALAPPDATA%\MuesliDeveloperBuild\<version>,
      3. registers it for the current user (no administrator rights).

    The unpacked folder IS the installed app, so it must stay where it is. History and settings
    live in %APPDATA%\muesli and are not touched.

    This file is published with each unsigned release as Install-MuesliDeveloperBuild.ps1 and has
    no dependency on the rest of the repository.

.PARAMETER MsixPath
    The unsigned .msix. Defaults to the single Muesli-Windows-*-unsigned.msix next to this script.

.PARAMETER Force
    Replace an already registered Muesli.Windows package without asking.
#>
[CmdletBinding()]
param(
    [string]$MsixPath = "",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$packageName = "Muesli.Windows"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

if ([string]::IsNullOrWhiteSpace($MsixPath)) {
    $candidates = @(Get-ChildItem -LiteralPath $here -Filter "Muesli-Windows-*-unsigned.msix" -File)
    if ($candidates.Count -ne 1) {
        throw "Expected exactly one Muesli-Windows-*-unsigned.msix next to this script, found $($candidates.Count). Pass -MsixPath."
    }
    $MsixPath = $candidates[0].FullName
}
$MsixPath = (Resolve-Path -LiteralPath $MsixPath).Path
$msixName = [IO.Path]::GetFileName($MsixPath)

# 1. Checksum, when the release's checksum file was downloaded alongside.
$checksumsPath = Join-Path (Split-Path -Parent $MsixPath) "SHA256SUMS.txt"
if (Test-Path -LiteralPath $checksumsPath -PathType Leaf) {
    $expected = $null
    foreach ($line in [IO.File]::ReadAllLines($checksumsPath)) {
        if ($line -match '^([0-9a-fA-F]{64}) [ *](.+)$' -and $Matches[2].Trim() -eq $msixName) {
            $expected = $Matches[1].ToLowerInvariant()
        }
    }
    if ($null -eq $expected) { throw "SHA256SUMS.txt does not list $msixName. Download the files again from the same release." }
    $actual = (Get-FileHash -LiteralPath $MsixPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw "SHA-256 mismatch for $msixName. The download is damaged or was altered; do not install it." }
    Write-Host "Checksum verified."
} else {
    Write-Warning "SHA256SUMS.txt was not found next to the package, so the download was not verified."
}

# 2. Read the identity and refuse anything that is not the Muesli developer package.
$archive = [IO.Compression.ZipFile]::OpenRead($MsixPath)
try {
    $manifestEntry = $archive.Entries | Where-Object { $_.FullName -eq "AppxManifest.xml" } | Select-Object -First 1
    if ($null -eq $manifestEntry) { throw "$msixName has no AppxManifest.xml; it is not an MSIX package." }
    $reader = New-Object IO.StreamReader ($manifestEntry.Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $name = [string]$manifest.Package.Identity.Name
    $version = [string]$manifest.Package.Identity.Version
    if ($name -cne $packageName) { throw "This script installs '$packageName' only; the package is '$name'." }
    # Unpacking drops the signature, so a signed production package must never go through here:
    # it would be registered as a different, unsigned package family.
    $publisher = [string]$manifest.Package.Identity.Publisher
    if ($publisher -cne "CN=AppPublisher") {
        throw "This script installs the unsigned developer build only (publisher 'CN=AppPublisher'); the package publisher is '$publisher'. Install a signed package by opening it."
    }
    if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Unexpected package version '$version'." }

    $developerMode = $false
    try {
        $developerMode = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Name AllowDevelopmentWithoutDevLicense -ErrorAction Stop).AllowDevelopmentWithoutDevLicense -eq 1
    } catch { $developerMode = $false }
    if (-not $developerMode) {
        throw "Developer Mode is off. Turn it on in Settings > System > For developers, then run this script again. An unsigned build cannot be installed without it."
    }

    $existing = @(Get-AppxPackage -Name $packageName)
    if ($existing.Count -gt 0 -and -not $Force) {
        Write-Host "Muesli $($existing[0].Version) is already registered from: $($existing[0].InstallLocation)"
        $answer = Read-Host "Replace it with ${version}? Your history and settings are kept. [y/N]"
        if ($answer -notmatch '^(y|yes)$') { Write-Host "Nothing was changed."; return }
    }
    if (@(Get-Process -Name "Muesli.Windows.WinUI" -ErrorAction SilentlyContinue).Count -gt 0) {
        throw "Muesli is running. Quit it from the tray icon, then run this script again."
    }

    # 3. Unpack. MSIX stores entry names percent-encoded, and the folder must not be left half
    #    written, so unpack into a temporary sibling and swap it in.
    $installRoot = Join-Path $env:LOCALAPPDATA "MuesliDeveloperBuild"
    $target = Join-Path $installRoot $version
    $staging = Join-Path $installRoot (".staging-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    $stagingPrefix = [IO.Path]::GetFullPath($staging).TrimEnd('\') + '\'
    try {
        Write-Host "Unpacking $msixName ..."
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.EndsWith("/")) { continue }
            # Package bookkeeping that only matters inside the archive.
            if ($entry.FullName -in @("[Content_Types].xml", "AppxSignature.p7x", "AppxBlockMap.xml")) { continue }
            if ($entry.FullName -like "AppxMetadata/*") { continue }
            $relative = [Uri]::UnescapeDataString($entry.FullName).Replace('/', '\')
            $destination = [IO.Path]::GetFullPath((Join-Path $staging $relative))
            if (-not $destination.StartsWith($stagingPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "The package contains an entry outside its own folder: $($entry.FullName)"
            }
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $true)
        }
    } catch {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        throw
    }
} finally {
    $archive.Dispose()
}

$previous = $null
if (Test-Path -LiteralPath $target) {
    $previous = Join-Path $installRoot (".previous-" + [Guid]::NewGuid().ToString("N"))
    Move-Item -LiteralPath $target -Destination $previous
}
Move-Item -LiteralPath $staging -Destination $target

Write-Host "Registering Muesli $version ..."
try {
    Add-AppxPackage -Register (Join-Path $target "AppxManifest.xml") -ForceUpdateFromAnyVersion
} catch {
    # Put the earlier files back so an already registered build keeps working.
    Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
    if ($previous) { Move-Item -LiteralPath $previous -Destination $target }
    throw "Windows refused to register the package: $($_.Exception.Message)"
}

# Earlier unpacked versions are no longer registered; remove them and any leftovers.
Get-ChildItem -LiteralPath $installRoot -Directory -Force |
    Where-Object { $_.FullName -ne $target } |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ""
Write-Host "Muesli $version is installed. Start it from the Start menu."
Write-Host "Installed files: $target (do not move or delete this folder while Muesli is installed)."
Write-Host "To remove it: Get-AppxPackage $packageName | Remove-AppxPackage, then delete $installRoot."
