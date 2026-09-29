# WinUI MSIX release qualification.
#
# Validates the shipping MSIX payload (identity, native runtime coverage, no CUDA/debug artifacts),
# the publisher identity, and — when -RequireSignature is set — a valid timestamped Authenticode
# signature. It never installs the package and never claims a launch it did not perform.

param(
    [string]$MsixPath = "",
    [string]$OutputDirectory = "",
    [string]$NativeInventoryPath = "",
    [switch]$RequireSignature,
    [string]$ExpectedPublisher = ""
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "read-release-properties.ps1")
. (Join-Path $PSScriptRoot "release-common.ps1")
$release = Get-MuesliReleaseProperties -Root $root

if ([string]::IsNullOrWhiteSpace($MsixPath)) {
    $discovered = Get-MuesliLatestMsix -SearchRoot (Join-Path $root "artifacts\msix")
    if ($null -eq $discovered) { throw "No WinUI MSIX was found under artifacts\msix." }
    $MsixPath = $discovered.FullName
}
$resolvedMsix = (Resolve-Path -LiteralPath $MsixPath).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root "artifacts\qualification\msix"
}
$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
$smokeReportPath = Join-Path $resolvedOutput "msix-smoke-report.json"
if ([string]::IsNullOrWhiteSpace($NativeInventoryPath)) {
    $NativeInventoryPath = Join-Path $resolvedOutput "native-runtime-inventory.json"
}
$reportPath = Join-Path $resolvedOutput "release-qualification.json"

function Get-MachineEvidence {
    $cpu = @(); $gpu = @(); $memoryBytes = 0L
    try { $cpu = @(Get-CimInstance Win32_Processor -ErrorAction Stop | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors) } catch {}
    try { $gpu = @(Get-CimInstance Win32_VideoController -ErrorAction Stop | Select-Object Name,DriverVersion,AdapterRAM) } catch {}
    try { $memoryBytes = [long](Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).TotalPhysicalMemory } catch {}
    return [ordered]@{
        machineName = $env:COMPUTERNAME
        osVersion = [Environment]::OSVersion.VersionString
        processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
        powershellVersion = $PSVersionTable.PSVersion.ToString()
        totalPhysicalMemoryBytes = $memoryBytes
        cpu = $cpu
        gpu = $gpu
    }
}

$failures = [System.Collections.Generic.List[string]]::new()
$smokePassed = $true
$smokeError = ""
try {
    & (Join-Path $PSScriptRoot "test-winui-msix.ps1") -MsixPath $resolvedMsix -ReportPath $smokeReportPath -NativeInventoryPath $NativeInventoryPath -SkipLaunch | Out-Null
} catch {
    $smokePassed = $false
    $smokeError = $_.Exception.Message
    $failures.Add("MSIX smoke test failed: $smokeError")
}

$identity = Get-MuesliMsixManifestIdentity -MsixPath $resolvedMsix
$publisherIsPlaceholder = [string]$identity.Publisher -match '^CN=AppPublisher$'
$signature = $null
$signatureStatus = "Unavailable"
$timestamped = $false
$signatureCheckPassed = $true
if ($RequireSignature) {
    try {
        $signature = Assert-MuesliSignedMsix -MsixPath $resolvedMsix -ExpectedPublisher $ExpectedPublisher
        $signatureStatus = [string]$signature.status
        $timestamped = [bool]$signature.timestamped
    } catch {
        $signatureCheckPassed = $false
        $failures.Add("Signature gate failed: $($_.Exception.Message)")
    }
} else {
    $sig = Get-AuthenticodeSignature -LiteralPath $resolvedMsix
    $signatureStatus = if ($null -eq $sig) { "Unavailable" } else { [string]$sig.Status }
}

$report = [ordered]@{
    schemaVersion = 1
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    releaseVersion = $release.Version
    releaseChannel = $release.Channel
    minimumWindowsVersion = $release.MinimumWindowsVersion
    supportedEnvironments = $release.SupportedEnvironments
    passed = $failures.Count -eq 0 -and $smokePassed
    failures = $failures
    machine = Get-MachineEvidence
    package = [ordered]@{
        path = $resolvedMsix
        bytes = (Get-Item -LiteralPath $resolvedMsix).Length
        sha256 = Get-Sha256HexFromFile -Path $resolvedMsix
        smokePassed = $smokePassed
        smokeError = $smokeError
        smokeReportPath = $smokeReportPath
        nativeInventoryPath = $NativeInventoryPath
        publisher = [string]$identity.Publisher
        publisherIsPlaceholder = $publisherIsPlaceholder
        signatureStatus = $signatureStatus
        timestamped = $timestamped
    }
    gates = [ordered]@{
        signatureRequired = [bool]$RequireSignature
        signatureCheckPassed = $signatureCheckPassed
        publisherExpected = $ExpectedPublisher
    }
    legalProductGates = @(Get-MuesliReleaseLegalProductGates -Root $root)
    nonClaims = @(
        "The unsigned development MSIX was not installed by this script.",
        "No human, hardware, clean-VM, or production-signing evidence is fabricated."
    )
}
$report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Host "MSIX release qualification report: $reportPath"
if ($failures.Count -gt 0) {
    throw "MSIX release qualification failed: $($failures -join '; ')"
}
Write-Host "MSIX release qualification passed."
Get-Content -LiteralPath $reportPath -Raw
