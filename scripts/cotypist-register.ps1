<#
.SYNOPSIS
  Builds, installs and registers the Cotypist TSF text service (experimental, dev builds only).

.DESCRIPTION
  TSF only discovers text services registered machine-wide, and it loads them into every app the
  user types in, so the helper is installed outside the MSIX under Program Files and registered
  with regsvr32. Run from an elevated PowerShell. Afterwards add "Muesli Cotypist" under
  Settings > Time & language > Language & region > English (United States) > Keyboards, and build
  Muesli with -p:MuesliIncludeCotypist=true.

  -Unregister removes the registration and the installed file. A host app that still has the DLL
  loaded keeps it until that app closes; the file is then scheduled for deletion on reboot.
#>
[CmdletBinding()]
param([switch]$Unregister, [switch]$SkipBuild)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated PowerShell: TSF text services register machine-wide.'
}

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'windows-native\Muesli.Cotypist.TextService\Muesli.Cotypist.TextService.vcxproj'
$built = Join-Path $root 'windows-native\Muesli.Cotypist.TextService\bin\x64\Muesli.Cotypist.TextService.dll'
$installDir = Join-Path $env:ProgramFiles 'Muesli\Cotypist'
$installed = Join-Path $installDir 'Muesli.Cotypist.TextService.dll'
$regsvr32 = Join-Path $env:SystemRoot 'System32\regsvr32.exe'

if ($Unregister) {
    if (Test-Path $installed) {
        & $regsvr32 /s /u $installed
        if ($LASTEXITCODE -ne 0) { throw "regsvr32 /u failed ($LASTEXITCODE)." }
        try { Remove-Item $installed -Force }
        catch {
            Add-Type -Namespace Native -Name Kernel32 -MemberDefinition '[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool MoveFileEx(string from, string to, int flags);'
            [void][Native.Kernel32]::MoveFileEx($installed, $null, 4)  # MOVEFILE_DELAY_UNTIL_REBOOT
            Write-Host 'The DLL is still loaded by an open app; it will be removed on reboot.'
        }
    }
    Write-Host 'Muesli Cotypist unregistered.'
    return
}

if (-not $SkipBuild) {
    $msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $msbuild) { throw 'MSBuild with the C++ build tools (v143) was not found.' }
    & $msbuild $project -p:Configuration=Release -p:Platform=x64 -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Building the text service failed ($LASTEXITCODE)." }
}
if (-not (Test-Path $built)) { throw "Missing $built; build it first." }

New-Item -ItemType Directory -Force $installDir | Out-Null
# Re-registering over a DLL loaded in open apps: move it aside instead of failing to overwrite.
if (Test-Path $installed) {
    try { Copy-Item $built $installed -Force }
    catch {
        $aside = "$installed.$([DateTime]::UtcNow.Ticks).old"
        Move-Item $installed $aside -Force
        Copy-Item $built $installed -Force
    }
} else {
    Copy-Item $built $installed -Force
}
# Packaged and AppContainer apps (Store apps, the Start menu search box) can only load the DLL with
# ALL APPLICATION PACKAGES read access.
& icacls $installDir /grant '*S-1-15-2-1:(OI)(CI)RX' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls failed ($LASTEXITCODE)." }
& $regsvr32 /s $installed
if ($LASTEXITCODE -ne 0) { throw "regsvr32 failed ($LASTEXITCODE)." }
Write-Host "Muesli Cotypist registered from $installed."
Write-Host 'Enable it under Settings > Time & language > Language & region > English (United States) > Keyboards.'
