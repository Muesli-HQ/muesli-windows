param(
    [string]$Root = "",
    [switch]$AllowDirty,
    [string]$OverridePath = ""
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}
. (Join-Path $PSScriptRoot "release-common.ps1")
Assert-MuesliCleanReleaseInputs -Root $Root -AllowDirty:$AllowDirty -OverridePath $OverridePath | Out-Null
