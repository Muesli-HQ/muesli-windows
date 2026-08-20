param(
    [string]$ZipPath = "",
    [string]$WorkDir = "",
    [string]$ReportPath = "",
    [switch]$SkipLaunch
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
. (Join-Path $PSScriptRoot "read-release-properties.ps1")
. (Join-Path $PSScriptRoot "release-common.ps1")
$release = Get-MuesliReleaseProperties -Root $root
if ([string]::IsNullOrWhiteSpace($ZipPath)) {
    $ZipPath = Join-Path $root "artifacts\muesli-windows-$($release.Version)-win-x64.zip"
}
if ([string]::IsNullOrWhiteSpace($WorkDir)) {
    $WorkDir = Join-Path $env:TEMP "muesli-$($release.Channel)-qa"
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $root "artifacts\package-smoke-report.json"
}

$smoke = [ordered]@{
    schemaVersion = 1
    passed = $false
    zipPath = $ZipPath
    workDir = $WorkDir
    skipLaunch = [bool]$SkipLaunch
    startedAtUtc = [DateTime]::UtcNow.ToString("o")
    nativeRuntime = $null
    inventoryPassed = $false
    cudaProviderIncluded = $false
    cpuProviderIncluded = $true
    signed = $false
    failures = @()
}

function Write-MuesliSmokeReport {
    param($Report)
    $Report.finishedAtUtc = [DateTime]::UtcNow.ToString("o")
    Write-Utf8NoBomFile -Path $ReportPath -Content (($Report | ConvertTo-Json -Depth 8) + "`n")
}

try {
if (-not (Test-Path $ZipPath)) {
    throw "Package not found: $ZipPath"
}

if (Test-Path $WorkDir) {
    Get-Process Muesli -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    try {
        Remove-Item -LiteralPath $WorkDir -Recurse -Force
    } catch {
        $timestamp = Get-Date -Format "yyyyMMddHHmmss"
        $WorkDir = "$WorkDir-$timestamp"
        Write-Warning "Could not clean previous QA directory. Using '$WorkDir' instead. Last error: $($_.Exception.Message)"
    }
}
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
Expand-Archive -LiteralPath $ZipPath -DestinationPath $WorkDir -Force

$required = @(
    "Muesli.exe",
    "Muesli.dll",
    "README-WINDOWS.txt",
    "RELEASE-NOTES.txt",
    $release.MetadataFileName,
    "SHIP-CHECKLIST.txt",
    "WINDOWS-PRIVACY.md",
    "THIRD-PARTY-NOTICES.md",
    "licenses\MIT.txt",
    "licenses\Apache-2.0.txt",
    "licenses\BSD-3-Clause.txt",
    "licenses\CC-BY-4.0.txt",
    "licenses\OFL-1.1.txt",
    "licenses\QuestPDF-2026.5.0.md",
    "licenses\Zlib.txt",
    "licenses\SQLite-blessing.txt",
    "licenses\GCC-Runtime-Library-Exception-3.1.txt",
    "licenses\MinGW-w64-winpthread-MIT.txt",
    "native-runtime-inventory.json",
    "install-windows.ps1",
    "uninstall-windows.ps1",
    "Assets\menu_m_template@2x.png"
)

foreach ($item in $required) {
    $path = Join-Path $WorkDir $item
    if (-not (Test-Path $path)) {
        throw "Package missing required file: $item"
    }
}

$forbidden = @(
    "setup-worker-runtime.ps1",
    "worker",
    ".venv",
    "test-windows-package.ps1",
    "fresh-machine-qa.ps1",
    "benchmark-native-transcription.ps1",
    "benchmark-native-meeting.ps1",
    "test-media-imports.ps1",
    "qualify-windows-release.ps1",
    "summarize-dictation-latency.ps1",
    "test-transcription-corpus.ps1",
    "qualify-dictation-target.ps1",
    "install-parakeet-cuda-runtime.ps1",
    "native-sherpa-cuda"
)
foreach ($item in $forbidden) {
    $path = Join-Path $WorkDir $item
    if (Test-Path $path) {
        throw "Package includes a forbidden internal or unqualified release artifact: $item"
    }
}

$pythonArtifacts = Get-ChildItem -LiteralPath $WorkDir -Recurse -File -Force -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Extension -in @(".py", ".pyc", ".pyo", ".pyd") -or
        $_.Name -like "requirements*.txt" -or
        $_.Name -like "python*.dll" -or
        $_.Name -eq "base_library.zip"
    }
if ($pythonArtifacts) {
    throw "Package includes forbidden Python files: $($pythonArtifacts[0].FullName)"
}

$removedAsrArtifacts = Get-ChildItem -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Name -match "(?i)whisper|ctranslate2|faster-whisper"
    }
