<#
.SYNOPSIS
    Builds the shipping WinUI 3 MSIX package.

.DESCRIPTION
    Compatibility entry point retained for existing release automation. All arguments are
    forwarded to scripts/package-winui-msix.ps1. The retired WPF implementation is preserved
    at scripts/archive/wpf/package-windows-v1.ps1.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [string]$CertificateThumbprint
)

$ErrorActionPreference = 'Stop'
$arguments = @{ Configuration = $Configuration }
if ($OutputDirectory) { $arguments.OutputDirectory = $OutputDirectory }
if ($CertificateThumbprint) { $arguments.CertificateThumbprint = $CertificateThumbprint }
& (Join-Path $PSScriptRoot 'package-winui-msix.ps1') @arguments
exit $LASTEXITCODE
