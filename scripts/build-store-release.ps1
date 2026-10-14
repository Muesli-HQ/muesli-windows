# Builds the exact unsigned MSIX to upload to Partner Center. No signing credentials are used.
[CmdletBinding()]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Configuration -ne 'Release') { throw 'Store submissions require a Release build.' }
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'release-common.ps1')
. (Join-Path $PSScriptRoot 'store-package-common.ps1')
$identity = Read-MuesliStoreIdentity -Path (Join-Path $root 'windows-native\store-identity.json')
& (Join-Path $PSScriptRoot 'tests\test-store-packaging.ps1')
& (Join-Path $PSScriptRoot 'rehearse-windows-release.ps1') -Configuration Release -Store -SkipLaunch -SkipTwoBuildCompare
$artifacts = Join-Path $root 'artifacts'
$rehearsal = Get-Content -LiteralPath (Join-Path $artifacts 'release-rehearsal-report.json') -Raw | ConvertFrom-Json
if (-not $rehearsal.msixSmokePassed -or -not $rehearsal.nativeInventoryPassed -or $rehearsal.dirtyTree) {
    throw 'Store package requires a clean checkout and passing payload/native checks.'
}
$trxPath = Join-Path $artifacts 'test-results\windows-tests.trx'
$testCounters = Read-MuesliPassingWindowsTestResults -Path $trxPath
$report = [ordered]@{
    schemaVersion = 1
    storeId = $identity.storeId
    storeIdentity = $identity
    gitHead = $rehearsal.gitHead
    package = [IO.Path]::GetFileName($rehearsal.msixPath)
    sha256 = Get-Sha256HexFromFile -Path $rehearsal.msixPath
    version = $rehearsal.msixIdentity.version
    unsignedForStoreSubmission = $true
    payloadChecksPassed = $true
    productTestsPassed = $true
    testCounters = $testCounters
    storeCertificationPerformed = $false
    interactiveQualificationPerformed = $false
    remainingSteps = @('Windows App Certification Kit and interactive qualification', 'Partner Center draft/package upload', 'Certification and manual publication')
}
Write-Utf8NoBomFile -Path (Join-Path $artifacts 'store-submission-report.json') -Content (($report | ConvertTo-Json -Depth 8) + "`n")
Write-Host "Store upload package: $($rehearsal.msixPath)"
Write-Host 'No certificate or signing subscription is required. Store certification and interactive testing are still outstanding.'
