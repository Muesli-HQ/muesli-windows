# One-command Windows v1 qualification bundle.
#
# Bundles the five qualification domains (Dictation, Audio, Meetings, Display/a11y, Clean machine)
# into a single machine-readable report. This script never fabricates a pass: a domain with no
# supplied report is recorded as "NotPerformed" with the exact external prerequisite it needs, and
# a supplied report only counts when it parses, records a pass, and (for human domains) names a
# non-placeholder reviewer. Missing hardware is never marked as passed.
#
#   pwsh -ExecutionPolicy Bypass -File .\scripts\qualify-windows-v1.ps1 `
#       -DictationSuiteReport artifacts\dictation\suite.json `
#       -MeetingLifecycleReport artifacts\meetings\lifecycle.json `
#       -Reviewer "Jane Doe" -ReviewedAt "2026-09-18T10:00:00Z" -BuildSha "<sha>"
#
# Optional automation: -RunAutomation invokes scripts/qualify-wave3-automatable.ps1.

param(
    [string]$OutputDirectory = "",
    [string]$DictationSuiteReport = "",
    [string]$AudioQualificationReport = "",
    [string]$MeetingLifecycleReport = "",
    [string]$DisplayQualificationReport = "",
    [string]$CleanMachineReport = "",
    [string]$Reviewer = "",
    [string]$ReviewedAt = "",
    [string]$BuildSha = "",
    [switch]$RunAutomation,
    [switch]$SkipAutomation
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "read-release-properties.ps1")
. (Join-Path $PSScriptRoot "release-common.ps1")
$release = Get-MuesliReleaseProperties -Root $root

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root "artifacts\qualification\v1"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

function Get-MachineEvidence {
    $cpu = @(); $gpu = @(); $memoryBytes = 0L
    try { $cpu = @(Get-CimInstance Win32_Processor -ErrorAction Stop | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors) } catch {}
    try { $gpu = @(Get-CimInstance Win32_VideoController -ErrorAction Stop | Select-Object Name,DriverVersion) } catch {}
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

$placeholderPattern = '(?i)^\s*(tbd|todo|placeholder|n/?a|none|unknown|test|changeme)\s*$'
function Test-PlaceholderReviewer {
    param([string]$Value)
    return [string]::IsNullOrWhiteSpace($Value) -or $Value -match $placeholderPattern
}

$domainFailures = [System.Collections.Generic.List[string]]::new()

function Read-DomainReport {
    param([string]$Path, [string]$Label, [string]$ExternalPrerequisite)
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return [ordered]@{
            status = "NotPerformed"
            evidenceKind = "human-or-hardware"
            prerequisite = $ExternalPrerequisite
            reportPath = ""
        }
    }
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        $domainFailures.Add("$Label report was supplied but does not exist: $resolved")
        return [ordered]@{ status = "Failed"; evidenceKind = "human-or-hardware"; prerequisite = $ExternalPrerequisite; reportPath = $resolved; failures = @("report not found") }
    }
    try {
        $body = Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json
    } catch {
        $domainFailures.Add("$Label report is not valid JSON: $resolved")
        return [ordered]@{ status = "Failed"; evidenceKind = "human-or-hardware"; prerequisite = $ExternalPrerequisite; reportPath = $resolved; failures = @("invalid JSON") }
    }
    $passed = ($body.PSObject.Properties.Name -contains "passed") -and [bool]$body.passed
    if (-not $passed) { $domainFailures.Add("$Label report did not record passed=true.") }
    return [ordered]@{
        status = if ($passed) { "Passed" } else { "Failed" }
        evidenceKind = "human-or-hardware"
        reportPath = $resolved
        prerequisite = $ExternalPrerequisite
    }
}

$dictation = Read-DomainReport -Path $DictationSuiteReport -Label "Dictation" `
    -ExternalPrerequisite "Run scripts/qualify-dictation-target-suite.ps1 over four real target reports (Notepad, Chrome, Office, other) with human text verification."
$audio = Read-DomainReport -Path $AudioQualificationReport -Label "Audio" `
    -ExternalPrerequisite "Physical audio qualification: default and explicit microphone, permission denial, unplug, default-device switch, Bluetooth, and recovery."
