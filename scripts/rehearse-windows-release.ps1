# L04 one-command non-signing Windows release rehearsal.
# Local and CI must produce the same unsigned Release MSIX from the pinned SDK.
#
#   pwsh -ExecutionPolicy Bypass -File .\scripts\rehearse-windows-release.ps1
#
# Does not sign. Does not claim CUDA is included. Authenticode signing is L05.
# The MSIX is the shipping artifact; the retired WPF-era portable ZIP + Inno companion installer
# are no longer produced here.

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDirectory = "",
    [switch]$AllowDirty,
    [switch]$SkipLaunch,
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [switch]$CompareTwoBuilds,
    [switch]$SkipTwoBuildCompare
)

$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location -LiteralPath $root
. (Join-Path $PSScriptRoot "read-release-properties.ps1")
. (Join-Path $PSScriptRoot "release-common.ps1")
$release = Get-MuesliReleaseProperties -Root $root

$runTwoBuildCompare = $true
if ($SkipTwoBuildCompare) {
    $runTwoBuildCompare = $false
} elseif ($PSBoundParameters.ContainsKey("CompareTwoBuilds")) {
    $runTwoBuildCompare = [bool]$CompareTwoBuilds
} elseif ($env:GITHUB_ACTIONS -eq "true") {
    $runTwoBuildCompare = $false
}

$artifactsDir = Join-Path $root "artifacts"
New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null
$testResultsDir = Join-Path $artifactsDir "test-results"
New-Item -ItemType Directory -Force -Path $testResultsDir | Out-Null
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $artifactsDir "msix"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

$startedAtUtc = [DateTime]::UtcNow
$modelsStatus = Assert-MuesliModelSourcesPresent -Root $root
$sdkVersion = Assert-MuesliPinnedSdk -Root $root
$tree = Assert-MuesliCleanReleaseInputs -Root $root -AllowDirty:$AllowDirty
$sharedCore = Assert-MuesliSharedCoreRevision -Root $root -AllowUnpinnedDevOverride
$env:MUESLI_SHARED_CORE_MODE = if ($sharedCore.BridgeRequired) { 'shared-core' } else { 'managed-fallback' }
# Managed fallback is a shipping decision, not a skip: the packaged app carries the parity-tested
# managed text processor and the Swift bridge must not be staged or required.
if (-not $sharedCore.BridgeRequired) {
    # Quarantine rather than skip: the release must not present stale bridge output as current.
    Write-Host "Shared core mode: $($sharedCore.Mode). Shipping the managed-fallback text processor; the Swift bridge is optional and any stale staging is removed."
}
$head = $tree.Head

Write-Host "Muesli unsigned release rehearsal (L04, MSIX)"
Write-Host "  version=$($release.Version) channel=$($release.Channel) sdk=$sdkVersion head=$head"

$scripts = Get-ChildItem -LiteralPath (Join-Path $root "scripts") -Filter *.ps1 -File
foreach ($script in $scripts) {
    [scriptblock]::Create((Get-Content -LiteralPath $script.FullName -Raw)) | Out-Null
}

$trxPath = Join-Path $testResultsDir "windows-tests.trx"
if (-not $SkipTests) {
    Write-Host "Restoring and running Release tests..."
    dotnet restore (Join-Path $root "windows-native\Muesli.Windows.Tests\Muesli.Windows.Tests.csproj") --force-evaluate
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }
    $testArguments = @(
        "test", (Join-Path $root "windows-native\Muesli.Windows.Tests\Muesli.Windows.Tests.csproj"),
        "-c", $Configuration,
        "--no-restore",
        "--logger", "trx;LogFileName=windows-tests.trx",
        "--results-directory", $testResultsDir
    )
    if ($env:MUESLI_SHARED_CORE_MODE -eq 'managed-fallback') {
        $testArguments += "-p:MuesliRequireSwiftBridge=false"
    }
    dotnet @testArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }
}

$packageScript = Join-Path $root "scripts\package-winui-msix.ps1"
$inventoryScript = Join-Path $root "scripts\write-package-content-inventory.ps1"
$smokeScript = Join-Path $root "scripts\test-winui-msix.ps1"