if ($removedAsrArtifacts) {
    throw "Package includes a removed transcription backend artifact: $($removedAsrArtifacts[0].FullName)"
}

$debugSymbols = Get-ChildItem -LiteralPath $WorkDir -Recurse -File -Filter "*.pdb" -ErrorAction SilentlyContinue
if ($debugSymbols) {
    throw "Public package includes debug symbols: $($debugSymbols[0].FullName)"
}

$foreignRuntimeAssets = Get-ChildItem -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match "(?i)\\runtimes\\(android|linux|osx|win-arm64|win-x86)(\\|$)" }
if ($foreignRuntimeAssets) {
    throw "win-x64 package includes a foreign runtime asset: $($foreignRuntimeAssets[0].FullName)"
}

function Assert-NativeRuntimeFile {
    param(
        [string]$Pattern,
        [string]$Description
    )

    $match = Get-ChildItem -LiteralPath $WorkDir -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like $Pattern } |
        Select-Object -First 1
    if (-not $match) {
        throw "Package missing native runtime dependency: $Description ($Pattern)"
    }
}

Assert-NativeRuntimeFile -Pattern "sherpa-onnx.dll" -Description "sherpa-onnx managed runtime"
Assert-NativeRuntimeFile -Pattern "sherpa-onnx-c-api.dll" -Description "sherpa-onnx native runtime"
Assert-NativeRuntimeFile -Pattern "onnxruntime.dll" -Description "ONNX Runtime native runtime"
Assert-NativeRuntimeFile -Pattern "LLamaSharp.dll" -Description "LLamaSharp managed cleanup runtime"
Assert-NativeRuntimeFile -Pattern "llama.dll" -Description "llama.cpp native cleanup runtime"

$forbiddenCudaFiles = @(
    "onnxruntime_providers_cuda.dll",
    "onnxruntime_providers_shared.dll",
    "cublas64_12.dll",
    "cudart64_12.dll",
    "cudnn64_9.dll"
)
$cudaHits = Get-ChildItem -LiteralPath $WorkDir -Recurse -File -Force -ErrorAction SilentlyContinue |
    Where-Object { $forbiddenCudaFiles -contains $_.Name }
if ($cudaHits) {
    throw "Public CPU package includes a CUDA/NVIDIA runtime file: $($cudaHits[0].FullName)"
}

$notices = Get-Content -LiteralPath (Join-Path $WorkDir "THIRD-PARTY-NOTICES.md") -Raw
if ($notices -match "The primary Muesli package includes the version-matched sherpa-onnx CUDA provider") {
    throw "Packaged THIRD-PARTY-NOTICES.md still claims the public package includes a CUDA provider."
}
if ($notices -notmatch "CPU Sherpa") {
    throw "Packaged THIRD-PARTY-NOTICES.md does not disclose the public CPU-only Sherpa provider."
}

$inventory = Get-Content -LiteralPath (Join-Path $WorkDir "native-runtime-inventory.json") -Raw | ConvertFrom-Json
if (-not $inventory.passed -or $inventory.publicPackage.cudaProviderIncluded -ne $false) {
    throw "Packaged native-runtime inventory does not record a passing CPU-only public package."
}