$meetings = Read-DomainReport -Path $MeetingLifecycleReport -Label "Meetings" `
    -ExternalPrerequisite "Run scripts/qualify-meeting-session-lifecycle.ps1 against real Zoom/Teams/Meet sessions with reviewed diarization audio and retained-audio retranscription."
$display = Read-DomainReport -Path $DisplayQualificationReport -Label "Display/a11y" `
    -ExternalPrerequisite "Physical display/a11y qualification at 100/125/150/200% DPI, multi-monitor work areas, keyboard-only, Narrator, and High Contrast."
$cleanMachine = Read-DomainReport -Path $CleanMachineReport -Label "Clean machine" `
    -ExternalPrerequisite "Clean-VM (Hyper-V/Windows Sandbox) install, first launch, mic permission, startup registration, notifications, upgrade, downgrade rejection, uninstall, data retention, and fresh logs."

$automation = [ordered]@{
    status = "Skipped"
    evidenceKind = "automation"
    reason = "Pass -RunAutomation to invoke scripts/qualify-wave3-automatable.ps1."
    reportPath = ""
}
if ($RunAutomation -and -not $SkipAutomation) {
    $automationDirectory = Join-Path $OutputDirectory "automation"
    try {
        & (Join-Path $PSScriptRoot "qualify-wave3-automatable.ps1") -OutputDirectory $automationDirectory
        $automationReport = Join-Path $automationDirectory "wave3-automatable.json"
        if (Test-Path -LiteralPath $automationReport) { $automation.reportPath = $automationReport }
        $automation.status = "Passed"
    } catch {
        $automation.status = "Failed"
        $automation.reason = "$($_.Exception.Message)"
        $domainFailures.Add("Automation qualification failed: $($_.Exception.Message)")
    }
}

$reviewerOk = -not (Test-PlaceholderReviewer -Value $Reviewer)
if (-not $reviewerOk -and ($DictationSuiteReport -or $AudioQualificationReport -or $MeetingLifecycleReport -or $DisplayQualificationReport -or $CleanMachineReport)) {
    $domainFailures.Add("A human-supplied domain report requires a non-placeholder -Reviewer and -ReviewedAt.")
}
if (-not [string]::IsNullOrWhiteSpace($Reviewer) -and -not $reviewerOk) {
    $domainFailures.Add("Reviewer '$Reviewer' looks like a placeholder; provide a real reviewer name.")
}

$humanDomains = @($dictation, $audio, $meetings, $display, $cleanMachine)
$allHumanPassed = @($humanDomains | Where-Object { $_.status -ne "Passed" }).Count -eq 0
$automationOk = $automation.status -in @("Passed", "Skipped")

$report = [ordered]@{
    schemaVersion = 1
    module = "v1-qualification-bundle"
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    releaseVersion = $release.Version
    releaseChannel = $release.Channel
    buildSha = $BuildSha
    reviewer = $Reviewer
    reviewedAt = $ReviewedAt
    reviewerAccepted = $reviewerOk
    machine = Get-MachineEvidence
    domains = [ordered]@{
        dictation = $dictation
        audio = $audio
        meetings = $meetings
        displayA11y = $display
        cleanMachine = $cleanMachine
        automation = $automation
    }
    passed = ($domainFailures.Count -eq 0) -and $allHumanPassed -and $reviewerOk -and $automationOk
    failures = @($domainFailures)
    nonClaims = @(
        "NotPerformed domains name an external hardware, human, or clean-VM prerequisite and are not passes.",
        "No physical audio, multi-monitor DPI, Narrator, clean-VM, or human-transcription evidence was fabricated.",
        "Automation status is separate from human/hardware status and cannot substitute for it."
    )
    outputDirectory = $OutputDirectory
}

$reportPath = Join-Path $OutputDirectory "windows-v1-qualification.json"
Write-Utf8NoBomFile -Path $reportPath -Content (($report | ConvertTo-Json -Depth 20) + "`n")
Write-Host "Windows v1 qualification bundle: $reportPath"
Write-Host "passed=$($report.passed) failures=$($report.failures.Count)"
if ($domainFailures.Count -gt 0) {
    throw "Windows v1 qualification bundle recorded failures. See $reportPath"
}
