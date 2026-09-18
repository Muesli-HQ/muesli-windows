# Builds the shipping WinUI MSIX installer.
#
# The MSIX is the only shipping installer for Muesli Windows. The retired WPF-era portable
# publish + Inno Setup pipeline is archived under scripts/archive/wpf/ and is not used here.
# Production signing and timestamping are performed by scripts/sign-windows-release.ps1, which
# must be run against the produced MSIX before public release.

param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [string]$CertificateThumbprint
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

$arguments = @{ Configuration = $Configuration }
if ($OutputDirectory) { $arguments.OutputDirectory = $OutputDirectory }
if ($CertificateThumbprint) { $arguments.CertificateThumbprint = $CertificateThumbprint }
& (Join-Path $PSScriptRoot 'package-winui-msix.ps1') @arguments

Write-Host ''
Write-Host 'The MSIX installer was produced. Sign and timestamp it for public release with:'
Write-Host '  .\scripts\sign-windows-release.ps1 -MsixPath <path-to.msix>'
Write-Host 'A signed MSIX is required before install/upgrade/uninstall qualification.'
