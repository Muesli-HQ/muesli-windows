param(
    [Parameter(Mandatory = $true)]
    [string[]]$ReportPaths,
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
$reports = @($ReportPaths | ForEach-Object {
    $path = (Resolve-Path -LiteralPath $_).Path
    $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($report.schemaVersion -ne 2 -or -not $report.passed -or -not $report.textVerified) {
        throw "Target report is not a passed, human-verified schema-2 report: $path"
    }
    if ([string]::IsNullOrWhiteSpace([string]$report.traceId) -or
        [string]::IsNullOrWhiteSpace([string]$report.requiredModelId) -or
        [string]::IsNullOrWhiteSpace([string]$report.targetProcess)) {
        throw "Target report is missing its real trace, model, or target identity: $path"
    }
    $json = $report | ConvertTo-Json -Depth 10
    if ($json -match "(?i)(windowTitle|targetWindowTitle|transcript|utterance|expectedTranscript|observedTranscript)") {
        throw "Target report contains forbidden transcript or window-title fields: $path"
    }
    $report
})
$requiredKinds = @("Notepad", "Chrome", "Office", "Other")
$unexpectedReportCount = $reports.Count -ne $requiredKinds.Count
if ($unexpectedReportCount) { throw "Exactly one passed report for each of Notepad, Chrome, Office, and Other is required; received $($reports.Count)." }
$duplicateKinds = @($reports | Group-Object -Property targetKind | Where-Object Count -ne 1 | Select-Object -ExpandProperty Name)
if ($duplicateKinds.Count -gt 0) { throw "Duplicate target qualification reports are not allowed: $($duplicateKinds -join ', ')." }
$missing = @($requiredKinds | Where-Object { $_ -notin @($reports.targetKind) })
$models = @($reports.requiredModelId | Sort-Object -Unique)
$traceIds = @($reports.traceId | Sort-Object -Unique)
if ($missing.Count -gt 0) { throw "Missing target qualifications: $($missing -join ', ')" }
if ($models.Count -ne 1) { throw "Target reports do not use one explicit dictation model identity." }
if ($traceIds.Count -ne $reports.Count) { throw "Every target qualification must use a distinct real dictation trace." }

$requiredModelId = "parakeet-v3"
if ($models[0] -ne $requiredModelId) {
    throw "Target reports must use the native Parakeet model '$requiredModelId'; received '$($models[0])'."
}

$forbiddenTraceKeys = "windowTitle", "targetWindowTitle", "transcript", "utterance", "expectedTranscript", "observedTranscript"
$contractFailures = [System.Collections.Generic.List[string]]::new()
foreach ($report in $reports) {
    $trace = $report.trace
    if ($null -eq $trace) {
        $contractFailures.Add("$($report.targetKind): embedded latency trace is missing")
        continue
    }
    $traceJson = $trace | ConvertTo-Json -Depth 10
    foreach ($key in $forbiddenTraceKeys) {
        if ($traceJson -match [regex]::Escape($key)) {
            $contractFailures.Add("$($report.targetKind): trace contains forbidden field '$key'")
        }
    }
    if (-not ([string]$trace.engine).StartsWith("native-sherpa-onnx/", [StringComparison]::OrdinalIgnoreCase)) {
        $contractFailures.Add("$($report.targetKind): trace engine is not native sherpa-onnx")
    }
    if ([string]$trace.model -ne $requiredModelId -or [string]$report.requiredModelId -ne $requiredModelId) {
        $contractFailures.Add("$($report.targetKind): trace/model identity is not native Parakeet")
    }
    if ([string]$trace.deliveryMode -ne "active-app" -or [string]$trace.targetForeground -ne "True") {
        $contractFailures.Add("$($report.targetKind): foreground active-app delivery evidence is missing")
    }
    if ([string]$trace.historyPersisted -ne "True") {
        $contractFailures.Add("$($report.targetKind): history persistence evidence is missing")
    }
    $chars = 0L
    if (-not [long]::TryParse([string]$trace.chars, [ref]$chars) -or $chars -le 0) {
        $contractFailures.Add("$($report.targetKind): non-empty text evidence is missing")
    }
    $releaseToPasteMs = 0L
    if (-not [long]::TryParse([string]$report.releaseToPasteMs, [ref]$releaseToPasteMs) -or $releaseToPasteMs -lt 0) {
        $contractFailures.Add("$($report.targetKind): release-to-paste measurement is missing")
    } elseif ($releaseToPasteMs -gt 3000) {
        $contractFailures.Add("$($report.targetKind): release-to-paste $releaseToPasteMs ms exceeds 3000 ms")
    }
    if (@($report.failures).Count -gt 0) {
        $contractFailures.Add("$($report.targetKind): report contains failure details")
    }
}
if ($contractFailures.Count -gt 0) {
    throw "Dictation target suite contract failed: $($contractFailures -join '; ')"
}

function Get-Percentile {
    param([long[]]$Values, [double]$Percentile)
    $sorted = @($Values | Sort-Object)
    $rank = [math]::Ceiling($Percentile * $sorted.Count) - 1
    return $sorted[[math]::Max(0, [math]::Min($rank, $sorted.Count - 1))]
}

$latencies = @($reports | ForEach-Object { [long]$_.releaseToPasteMs })
$medianReleaseToPasteMs = Get-Percentile -Values $latencies -Percentile 0.50
$p95ReleaseToPasteMs = Get-Percentile -Values $latencies -Percentile 0.95
if ($medianReleaseToPasteMs -gt 3000 -or $p95ReleaseToPasteMs -gt 3000) {
    throw "Dictation target suite latency failed: median=$medianReleaseToPasteMs ms; p95=$p95ReleaseToPasteMs ms; target=3000 ms."
}

$summary = [ordered]@{
    schemaVersion = 2
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    modelId = $models[0]
    targetKinds = @($reports.targetKind)
    prerequisites = [ordered]@{
        freshRealTraces = $true
        nativeParakeet = $true
        foregroundActiveApp = $true
        historyPersisted = $true
        completeTextHumanVerified = $true
        zeroFailures = $true
        latencyTargetMs = 3000
    }
    latency = [ordered]@{
        count = $latencies.Count
        medianReleaseToPasteMs = $medianReleaseToPasteMs
        p95ReleaseToPasteMs = $p95ReleaseToPasteMs
        maximumReleaseToPasteMs = ($latencies | Measure-Object -Maximum).Maximum
    }
    passed = $true
    reports = $reports
}
$json = $summary | ConvertTo-Json -Depth 10
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resolvedOutput) | Out-Null
    Set-Content -LiteralPath $resolvedOutput -Value $json -Encoding UTF8
}
Write-Host "Dictation target suite passed for Notepad, Chrome, Office, and another editor."
Write-Host "This is not Phase 2 qualification unless the four reports came from fresh human-verified paste traces."
$json
