<#
.SYNOPSIS
    Builds and launches the shipping WinUI 3 app.

.DESCRIPTION
    Uses the packaged WinApp launch path required by the Windows App SDK. With no
    ProfileRoot argument, Muesli opens the real production profile at %APPDATA%\muesli.

.PARAMETER ProfileRoot
    Optional isolated profile root. Omit it to use the real Muesli library.
#>
[CmdletBinding()]
param(
    [string]$ProfileRoot,
    [switch]$SkipBuild,
    [int]$Wait = 10,
    # Extra application arguments, e.g. "--preview-meeting-notification=active" or "--background".
    [string]$AppArgs = "",
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'windows-native\Muesli.Windows.WinUI\Muesli.Windows.WinUI.csproj'
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "WinUI project not found at $project" }
if (-not (Get-Command winapp -ErrorAction SilentlyContinue)) {
    throw 'WinApp CLI 0.6 or newer is required. Install it before launching Muesli.'
}

. (Join-Path $PSScriptRoot 'select-packaged-layout.ps1')
$outputRoot = Join-Path (Split-Path -Parent $project) "bin\x64\$Configuration"

Get-Process -Name 'Muesli','Muesli.Windows.WinUI','Muesli.Windows.Indicator.Wpf' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping running Muesli shell (pid $($_.Id))..." -ForegroundColor DarkGray
    $_ | Stop-Process -Force
    $_.WaitForExit(10000) | Out-Null
}

$hadProfileOverride = Test-Path Env:MUESLI_PROFILE_ROOT
$previousProfileOverride = $env:MUESLI_PROFILE_ROOT
if ([string]::IsNullOrWhiteSpace($ProfileRoot)) {
    Remove-Item Env:MUESLI_PROFILE_ROOT -ErrorAction SilentlyContinue
    $profileDescription = Join-Path $env:APPDATA 'muesli'
} else {
    $ProfileRoot = [IO.Path]::GetFullPath($ProfileRoot)
    New-Item -ItemType Directory -Path $ProfileRoot -Force | Out-Null
    $env:MUESLI_PROFILE_ROOT = $ProfileRoot
    $profileDescription = $ProfileRoot
}

try {
    $buildCompletedUtc = $null
    if (-not $SkipBuild) {
        Write-Host "Building Muesli WinUI ($Configuration, x64)..." -ForegroundColor Cyan
        $buildStartedUtc = (Get-Date).ToUniversalTime()
        Push-Location $env:TEMP
        try {
            & dotnet build $project -c $Configuration -p:Platform=x64
            if ($LASTEXITCODE -ne 0) { throw "WinUI build failed with exit code $LASTEXITCODE." }
        } finally {
            Pop-Location
        }
        # An incremental build that produced no new payload still counts: what matters is that the
        # layout we launch is not older than the newest compiler output for this configuration.
        $newestPayload = Get-ChildItem -LiteralPath $outputRoot -Filter 'Muesli.Windows.WinUI.dll' -File -Recurse -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        $buildCompletedUtc = if ($newestPayload) { $newestPayload.LastWriteTimeUtc } else { $buildStartedUtc }
    }

    $selectorArguments = @{ OutputRoot = $outputRoot }
    if ($buildCompletedUtc) {
        # The layout must belong to the build that just ran, never an older nested copy.
        $selectorArguments['MinimumDllWriteTimeUtc'] = $buildCompletedUtc
    }
    $manifestPath = Select-MuesliPackagedLayout @selectorArguments
    $manifest = Get-Item -LiteralPath $manifestPath
    $payload = Get-Item -LiteralPath (Join-Path $manifest.Directory.FullName 'Muesli.Windows.WinUI.dll')
    Write-Host "Layout: $($manifest.Directory.FullName)" -ForegroundColor DarkGray
    Write-Host "Payload built: $($payload.LastWriteTime)" -ForegroundColor DarkGray

    $arguments = @('run', $manifest.Directory.FullName, '--detach', '--json')
    $forwarded = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($ProfileRoot)) {
        $forwarded.Add("--profile-root=$ProfileRoot")
    }
    if (-not [string]::IsNullOrWhiteSpace($AppArgs)) {
        $forwarded.Add($AppArgs)
    }
    if ($forwarded.Count -gt 0) {
        $arguments += @('--args', ($forwarded -join ' '))
    }

    Write-Host "Launching Muesli WinUI ($Configuration, x64)..." -ForegroundColor Cyan
    Write-Host "Profile: $profileDescription" -ForegroundColor DarkGray
    $json = & winapp @arguments
    if ($LASTEXITCODE -ne 0) { throw "WinApp launch failed with exit code $LASTEXITCODE." }
    $result = $json | ConvertFrom-Json
} finally {
    if ($hadProfileOverride) {
        $env:MUESLI_PROFILE_ROOT = $previousProfileOverride
    } else {
        Remove-Item Env:MUESLI_PROFILE_ROOT -ErrorAction SilentlyContinue
    }
}

Start-Sleep -Seconds $Wait
$process = Get-Process -Name 'Muesli.Windows.WinUI' -ErrorAction SilentlyContinue |
    Sort-Object StartTime -Descending |
    Select-Object -First 1
if (-not $process) { throw "Muesli exited within $Wait second(s). Check $profileDescription\logs for startup details." }

[pscustomobject]@{
    ProcessId = $process.Id
    ProfileRoot = $profileDescription
    AppUserModelId = $result.AUMID
    WindowTitle = $process.MainWindowTitle
}