function Invoke-MuesliMsixBuild {
    param([string]$BuildOutputDirectory)
    Write-Host "Building unsigned WinUI MSIX (CPU-only, $Configuration, $Runtime) into $BuildOutputDirectory ..."
    if (Test-Path -LiteralPath $BuildOutputDirectory) {
        Remove-Item -LiteralPath $BuildOutputDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Force -Path $BuildOutputDirectory | Out-Null
    # package-winui-msix.ps1 emits package descriptor objects; discard them so only the path is returned.
    & $packageScript -Configuration $Configuration -OutputDirectory $BuildOutputDirectory | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "MSIX packaging failed with exit code $LASTEXITCODE." }
    $msix = Get-ChildItem -Path $BuildOutputDirectory -Recurse -Filter 'Muesli.Windows.WinUI_*.msix' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if (-not $msix) { throw "MSIX packaging reported success but produced no .msix under $BuildOutputDirectory." }
    return $msix.FullName
}

$build1Dir = Join-Path $OutputDirectory "build1"
$msixPath = Invoke-MuesliMsixBuild -BuildOutputDirectory $build1Dir
$firstInventoryPath = Join-Path $artifactsDir "package-content-inventory.json"
& $inventoryScript -ZipPath $msixPath -OutputPath $firstInventoryPath
$firstInventory = Get-Content -LiteralPath $firstInventoryPath -Raw | ConvertFrom-Json
$twoBuild = $null

if ($runTwoBuildCompare) {
    Write-Host "Rebuilding the MSIX for content-inventory comparison (zip-entry timestamps ignored)..."
    $build2Dir = Join-Path $OutputDirectory "build2"
    $msixPath2 = Invoke-MuesliMsixBuild -BuildOutputDirectory $build2Dir
    $secondInventoryPath = Join-Path $artifactsDir "package-content-inventory-build2.json"
    & $inventoryScript -ZipPath $msixPath2 -OutputPath $secondInventoryPath
    $secondInventory = Get-Content -LiteralPath $secondInventoryPath -Raw | ConvertFrom-Json
    $twoBuild = Compare-MuesliContentInventories -Left $firstInventory -Right $secondInventory
    $comparisonPath = Join-Path $artifactsDir "package-content-inventory-comparison.json"
    Write-Utf8NoBomFile -Path $comparisonPath -Content (($twoBuild | ConvertTo-Json -Depth 8) + "`n")
    if (-not $twoBuild.matched) {
        throw "Two clean MSIX builds produced different content inventories. See $comparisonPath"
    }
    Write-Host "Two-build content inventories matched (digest $($twoBuild.leftDigest))."
}

# CI skips the optional comparison but still needs one predictable downloadable MSIX.
$shippingMsix = Join-Path $OutputDirectory "Muesli.Windows.WinUI_$($release.Version)_x64.msix"
Copy-Item -LiteralPath $msixPath -Destination $shippingMsix -Force
$msixPath = $shippingMsix

$manifestPath = Join-Path $artifactsDir "muesli-win32-manifest.xml"
& (Join-Path $root "scripts\extract-win32-manifest.ps1") -MsixPath $msixPath -OutputPath $manifestPath

$nativeInventoryPath = Join-Path $artifactsDir "native-runtime-inventory.json"
$smokePath = Join-Path $artifactsDir "msix-smoke-report.json"
& $smokeScript -MsixPath $msixPath -ReportPath $smokePath -NativeInventoryPath $nativeInventoryPath -SkipLaunch:$SkipLaunch
$smoke = Get-Content -LiteralPath $smokePath -Raw | ConvertFrom-Json

# The MSIX is the shipping installer. The retired WPF-era Inno companion installer is not built
# here; -SkipInstaller remains accepted for CLI compatibility and records that explicitly.
$installerNote = if ($SkipInstaller) {
    "Installer step skipped by -SkipInstaller; no installer artifact was produced."
} else {
    "MSIX is the shipping installer. The retired WPF-era Inno companion installer is not produced by this pipeline."
}

