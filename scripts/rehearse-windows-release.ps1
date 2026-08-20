# L04 one-command non-signing Windows release rehearsal.
# Local and CI must produce the same unsigned Release package from the pinned SDK.
#
#   pwsh -ExecutionPolicy Bypass -File .\scripts\rehearse-windows-release.ps1
#
# Does not sign. Does not claim CUDA is included. Authenticode is L05.

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
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

$startedAtUtc = [DateTime]::UtcNow
$modelsStatus = Assert-MuesliModelSourcesPresent -Root $root
$sdkVersion = Assert-MuesliPinnedSdk -Root $root
$tree = Assert-MuesliCleanReleaseInputs -Root $root -AllowDirty:$AllowDirty
$head = $tree.Head

Write-Host "Muesli unsigned release rehearsal (L04)"
Write-Host "  version=$($release.Version) channel=$($release.Channel) sdk=$sdkVersion head=$head"

$scripts = Get-ChildItem -LiteralPath (Join-Path $root "scripts") -Filter *.ps1 -File
foreach ($script in $scripts) {
    [scriptblock]::Create((Get-Content -LiteralPath $script.FullName -Raw)) | Out-Null
}

$trxPath = Join-Path $testResultsDir "windows-tests.trx"
if (-not $SkipTests) {
    Write-Host "Restoring and running Release tests..."
    dotnet restore (Join-Path $root "windows-native\Muesli.Windows.Tests\Muesli.Windows.Tests.csproj")
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }
    dotnet test (Join-Path $root "windows-native\Muesli.Windows.Tests\Muesli.Windows.Tests.csproj") `
        -c $Configuration `
        --no-restore `
        --logger "trx;LogFileName=windows-tests.trx" `
        --results-directory $testResultsDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }
}

$packageScript = Join-Path $root "scripts\package-windows-v1.ps1"
Write-Host "Building public CPU-only win-x64 package..."
& $packageScript -Configuration $Configuration -Runtime $Runtime -AllowDirty:$AllowDirty

$zipName = "muesli-windows-$($release.Version)-$Runtime.zip"
$zipPath = Join-Path $artifactsDir $zipName
$firstInventoryPath = Join-Path $artifactsDir "package-content-inventory.json"
$firstInventory = Get-Content -LiteralPath $firstInventoryPath -Raw | ConvertFrom-Json
$twoBuild = $null

if ($runTwoBuildCompare) {
    Write-Host "Rebuilding package for content-inventory comparison (zip-entry timestamps ignored)..."
    $firstCopy = Join-Path $artifactsDir "package-content-inventory-build1.json"
    Copy-Item -LiteralPath $firstInventoryPath -Destination $firstCopy -Force
    & $packageScript -Configuration $Configuration -Runtime $Runtime -AllowDirty:$AllowDirty
    $secondInventory = Get-Content -LiteralPath $firstInventoryPath -Raw | ConvertFrom-Json
    $twoBuild = Compare-MuesliContentInventories -Left $firstInventory -Right $secondInventory
    $comparisonPath = Join-Path $artifactsDir "package-content-inventory-comparison.json"
    Write-Utf8NoBomFile -Path $comparisonPath -Content (($twoBuild | ConvertTo-Json -Depth 8) + "`n")
    if (-not $twoBuild.matched) {
        throw "Two clean package builds produced different content inventories. See $comparisonPath"
    }
    Write-Host "Two-build content inventories matched (digest $($twoBuild.leftDigest))."
}

$publishDir = (Get-Content -LiteralPath (Join-Path $artifactsDir "last-publish-dir.txt") -Raw).Trim()
$exePath = Join-Path $publishDir "Muesli.exe"
$manifestPath = Join-Path $artifactsDir "muesli-win32-manifest.xml"
& (Join-Path $root "scripts\extract-win32-manifest.ps1") -ExePath $exePath -OutputPath $manifestPath

$installerPath = Join-Path $artifactsDir "MuesliSetup-$($release.Version)-win-x64.exe"
$installerBuilt = $false
if (-not $SkipInstaller) {
    Write-Host "Building unsigned Inno installer (signing is L05 / SIGN-01 and is not performed here)..."
    & (Join-Path $root "scripts\build-installer.ps1") -SkipPackage -AllowDirty:$AllowDirty
    $installerBuilt = Test-Path -LiteralPath $installerPath
    if (-not $installerBuilt) {
        throw "Unsigned installer was not produced at $installerPath."
    }
}

$smokePath = Join-Path $artifactsDir "package-smoke-report.json"
& (Join-Path $root "scripts\test-windows-package.ps1") -ZipPath $zipPath -ReportPath $smokePath -SkipLaunch:$SkipLaunch

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
    @{ name = "portableZip"; path = $zipPath },
    @{ name = "contentInventory"; path = $firstInventoryPath },
    @{ name = "nativeInventory"; path = (Join-Path $artifactsDir "native-runtime-inventory.json") },
    @{ name = "win32Manifest"; path = $manifestPath },
    @{ name = "packageSmokeReport"; path = $smokePath }
)
if ($installerBuilt) {
    $hashTargets += @{ name = "unsignedInstaller"; path = $installerPath }
}
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
    twoBuildContentInventoriesMatched = if ($null -eq $twoBuild) { $null } else { [bool]$twoBuild.matched }
    twoBuildNote = "ZIP file hashes may differ because Compress-Archive stores entry LastWriteTime. contentDigest ignores those timestamps."
    remainingGates = @(
        "SIGN-01 Authenticode signing (L05 / D5)",
        "PKG-01 clean-VM install/upgrade/uninstall (L07)"
    )
    artifacts = @(
        $zipName,
        $(if ($installerBuilt) { [IO.Path]::GetFileName($installerPath) } else { $null }),
        "test-results/windows-tests.trx",
        "package-smoke-report.json",
        "muesli-win32-manifest.xml",
        "release-hashes.json",
        "native-runtime-inventory.json",
        "package-content-inventory.json"
    ) | Where-Object { $_ }
    startedAtUtc = $startedAtUtc.ToString("o")
    finishedAtUtc = [DateTime]::UtcNow.ToString("o")
}
$reportPath = Join-Path $artifactsDir "release-rehearsal-report.json"
Write-Utf8NoBomFile -Path $reportPath -Content (($report | ConvertTo-Json -Depth 8) + "`n")

Write-Host "Unsigned rehearsal complete. Artifacts:"
foreach ($name in $report.artifacts) {
    Write-Host "  artifacts/$name"
}
Write-Host "Report: $reportPath"
