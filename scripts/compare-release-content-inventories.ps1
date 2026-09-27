<##
.SYNOPSIS
Compares two package outputs from isolated release checkouts.

ZIP entry timestamps and checkout paths are ignored. File names, byte counts, and
SHA-256 content hashes are compared, producing a stable contentDigest.
##>
param(
    [Parameter(Mandatory = $true)]
    [string]$LeftPath,
    [Parameter(Mandatory = $true)]
    [string]$RightPath,
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "release-common.ps1")

function Read-InventoryForPath {
    param([string]$Path)
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved)) {
        throw "Package output was not found: $resolved"
    }
    if ((Get-Item -LiteralPath $resolved).PSIsContainer) {
        return Get-MuesliDirectoryContentInventory -DirectoryPath $resolved
    }
    if ([IO.Path]::GetExtension($resolved) -in @(".zip", ".msix", ".appx", ".msixbundle")) {
        # MSIX/APPX are ZIP containers; the inventory hashes entry names, sizes and bytes.
        return Get-MuesliZipContentInventory -ZipPath $resolved
    }
    if ([IO.Path]::GetExtension($resolved) -ieq ".json") {
        return (Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json)
    }
    throw "Expected a package directory, ZIP, or inventory JSON: $resolved"
}

$left = Read-InventoryForPath -Path $LeftPath
$right = Read-InventoryForPath -Path $RightPath
$comparison = Compare-MuesliContentInventories -Left $left -Right $right
$comparison = [ordered]@{
    schemaVersion = 1
    leftPath = [IO.Path]::GetFileName([IO.Path]::GetFullPath($LeftPath))
    rightPath = [IO.Path]::GetFileName([IO.Path]::GetFullPath($RightPath))
    leftDigest = $comparison.leftDigest
    rightDigest = $comparison.rightDigest
    matched = [bool]$comparison.matched
    differenceCount = [int]$comparison.differenceCount
    differences = @($comparison.differences)
    note = "Compare contentDigest; ZIP file hashes and checkout paths may differ while package contents match."
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $root "artifacts\package-content-inventory-comparison.json"
}
Write-Utf8NoBomFile -Path $OutputPath -Content (($comparison | ConvertTo-Json -Depth 10) + "`n")
if (-not $comparison.matched) {
    throw "Release package content inventories do not match. See $OutputPath"
}
Write-Host "Release package content inventories matched (digest $($comparison.leftDigest))."
