<#
.SYNOPSIS
    One-time import of JSON history from a pre-SQLite development build into a new library database.

.DESCRIPTION
    Muesli keeps dictations, meetings, folders, templates and the dictionary in
    <profile>\data\muesli.db. Development builds from before that kept them in
    windows-*.json files in the same folder. The app does not convert those files: it tells you
    they are there and points here.

    This script builds the command host and runs its --import-legacy-history command, which
      * only reads the JSON files and leaves them exactly as they were;
      * builds the database in a temporary file, verifies it against the source, then moves it
        into place, so a failed import leaves nothing behind;
      * refuses to run when the profile already has a muesli.db.

    Close Muesli (the build that uses this profile) before running it.

.PARAMETER ProfileRoot
    The profile folder, for example "$env:APPDATA\muesli" or "$env:APPDATA\MuesliDevB".
    There is no default: name the profile you mean.

.EXAMPLE
    .\scripts\import-legacy-history.ps1 -ProfileRoot "$env:APPDATA\muesli"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProfileRoot,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'windows-native\Muesli.Windows.CommandHost\Muesli.Windows.CommandHost.csproj'
$executable = Join-Path $repoRoot "windows-native\Muesli.Windows.CommandHost\bin\$Configuration\net10.0-windows\Muesli.Windows.CommandHost.exe"

$ProfileRoot = [IO.Path]::GetFullPath($ProfileRoot)
if (-not (Test-Path -LiteralPath (Join-Path $ProfileRoot 'data') -PathType Container)) {
    throw "No data folder found under $ProfileRoot. Pass the profile folder itself, for example $env:APPDATA\muesli."
}

if (-not $SkipBuild) {
    Write-Host 'Building the command host...' -ForegroundColor Cyan
    & dotnet build $project -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Command host build failed with exit code $LASTEXITCODE." }
}
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Command host not found at $executable." }

& $executable --import-legacy-history --profile-root $ProfileRoot
if ($LASTEXITCODE -ne 0) { throw "The import did not complete (exit code $LASTEXITCODE). Nothing was changed." }
