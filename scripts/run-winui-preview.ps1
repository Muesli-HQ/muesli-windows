<#
.SYNOPSIS
    Launches the shipping WinUI app with an isolated temporary profile.

.DESCRIPTION
    Compatibility wrapper for UI experiments. The normal production-profile launcher is
    scripts/run-windows.ps1. This wrapper still uses the supported packaged WinApp path.
#>
[CmdletBinding()]
param(
    [string]$ProfileRoot,
    [switch]$SkipBuild,
    [int]$Wait = 10,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $ProfileRoot) {
    $ProfileRoot = Join-Path $env:TEMP ("muesli-winui-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}

& (Join-Path $PSScriptRoot 'run-windows.ps1') `
    -ProfileRoot $ProfileRoot `
    -SkipBuild:$SkipBuild `
    -Wait $Wait `
    -Configuration $Configuration

exit $LASTEXITCODE
