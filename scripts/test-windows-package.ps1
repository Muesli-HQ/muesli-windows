# MSIX package smoke test entry point.
#
# The shipping Windows artifact is the WinUI MSIX. This script finds the package and delegates to
# scripts/test-winui-msix.ps1, which validates the payload, the Appx manifest identity, the native
# runtime catalog coverage, and the forbidden CUDA/debug-artifact rules without installing the
# unsigned package. The retired portable-ZIP smoke test is archived under scripts/archive/wpf/.

param(
    [string]$MsixPath = "",
    [string]$ReportPath = "",
    [switch]$SkipLaunch
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "release-common.ps1")

if ([string]::IsNullOrWhiteSpace($MsixPath)) {
    $discovered = Get-MuesliLatestMsix -SearchRoot (Join-Path $root "artifacts\msix")
    if ($null -eq $discovered) {
        throw "No Muesli Windows WinUI MSIX was found under artifacts\msix. Build it with scripts\package-winui-msix.ps1 first."
    }
    $MsixPath = $discovered.FullName
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $root "artifacts\msix-smoke-report.json"
}

& (Join-Path $PSScriptRoot "test-winui-msix.ps1") -MsixPath $MsixPath -ReportPath $ReportPath -SkipLaunch:$SkipLaunch
