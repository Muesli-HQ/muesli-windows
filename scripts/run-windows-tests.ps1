# Restores and runs the Windows unit tests, writing windows-tests.trx.
# Used by scripts/rehearse-windows-release.ps1 and by the Windows CI test job, so both run the
# suite the same way.
#   pwsh -ExecutionPolicy Bypass -File .\scripts\run-windows-tests.ps1
param(
    [string]$Configuration = "Release",
    [string]$ResultsDirectory
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $root "artifacts\test-results" }
New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null

$project = Join-Path $root "windows-native\Muesli.Windows.Tests\Muesli.Windows.Tests.csproj"
Write-Host "Restoring and running $Configuration tests..."
dotnet restore $project --force-evaluate
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }

$testArguments = @(
    "test", $project,
    "-c", $Configuration,
    "--no-restore",
    "--logger", "trx;LogFileName=windows-tests.trx",
    "--results-directory", $ResultsDirectory
)
# Without an approved shared-core ABI the managed text processor ships and the Swift bridge is
# not built (windows-native/shared-core.lock.json, bridgeRequired=false).
if ($env:MUESLI_SHARED_CORE_MODE -eq 'managed-fallback') {
    $testArguments += "-p:MuesliRequireSwiftBridge=false"
}
dotnet @testArguments
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }
