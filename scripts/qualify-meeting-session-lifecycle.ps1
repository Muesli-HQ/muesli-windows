param(
    [Parameter(Mandatory = $true)]
    [string[]]$ReportPaths,
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
$requiredScenarios = @(
    "baseline-zoom",
    "baseline-teams",
    "baseline-google-meet",
    "endpoint-fallback",
    "microphone-route-change",
    "system-route-change",
    "bluetooth-transition",
    "device-removal-degraded",
    "silence-clipping",
    "suspend-resume",
    "conservative-auto-stop",
    "forced-interruption-recovery",
    "cancellation-failure",
    "playback-ownership"
)
$allowedProviders = @("cpu", "cuda")
$allowedCaptureModes = @("process-tree", "endpoint-loopback", "mixed", "not-applicable")
$allowedFinalStates = @("Completed", "Failed", "Cancelled", "RecoverableInterruption", "Multiple")
$reports = [System.Collections.Generic.List[object]]::new()

foreach ($candidate in $ReportPaths) {
    $path = (Resolve-Path -LiteralPath $candidate).Path
    $raw = Get-Content -LiteralPath $path -Raw
    $report = $raw | ConvertFrom-Json
    if ($report.schemaVersion -ne 1) { throw "Meeting report must use schemaVersion 1: $path" }
    if (-not $report.passed -or -not $report.humanReviewed) { throw "Meeting report is not human-reviewed and passed: $path" }
    if ([string]::IsNullOrWhiteSpace([string]$report.reviewer)) { throw "Meeting report has no reviewer: $path" }
    if ([string]::IsNullOrWhiteSpace([string]$report.evidenceNotes)) { throw "Meeting report has no evidence notes: $path" }
    if ([string]::IsNullOrWhiteSpace([string]$report.buildSha256) -or [string]$report.buildSha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw "Meeting report has no valid executable SHA-256: $path"
    }
    if ([string]::IsNullOrWhiteSpace([string]$report.modelId)) { throw "Meeting report has no final model identity: $path" }
    if ($report.provider -notin $allowedProviders) { throw "Meeting report has an unsupported provider identity: $path" }
    if ($report.captureMode -notin $allowedCaptureModes) { throw "Meeting report has an invalid capture mode: $path" }
    if ($report.finalState -notin $allowedFinalStates) { throw "Meeting report has an invalid final state: $path" }
    if ([int]$report.freshLogForbiddenCount -ne 0) { throw "Meeting report has forbidden fresh-log entries: $path" }
    if (-not $report.privacyLeakCheckPassed) { throw "Meeting report did not pass the privacy-log review: $path" }
    if ([string]$report.evidenceNotes -match '(?i)https?://|[A-Z]:\\|meeting[ -]?code') {
        throw "Meeting evidence notes contain a URL, local path, or meeting-code marker: $path"
    }
    if ($raw -match '(?i)"(transcript|windowTitle|meetingTitle|meetingUrl|meetingCode|audioPath)"\s*:') {
        throw "Meeting report contains a prohibited content/path field: $path"
    }
    try {
        $started = [DateTimeOffset]::Parse([string]$report.startedAtUtc)
        $ended = [DateTimeOffset]::Parse([string]$report.endedAtUtc)
    } catch {
        throw "Meeting report timestamps are invalid: $path"
    }
    if ($ended -lt $started) { throw "Meeting report ends before it starts: $path" }
    $reports.Add($report)
}

$scenarioNames = @($reports.scenario)
$missing = @($requiredScenarios | Where-Object { $_ -notin $scenarioNames })
$duplicates = @($scenarioNames | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
$builds = @($reports.buildSha256 | Sort-Object -Unique)
$models = @($reports.modelId | Sort-Object -Unique)
if ($missing.Count -gt 0) { throw "Missing Phase 3 meeting scenarios: $($missing -join ', ')" }
if ($duplicates.Count -gt 0) { throw "Duplicate Phase 3 meeting scenarios: $($duplicates -join ', ')" }
if ($builds.Count -ne 1) { throw "All Phase 3 meeting reports must exercise the same executable build." }
if ($models.Count -ne 1) { throw "All Phase 3 meeting reports must use one explicit final-meeting model." }

$baselineModes = @($reports | Where-Object scenario -like 'baseline-*' | ForEach-Object captureMode)
if ('process-tree' -notin $baselineModes -and 'mixed' -notin $baselineModes) {
    throw "At least one Zoom/Teams/Google Meet baseline must exercise process-tree capture."
}
$fallbackReport = @($reports | Where-Object scenario -eq 'endpoint-fallback')
if ($fallbackReport.Count -ne 1 -or $fallbackReport[0].captureMode -notin @("endpoint-loopback", "mixed")) {
    throw "The endpoint-fallback scenario must identify endpoint-loopback or mixed capture."
}

$summary = [ordered]@{
    schemaVersion = 1
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    buildSha256 = $builds[0]
    modelId = $models[0]
    scenarios = $requiredScenarios
    passed = $true
    reports = $reports
}
$json = $summary | ConvertTo-Json -Depth 12
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resolvedOutput) | Out-Null
    Set-Content -LiteralPath $resolvedOutput -Value $json -Encoding UTF8
}
Write-Host "Phase 3 meeting-session qualification passed for all required physical scenarios."
$json
