param(
    [string]$ZipPath = "",
    [string]$WorkDir = "$env:TEMP\muesli-v1-qa",
    [switch]$SkipLaunch
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
if ([string]::IsNullOrWhiteSpace($ZipPath)) {
    $ZipPath = Join-Path $root "artifacts\muesli-windows-0.2.0-win-x64.zip"
}

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
    "SHIP-CHECKLIST.txt",
    "WINDOWS-PRIVACY.md",
    "THIRD-PARTY-NOTICES.md",
    "licenses\MIT.txt",
    "licenses\Apache-2.0.txt",
    "licenses\BSD-3-Clause.txt",
    "licenses\CC-BY-4.0.txt",
    "licenses\OFL-1.1.txt",
    "licenses\QuestPDF-2026.5.0.md",
    "install-parakeet-cuda-runtime.ps1",
    "install-windows.ps1",
    "uninstall-windows.ps1",
    "Assets\menu_m_template@2x.png",
    "native-sherpa-cuda\native-sherpa-cuda-runtime.json"
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
    "qualify-dictation-target.ps1"
)
foreach ($item in $forbidden) {
    $path = Join-Path $WorkDir $item
    if (Test-Path $path) {
        throw "Package includes forbidden Python-era artifact: $item"
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

$sherpaCudaManifestPath = Join-Path $WorkDir "native-sherpa-cuda\native-sherpa-cuda-runtime.json"
$sherpaCudaManifest = Get-Content -LiteralPath $sherpaCudaManifestPath -Raw | ConvertFrom-Json
if ($sherpaCudaManifest.schemaVersion -ne 1 -or
    $sherpaCudaManifest.runtimeKind -ne "native-sherpa-onnx-cuda" -or
    $sherpaCudaManifest.runtimeVersion -ne "1.13.4" -or
    $sherpaCudaManifest.cudaMajor -ne 12 -or
    $sherpaCudaManifest.cudnnMajor -ne 9) {
    throw "Sherpa CUDA runtime manifest is invalid: $sherpaCudaManifestPath"
}
$sherpaCudaIncluded = @($sherpaCudaManifest.requiredRuntimeFiles | Where-Object {
    Test-Path -LiteralPath (Join-Path (Split-Path -Parent $sherpaCudaManifestPath) $_)
})
if ($sherpaCudaIncluded.Count -gt 0) {
    throw "CPU-only public package includes Sherpa CUDA runtime files: $($sherpaCudaIncluded -join ', ')"
}
Write-Host "CPU-only public package excludes Sherpa CUDA runtime files."

$installScript = Get-Content (Join-Path $WorkDir "install-windows.ps1") -Raw
[scriptblock]::Create($installScript) | Out-Null

$uninstallScript = Get-Content (Join-Path $WorkDir "uninstall-windows.ps1") -Raw
[scriptblock]::Create($uninstallScript) | Out-Null

$parakeetCudaScript = Get-Content (Join-Path $WorkDir "install-parakeet-cuda-runtime.ps1") -Raw
[scriptblock]::Create($parakeetCudaScript) | Out-Null

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

Write-Host "Package QA passed: $ZipPath"
Write-Host "Extracted to: $WorkDir"