$inventoryCheck = Join-Path $env:TEMP "muesli-native-inventory-check.json"
& (Join-Path $PSScriptRoot "generate-native-runtime-inventory.ps1") `
    -PackageDirectory $WorkDir `
    -OutputPath $inventoryCheck
if ($LASTEXITCODE -ne 0) {
    throw "Extracted package failed native-runtime inventory regeneration."
}

$installScript = Get-Content (Join-Path $WorkDir "install-windows.ps1") -Raw
[scriptblock]::Create($installScript) | Out-Null

$uninstallScript = Get-Content (Join-Path $WorkDir "uninstall-windows.ps1") -Raw
[scriptblock]::Create($uninstallScript) | Out-Null

$nativeDiagnosticPath = Join-Path $WorkDir "package-native-runtime-diagnostic.json"
$diagnosticStartInfo = [Diagnostics.ProcessStartInfo]::new()
$diagnosticStartInfo.FileName = Join-Path $WorkDir "Muesli.exe"
$diagnosticStartInfo.WorkingDirectory = $WorkDir
$diagnosticStartInfo.UseShellExecute = $false
$diagnosticStartInfo.CreateNoWindow = $true
$diagnosticStartInfo.Arguments = "--diagnose-native --output `"$nativeDiagnosticPath`""
$diagnosticProcess = [Diagnostics.Process]::Start($diagnosticStartInfo)
$diagnosticProcess.WaitForExit()
if ($diagnosticProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $nativeDiagnosticPath)) {
    throw "Packaged native runtime diagnostic failed with exit code $($diagnosticProcess.ExitCode)."
}
$nativeDiagnostic = Get-Content -LiteralPath $nativeDiagnosticPath -Raw | ConvertFrom-Json
if (-not $nativeDiagnostic.Report.Passed -or -not $nativeDiagnostic.Report.RuntimeAvailable) {
    throw "Packaged native runtime could not load: $($nativeDiagnostic.Report.Failures -join '; ')"
}
$missingDiagnosticRuntimeFiles = @($nativeDiagnostic.Report.RuntimeFiles | Where-Object { -not $_.Exists })
if ($missingDiagnosticRuntimeFiles.Count -gt 0) {
    throw "Packaged runtime diagnostic could not resolve: $($missingDiagnosticRuntimeFiles.FileName -join ', ')"
}
Write-Host "Packaged native runtime loaded successfully through $($nativeDiagnostic.Report.SelectedRuntime)."

$freshMachineQaScript = Join-Path $PSScriptRoot "fresh-machine-qa.ps1"
& $freshMachineQaScript -InstallDir $WorkDir

if (-not $SkipLaunch) {
    $process = Start-Process -FilePath (Join-Path $WorkDir "Muesli.exe") -WorkingDirectory $WorkDir -PassThru
    Start-Sleep -Seconds 8
    if ($process.HasExited) {
        throw "Packaged app exited during smoke test with code $($process.ExitCode)"
    }

    Get-Process Muesli -ErrorAction SilentlyContinue | Stop-Process -Force
}

$smoke.passed = $true
$smoke.inventoryPassed = $true
$smoke.workDir = $WorkDir
$smoke.nativeRuntime = $nativeDiagnostic.Report.SelectedRuntime
$smoke.cpuProviderIncluded = $true
$smoke.cudaProviderIncluded = $false
Write-MuesliSmokeReport $smoke
Write-Host "Package QA passed: $ZipPath"
Write-Host "Extracted to: $WorkDir"
Write-Host "Wrote package smoke report: $ReportPath"
} catch {
    $smoke.passed = $false
    $smoke.workDir = $WorkDir
    $smoke.failures = @("$($_.Exception.Message)")
    Write-MuesliSmokeReport $smoke
    throw
}
