<#
.SYNOPSIS
    Builds this checkout and launches it as a side-by-side development slot (MuesliDevA, …).

.DESCRIPTION
    scripts/run-windows.ps1 registers the build as Muesli.Windows, which replaces whichever
    checkout held that registration and stops the running app. This launcher instead registers
    the build under its own package identity, MuesliDev<Slot>, so a feature branch can be tested
    while the normal Muesli (or another slot) keeps running.

    A slot differs from the normal build in three ways, all derived from its package name at
    run time (see MuesliDevSlot.cs):
      * its profile is %APPDATA%\MuesliDev<Slot>, never the production %APPDATA%\muesli;
      * it has its own single-instance scope;
      * it appears as "Muesli Dev <Slot>" in Start and the startup-task list.
    Downloaded models are shared with the normal build and every other slot: all model caches
    live under %USERPROFILE%\.cache\muesli, outside any profile, so nothing is downloaded twice.

    The first launch of a slot copies the production settings and onboarding state, so the slot
    starts with the same microphone, shortcut and model selection. History is not copied.

.PARAMETER Slot
    Slot letter or short name. Default: A (package MuesliDevA).

.PARAMETER FreshProfile
    Do not seed a new slot profile from the production settings; the slot starts at onboarding.
#>
[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9]{1,8}$')]
    [string]$Slot = 'A',
    [switch]$SkipBuild,
    [switch]$FreshProfile,
    [int]$Wait = 10,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectDirectory = Join-Path $repoRoot 'windows-native\Muesli.Windows.WinUI'
$project = Join-Path $projectDirectory 'Muesli.Windows.WinUI.csproj'
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "WinUI project not found at $project" }

. (Join-Path $PSScriptRoot 'select-packaged-layout.ps1')
$packageName = "MuesliDev$Slot"
$displayName = "Muesli Dev $Slot"
$outputRoot = Join-Path $projectDirectory "bin\x64\$Configuration"
# Outside the configuration output root, so the normal launcher's layout selector never sees it.
$slotRoot = Join-Path $projectDirectory "bin\dev-slots\$packageName"
$slotLayout = Join-Path $slotRoot 'AppX'
$slotManifest = Join-Path $slotRoot 'AppxManifest.xml'
$profileRoot = Join-Path $env:APPDATA $packageName

# Only this slot's process: the normal Muesli and other slots keep running.
Get-Process -Name 'Muesli.Windows.WinUI' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($slotRoot, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object {
        Write-Host "Stopping running $packageName (pid $($_.Id))..." -ForegroundColor DarkGray
        $_ | Stop-Process -Force
        $_.WaitForExit(10000) | Out-Null
    }

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
    $newestPayload = Get-ChildItem -LiteralPath $outputRoot -Filter 'Muesli.Windows.WinUI.dll' -File -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    $buildCompletedUtc = if ($newestPayload) { $newestPayload.LastWriteTimeUtc } else { $buildStartedUtc }
}

$selectorArguments = @{ OutputRoot = $outputRoot }
if ($buildCompletedUtc) { $selectorArguments['MinimumDllWriteTimeUtc'] = $buildCompletedUtc }
$builtManifestPath = Select-MuesliPackagedLayout @selectorArguments
$builtLayout = (Get-Item -LiteralPath $builtManifestPath).Directory.FullName

# The slot identity lives only in this staged manifest; Package.appxmanifest is never edited.
New-Item -ItemType Directory -Path $slotRoot -Force | Out-Null
$manifest = [xml](Get-Content -LiteralPath $builtManifestPath -Raw)
$manifest.Package.Identity.Name = $packageName
$manifest.Package.Properties.DisplayName = $displayName
$application = $manifest.Package.Applications.Application
$application.VisualElements.DisplayName = $displayName
$application.Extensions.Extension.StartupTask.DisplayName = $displayName
$manifest.Save($slotManifest)

if (-not $FreshProfile -and -not (Test-Path -LiteralPath (Join-Path $profileRoot 'windows-settings.json'))) {
    $production = Join-Path $env:APPDATA 'muesli'
    New-Item -ItemType Directory -Path $profileRoot -Force | Out-Null
    foreach ($name in 'windows-settings.json', 'onboarding-progress.json') {
        $source = Join-Path $production $name
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $profileRoot $name)
            Write-Host "Seeded $name from the production profile." -ForegroundColor DarkGray
        }
    }
}

# Stage the layout from the build's recipe. `winapp run` cannot be used here: it stages the
# recipe's own manifest, which carries the Muesli.Windows identity, over the slot's.
$recipePath = Join-Path $builtLayout 'Muesli.Windows.WinUI.build.appxrecipe'
if (-not (Test-Path -LiteralPath $recipePath)) { throw "Layout recipe not found at $recipePath. Run without -SkipBuild first." }
New-Item -ItemType Directory -Path $slotLayout -Force | Out-Null
$expected = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
[void]$expected.Add('AppxManifest.xml')
$copied = 0
foreach ($node in (Select-Xml -LiteralPath $recipePath -XPath "//*[local-name()='PackagePath']")) {
    $relative = $node.Node.InnerText
    if ($relative -ieq 'AppxManifest.xml') { continue }
    $source = Get-Item -LiteralPath $node.Node.ParentNode.GetAttribute('Include')
    $destination = Join-Path $slotLayout $relative
    [void]$expected.Add($relative)
    $existing = Get-Item -LiteralPath $destination -ErrorAction SilentlyContinue
    if ($existing -and $existing.Length -eq $source.Length -and $existing.LastWriteTimeUtc -eq $source.LastWriteTimeUtc) { continue }
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $source.FullName -Destination $destination -Force
    $copied++
}
$removed = 0
Get-ChildItem -LiteralPath $slotLayout -File -Recurse | ForEach-Object {
    if (-not $expected.Contains($_.FullName.Substring($slotLayout.Length + 1))) {
        Remove-Item -LiteralPath $_.FullName -Force
        $removed++
    }
}
Copy-Item -LiteralPath $slotManifest -Destination (Join-Path $slotLayout 'AppxManifest.xml') -Force

Write-Host "Launching $packageName ($Configuration, x64)..." -ForegroundColor Cyan
Write-Host "Layout: $slotLayout ($copied copied, $removed removed)" -ForegroundColor DarkGray
Write-Host "Profile: $profileRoot" -ForegroundColor DarkGray
Add-AppxPackage -Register (Join-Path $slotLayout 'AppxManifest.xml') -ForceUpdateFromAnyVersion
$package = Get-AppxPackage -Name $packageName
if (-not $package) { throw "$packageName did not register." }
$aumid = "$($package.PackageFamilyName)!App"
# Packaged activation, as `winapp run` does; the executable must not be started directly.
Start-Process "shell:AppsFolder\$aumid"

Start-Sleep -Seconds $Wait
$process = Get-Process -Name 'Muesli.Windows.WinUI' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($slotRoot, [StringComparison]::OrdinalIgnoreCase) } |
    Sort-Object StartTime -Descending |
    Select-Object -First 1
if (-not $process) { throw "$packageName exited within $Wait second(s). Check $profileRoot\logs for startup details." }

[pscustomobject]@{
    ProcessId = $process.Id
    Package = $packageName
    ProfileRoot = $profileRoot
    AppUserModelId = $aumid
    WindowTitle = $process.MainWindowTitle
}
