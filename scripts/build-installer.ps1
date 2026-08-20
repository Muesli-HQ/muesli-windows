param(
    [string]$InnoSetupCompiler = "",
    [switch]$SkipPackage,
    [switch]$AllowDirty
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
. (Join-Path $PSScriptRoot "read-release-properties.ps1")
. (Join-Path $PSScriptRoot "release-common.ps1")
$release = Get-MuesliReleaseProperties -Root $root
$packageScript = Join-Path $root "scripts\package-windows-v1.ps1"
$installerScript = Join-Path $root "installers\muesli-windows.iss"
$lastPublishFile = Join-Path $root "artifacts\last-publish-dir.txt"

if (-not $SkipPackage) {
    & $packageScript -AllowDirty:$AllowDirty
}

if ([string]::IsNullOrWhiteSpace($InnoSetupCompiler)) {
    $command = Get-Command iscc -ErrorAction SilentlyContinue
    if ($command) {
        $InnoSetupCompiler = $command.Source
    } else {
        $candidates = @(
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
            "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
            "C:\Program Files\Inno Setup 6\ISCC.exe"
        )
        $InnoSetupCompiler = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
}

if ($SkipPackage -and -not (Test-Path -LiteralPath $lastPublishFile)) {
    throw "SkipPackage was set but artifacts/last-publish-dir.txt is missing. Run package-windows-v1.ps1 first."
}

if ([string]::IsNullOrWhiteSpace($InnoSetupCompiler) -or -not (Test-Path $InnoSetupCompiler)) {
    throw "Inno Setup compiler was not found. Install Inno Setup or pass -InnoSetupCompiler with the path to ISCC.exe."
}

$publishSource = if (Test-Path $lastPublishFile) {
    (Get-Content -LiteralPath $lastPublishFile -Raw).Trim()
} else {
    Join-Path $root "publish\muesli-windows-win-x64"
}

& $InnoSetupCompiler "/DPublishSource=$publishSource" "/DMyAppVersion=$($release.Version)" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

$installer = Get-ChildItem -LiteralPath (Join-Path $root "artifacts") -Filter "MuesliSetup-*-win-x64.exe" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($installer) {
    Write-Host "Created $($installer.FullName)"
}
