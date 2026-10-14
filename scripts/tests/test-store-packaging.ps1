# Cross-platform manifest behavior tests. No Windows SDK, Store credentials or signing calls.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scripts = Split-Path -Parent $PSScriptRoot
$root = Split-Path -Parent $scripts
. (Join-Path $scripts 'store-package-common.ps1')
. (Join-Path $scripts 'read-release-properties.ps1')
$store = Read-MuesliStoreIdentity -Path (Join-Path $root 'windows-native/store-identity.json')
$version = "$((Get-MuesliReleaseProperties -Root $root).Version).0"
$source = Join-Path $root 'windows-native/Muesli.Windows.WinUI/Package.appxmanifest'
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('muesli-store-test-' + [guid]::NewGuid().ToString('N'))
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    try { & $Action | Out-Null } catch {
        Assert-True ($_.Exception.Message -like "*$Message*") "Unexpected failure: $($_.Exception.Message)"
        return
    }
    throw "Expected rejection containing: $Message"
}
try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    $path = Join-Path $scratch 'AppxManifest.xml'
    Copy-Item -LiteralPath $source -Destination $path
    Set-MuesliStoreManifestIdentity -ManifestPath $path -StoreIdentity $store
    [xml]$manifest = Get-Content -LiteralPath $path -Raw
    $manifest.Package.Identity.SetAttribute('ProcessorArchitecture', 'x64')
    Assert-MuesliStoreManifestIdentity -Manifest $manifest -StoreIdentity $store -ExpectedVersion $version
    Assert-True ($manifest.Package.Properties.DisplayName -eq 'Muesli: Voice & Meeting Notes') 'Ampersand/display name must round-trip through XML.'
    Assert-True ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -eq $sourceHash) 'Packaging must not modify the development manifest.'
    foreach ($field in @('Name', 'Publisher', 'Version', 'ProcessorArchitecture')) {
        $original = $manifest.Package.Identity.GetAttribute($field)
        $manifest.Package.Identity.SetAttribute($field, 'wrong')
        Assert-Rejected { Assert-MuesliStoreManifestIdentity -Manifest $manifest -StoreIdentity $store -ExpectedVersion $version } 'mismatch'
        $manifest.Package.Identity.SetAttribute($field, $original)
    }
    $manifest.Package.Properties.PublisherDisplayName = 'AppPublisher'
    Assert-Rejected { Assert-MuesliStoreManifestIdentity -Manifest $manifest -StoreIdentity $store -ExpectedVersion $version } 'PublisherDisplayName mismatch'
    $manifest.Package.Properties.PublisherDisplayName = $store.publisherDisplayName
    $manifest.Package.Applications.Application.VisualElements.DisplayName = 'Muesli'
    Assert-Rejected { Assert-MuesliStoreManifestIdentity -Manifest $manifest -StoreIdentity $store -ExpectedVersion $version } 'display name mismatch'
    $manifest.Package.Applications.Application.VisualElements.DisplayName = $store.displayName
    $manifest.Package.Identity.Version = '0.3.0.1'
    Assert-Rejected { Assert-MuesliStoreManifestIdentity -Manifest $manifest -StoreIdentity $store -ExpectedVersion '0.3.0.1' } 'final component'
    $invalidPath = Join-Path $scratch 'invalid.json'
    $invalid = $store | ConvertTo-Json | ConvertFrom-Json
    $invalid.publisher = 'CN=AppPublisher'
    $invalid | ConvertTo-Json | Set-Content -LiteralPath $invalidPath
    Assert-Rejected { Read-MuesliStoreIdentity -Path $invalidPath } 'Invalid Partner Center'
    $trxPath = Join-Path $scratch 'tests.trx'
    $trxText = '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary outcome="Completed"><Counters passed="2" failed="0" executed="2" notExecuted="1" error="0" timeout="0" aborted="0" /></ResultSummary></TestRun>'
    [IO.File]::WriteAllText($trxPath, $trxText)
    $counts = Read-MuesliPassingWindowsTestResults -Path $trxPath
    Assert-True ($counts.passed -eq 2 -and $counts.notExecuted -eq 1) 'TRX must preserve skipped test disclosure.'
    foreach ($mutation in @(
        @('outcome="Completed"', 'outcome="Failed"'),
        @('failed="0"', 'failed="1"'),
        @('executed="2"', 'executed="0"'),
        @('aborted="0"', 'aborted="1"')
    )) {
        [IO.File]::WriteAllText($trxPath, $trxText.Replace($mutation[0], $mutation[1]))
        Assert-Rejected { Read-MuesliPassingWindowsTestResults -Path $trxPath } $(if ($mutation[0] -like 'aborted*') { 'aborted' } else { 'passing Windows test run' })
    }
    Write-Host 'Store packaging behavioral tests passed (16 scenarios).'
} finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