$hashes = [ordered]@{
    schemaVersion = 1
    version = $release.Version
    channel = $release.Channel
    runtime = $Runtime
    sdk = $sdkVersion
    gitHead = $head
    signed = $false
    cudaProviderIncluded = $false
    cpuProviderIncluded = $true
    files = @()
}
$hashTargets = @(
    @{ name = "msix"; path = $msixPath },
    @{ name = "contentInventory"; path = $firstInventoryPath },
    @{ name = "nativeInventory"; path = $nativeInventoryPath },
    @{ name = "appxManifest"; path = $manifestPath },
    @{ name = "msixSmokeReport"; path = $smokePath }
)
if (Test-Path -LiteralPath $trxPath) {
    $hashTargets += @{ name = "trx"; path = $trxPath }
}
foreach ($target in $hashTargets) {
    if (-not (Test-Path -LiteralPath $target.path)) { continue }
    $hashes.files += [ordered]@{
        name = $target.name
        fileName = [IO.Path]::GetFileName($target.path)
        bytes = (Get-Item -LiteralPath $target.path).Length
        sha256 = Get-Sha256HexFromFile -Path $target.path
    }
}
$hashesPath = Join-Path $artifactsDir "release-hashes.json"
Write-Utf8NoBomFile -Path $hashesPath -Content (($hashes | ConvertTo-Json -Depth 8) + "`n")

$report = [ordered]@{
    schemaVersion = 1
    module = "L04"
    signed = $false
    shippingInstaller = "msix"
    cudaProviderIncluded = $false
    cpuProviderIncluded = $true
    version = $release.Version
    channel = $release.Channel
    configuration = $Configuration
    runtime = $Runtime
    sdk = $sdkVersion
    gitHead = $head
    dirtyTree = -not $tree.Clean
    allowDirty = [bool]$AllowDirty
    dirtyOverridePath = $tree.OverridePath
    modelsStatus = $modelsStatus
    sharedCorePinned = [bool]$sharedCore.Pinned
    sharedCoreRevision = $sharedCore.Revision
    sharedCoreDevOverride = $sharedCore.DevOverride
    sharedCoreMode = [string]$sharedCore.Mode
    sharedCoreReleaseEvidence = [bool]$sharedCore.Pinned
    msixPath = $msixPath
    msixIdentity = $smoke.identity
    msixSmokePassed = [bool]$smoke.passed
    nativeInventoryPassed = [bool]$smoke.nativeInventoryPassed
    launchVerification = [string]$smoke.launchVerification
    launchReason = [string]$smoke.launchReason
    installerNote = $installerNote
    twoBuildContentInventoriesMatched = if ($null -eq $twoBuild) { $null } else { [bool]$twoBuild.matched }
    twoBuildNote = "MSIX file hashes may differ because the container stores entry timestamps. contentDigest ignores those timestamps."
    legalProductGates = @(Get-MuesliReleaseLegalProductGates -Root $root)
    remainingGates = @(
        "SIGN-01 Authenticode/MSIX signing (L05 / D5)",
        "PKG-01 clean-VM install/upgrade/uninstall (L07)",
        "QuestPDF EXP-01 community-license eligibility (release owner)",
        "UPD-01 v1 manual update policy or app-side updater",
        "AUD-03 audio-interference policy decision"
    )
    artifacts = @(
        "msix/$([IO.Path]::GetFileName($msixPath))",
        "test-results/windows-tests.trx",
        "msix-smoke-report.json",
        "muesli-win32-manifest.xml",
        "release-hashes.json",
        "native-runtime-inventory.json",
        "package-content-inventory.json"
    ) | Where-Object { $_ }
    startedAtUtc = $startedAtUtc.ToString("o")
    finishedAtUtc = [DateTime]::UtcNow.ToString("o")
}
$reportPath = Join-Path $artifactsDir "release-rehearsal-report.json"
Write-Utf8NoBomFile -Path $reportPath -Content (($report | ConvertTo-Json -Depth 12) + "`n")

Write-Host "Unsigned rehearsal complete. Artifacts:"
foreach ($name in $report.artifacts) {
    Write-Host "  artifacts/$name"
}
Write-Host "Report: $reportPath"
