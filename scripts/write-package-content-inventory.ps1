param(
    [Parameter(Mandatory = $true)]
    [string]$ZipPath,
    [string]$OutputPath = "",
    [string]$ComparePath = ""
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
. (Join-Path $PSScriptRoot "release-common.ps1")

if (-not (Test-Path -LiteralPath $ZipPath)) {
    throw "ZIP not found: $ZipPath"
}

$inventory = Get-MuesliZipContentInventory -ZipPath $ZipPath
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $root "artifacts\package-content-inventory.json"
}
Write-Utf8NoBomFile -Path $OutputPath -Content (($inventory | ConvertTo-Json -Depth 8) + "`n")
Write-Host "Wrote content inventory: $OutputPath (digest $($inventory.contentDigest))"

if (-not [string]::IsNullOrWhiteSpace($ComparePath)) {
    $compareInventory = Get-Content -LiteralPath $ComparePath -Raw | ConvertFrom-Json
    $comparison = Compare-MuesliContentInventories -Left $compareInventory -Right $inventory
    $comparisonPath = Join-Path ([IO.Path]::GetDirectoryName($OutputPath)) "package-content-inventory-comparison.json"
    Write-Utf8NoBomFile -Path $comparisonPath -Content (($comparison | ConvertTo-Json -Depth 8) + "`n")
    if (-not $comparison.matched) {
        throw "Content inventories do not match. See $comparisonPath"
    }
    Write-Host "Content inventories matched (zip-entry timestamps ignored)."
}
