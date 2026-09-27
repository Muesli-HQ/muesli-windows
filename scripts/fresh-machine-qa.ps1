param(
    [string]$InstallDir = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $InstallDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}

if (-not (Test-Path (Join-Path $InstallDir "Muesli.exe"))) {
    throw "Muesli.exe was not found in '$InstallDir'. Run this script from the installed or extracted Muesli folder, or pass -InstallDir."
}

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
    "Assets\menu_m_template@2x.png",
    "install-windows.ps1",
    "uninstall-windows.ps1",
    "install-parakeet-cuda-runtime.ps1",
    "native-sherpa-cuda\native-sherpa-cuda-runtime.json"
)

foreach ($item in $required) {
    $path = Join-Path $InstallDir $item
    if (-not (Test-Path $path)) {
        throw "Missing required file: $item"
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
    $path = Join-Path $InstallDir $item
    if (Test-Path $path) {
        throw "Forbidden internal or legacy artifact found: $item"
    }
}

$pythonArtifacts = Get-ChildItem -LiteralPath $InstallDir -Recurse -File -Force -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Extension -in @(".py", ".pyc", ".pyo", ".pyd") -or
        $_.Name -like "requirements*.txt" -or
        $_.Name -like "python*.dll" -or
        $_.Name -eq "base_library.zip"
    }
if ($pythonArtifacts) {
    throw "Forbidden Python file found: $($pythonArtifacts[0].FullName)"
}

$removedAsrArtifacts = Get-ChildItem -LiteralPath $InstallDir -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Name -match "(?i)whisper|ctranslate2|faster-whisper"
    }
if ($removedAsrArtifacts) {
    throw "Removed transcription backend artifact found: $($removedAsrArtifacts[0].FullName)"
}

function Assert-NativeRuntimeFile {
    param(
        [string]$Pattern,
        [string]$Description
    )

    $match = Get-ChildItem -LiteralPath $InstallDir -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like $Pattern } |
        Select-Object -First 1
    if (-not $match) {
        throw "Missing native runtime dependency: $Description ($Pattern)"
    }
}

Assert-NativeRuntimeFile -Pattern "sherpa-onnx.dll" -Description "sherpa-onnx managed runtime"
Assert-NativeRuntimeFile -Pattern "sherpa-onnx-c-api.dll" -Description "sherpa-onnx native runtime"
Assert-NativeRuntimeFile -Pattern "onnxruntime.dll" -Description "ONNX Runtime native runtime"
Assert-NativeRuntimeFile -Pattern "LLamaSharp.dll" -Description "LLamaSharp managed cleanup runtime"
Assert-NativeRuntimeFile -Pattern "llama.dll" -Description "llama.cpp native cleanup runtime"

$sherpaCudaManifestPath = Join-Path $InstallDir "native-sherpa-cuda\native-sherpa-cuda-runtime.json"
$sherpaCudaManifest = Get-Content -LiteralPath $sherpaCudaManifestPath -Raw | ConvertFrom-Json
if ($sherpaCudaManifest.schemaVersion -ne 1 -or
    $sherpaCudaManifest.runtimeKind -ne "native-sherpa-onnx-cuda" -or
    $sherpaCudaManifest.runtimeVersion -ne "1.13.4") {
    throw "Sherpa CUDA runtime manifest is invalid: $sherpaCudaManifestPath"
}
$sherpaCudaIncluded = @($sherpaCudaManifest.requiredRuntimeFiles | Where-Object {
    Test-Path -LiteralPath (Join-Path (Split-Path -Parent $sherpaCudaManifestPath) $_)
})
if ($sherpaCudaIncluded.Count -gt 0) {
    throw "CPU-only public package includes Sherpa CUDA runtime files: $($sherpaCudaIncluded -join ', ')"
}

$forbiddenPatterns = @("Outlook.Application", "Microsoft.Office.Interop.Outlook", "MAPI")
$sourceFiles = @(
    "install-windows.ps1",
    "uninstall-windows.ps1"
) |
    ForEach-Object { Join-Path $InstallDir $_ } |
    Where-Object { Test-Path $_ }
foreach ($pattern in $forbiddenPatterns) {
    $hit = $sourceFiles | Select-String -Pattern $pattern -SimpleMatch -List | Select-Object -First 1
    if ($hit) {
        throw "Forbidden Outlook/MAPI reference found in $($hit.Path): $pattern"
    }
}

$appData = Join-Path $env:APPDATA "muesli"
$settings = Join-Path $appData "windows-settings.json"
$dataDir = Join-Path $appData "data"
$logsDir = Join-Path $appData "logs"

Write-Host "Fresh-machine QA passed for $InstallDir"
Write-Host "Settings path: $settings"
Write-Host "Data path: $dataDir"
Write-Host "Logs path: $logsDir"
Write-Host "Manual checks still required: onboarding once, explicit model preparation with no auto-activation, role routing, shortcut capture, dictation paste, and meeting detection."
