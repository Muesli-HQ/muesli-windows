param(
    [string]$LogPath = "",
    [ValidateRange(1, 1000)]
    [int]$Last = 100,
    [string]$OutputPath = "",
    [ValidateRange(0, 1000)]
    [int]$MinimumSuccessfulTraces = 0,
    [ValidateRange(0, 1000000)]
    [long]$MaxMedianReleaseToPasteMs = 0,
    [ValidateRange(0, 1000000)]
    [long]$MaxP95ReleaseToPasteMs = 0,
    [ValidateRange(0, 1000000)]
    [long]$MaxP95TranscriptionMs = 0,
    [ValidateRange(-1, 1)]
    [double]$MaxPasteFailureRate = -1,
    [string]$RequiredEngine = "",
    [switch]$RequireActiveAppDelivery,
    [switch]$RequireForegroundConfirmation,
    [string]$BaselinePath = "",
    [ValidateRange(-1, 10000)]
    [double]$MaxP95RegressionPercent = -1
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $logDirectory = Join-Path $env:APPDATA "muesli\logs"
    $LogPath = Get-ChildItem -LiteralPath $logDirectory -Filter "muesli-*.log" -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

if ([string]::IsNullOrWhiteSpace($LogPath) -or -not (Test-Path -LiteralPath $LogPath)) {
    throw "Muesli log not found. Pass -LogPath or run at least one dictation."
}

$numericFields = @(
    "audioDurationMs",
    "chars",
    "releaseToPasteMs",
    "releaseToUiSettledMs",
    "captureTotalMs",
    "captureStopDisposeMs",
    "captureFlushWaitMs",
    "capturePreparationMs",
    "transcriptionWallMs",
    "coordinatorTotalMs",
    "cleanupDictionaryMs",
    "deliveryMs",
    "clipboardMs",
    "focusWaitMs",
    "inputMs",
    "persistenceUiMs",
    "targetProcessId"
)

$traces = @(
    Get-Content -LiteralPath $LogPath |
        Where-Object { $_ -match "^\[(?<timestamp>[^\]]+)\].*Dictation latency trace\. (?<pairs>.+)$" } |
        ForEach-Object {
            $timestamp = $Matches.timestamp
            $pairs = $Matches.pairs
            $record = [ordered]@{ timestamp = $timestamp }
            foreach ($pair in ($pairs -split ";\s*")) {
                $parts = $pair.Split("=", 2)
                if ($parts.Count -ne 2) {
                    continue
                }

                $name = $parts[0].Trim()
                $value = $parts[1].Trim()
                if ($numericFields -contains $name) {
                    $number = 0L
                    if ([long]::TryParse(
                        $value,
                        [Globalization.NumberStyles]::Integer,
                        [Globalization.CultureInfo]::InvariantCulture,
                        [ref]$number)) {
                        $record[$name] = $number
                    }
                } else {
                    $record[$name] = $value
                }
            }

            [pscustomobject]$record
        } |
        Select-Object -Last $Last
)

if ($traces.Count -eq 0) {
    throw "No 'Dictation latency trace' entries were found in $LogPath."
}

$successfulTraces = @($traces | Where-Object status -eq "success")
$deliveryAttempts = @($traces | Where-Object { $_.status -in @("success", "paste-failed") })
$pasteFailures = @($traces | Where-Object status -eq "paste-failed")
$activeAppSuccesses = @($successfulTraces | Where-Object deliveryMode -eq "active-app")
$foregroundSuccesses = @($activeAppSuccesses | Where-Object { $_.targetForeground -eq "True" })

function Get-Percentile {
    param(
        [long[]]$Values,
        [double]$Percentile
    )

    if ($Values.Count -eq 0) {
        return 0
    }

    $sorted = @($Values | Sort-Object)
    $rank = [math]::Ceiling($Percentile * $sorted.Count) - 1
    $boundedRank = [math]::Max(0, [math]::Min($rank, $sorted.Count - 1))
    return $sorted[$boundedRank]
}

function Get-MetricSummary {
    param(
        [string]$Name,
        [object[]]$Records
    )

    $values = @(
        $Records |
            ForEach-Object { $_.$Name } |
            Where-Object { $null -ne $_ } |
            ForEach-Object { [long]$_ }
    )

    if ($values.Count -eq 0) {
        return $null
    }

    return [ordered]@{
        count = $values.Count
        averageMs = [math]::Round(($values | Measure-Object -Average).Average, 1)
        medianMs = Get-Percentile -Values $values -Percentile 0.50
        p95Ms = Get-Percentile -Values $values -Percentile 0.95
        maximumMs = ($values | Measure-Object -Maximum).Maximum
    }
}

function Get-LongOrZero {
    param($Value)
    if ($null -eq $Value) {
        return 0L
    }

    return [long]$Value
}

$metricSummary = [ordered]@{}
foreach ($field in $numericFields | Where-Object { $_ -like "*Ms" }) {
    $summary = Get-MetricSummary -Name $field -Records $successfulTraces
    if ($null -ne $summary) {
        $metricSummary[$field] = $summary
    }
}

foreach ($trace in $successfulTraces) {
    $components = @(
        [pscustomobject]@{ name = "capture"; valueMs = (Get-LongOrZero $trace.captureTotalMs) }
        [pscustomobject]@{ name = "transcription"; valueMs = (Get-LongOrZero $trace.transcriptionWallMs) }
        [pscustomobject]@{ name = "dictionary-cleanup"; valueMs = (Get-LongOrZero $trace.cleanupDictionaryMs) }
        [pscustomobject]@{ name = "delivery"; valueMs = (Get-LongOrZero $trace.deliveryMs) }
        [pscustomobject]@{ name = "persistence-ui"; valueMs = (Get-LongOrZero $trace.persistenceUiMs) }
    )
    $bottleneck = $components | Sort-Object valueMs -Descending | Select-Object -First 1
    $trace | Add-Member -NotePropertyName bottleneck -NotePropertyValue $bottleneck.name -Force
    $trace | Add-Member -NotePropertyName bottleneckMs -NotePropertyValue $bottleneck.valueMs -Force
}

$pasteFailureRate = if ($deliveryAttempts.Count -eq 0) {
    0.0
} else {
    $pasteFailures.Count / [double]$deliveryAttempts.Count
}
$foregroundRate = if ($activeAppSuccesses.Count -eq 0) {
    0.0
} else {
    $foregroundSuccesses.Count / [double]$activeAppSuccesses.Count
}

$baselineComparison = $null
if (-not [string]::IsNullOrWhiteSpace($BaselinePath)) {
    $resolvedBaseline = (Resolve-Path -LiteralPath $BaselinePath).Path
    $baseline = Get-Content -LiteralPath $resolvedBaseline -Raw | ConvertFrom-Json
    $baselineP95 = [double]$baseline.metrics.releaseToPasteMs.p95Ms
    $currentP95 = [double](Get-LongOrZero $metricSummary.releaseToPasteMs.p95Ms)
    $regressionPercent = if ($baselineP95 -le 0) { 0.0 } else { (($currentP95 - $baselineP95) / $baselineP95) * 100.0 }
    $baselineComparison = [ordered]@{
        path = $resolvedBaseline
        baselineP95Ms = $baselineP95
        currentP95Ms = $currentP95
        regressionPercent = [math]::Round($regressionPercent, 1)
    }
}

$gateFailures = [System.Collections.Generic.List[string]]::new()
if ($MinimumSuccessfulTraces -gt 0 -and $successfulTraces.Count -lt $MinimumSuccessfulTraces) {
    $gateFailures.Add("successful traces $($successfulTraces.Count) are below required $MinimumSuccessfulTraces")
}
if ($MaxMedianReleaseToPasteMs -gt 0 -and
    [long](Get-LongOrZero $metricSummary.releaseToPasteMs.medianMs) -gt $MaxMedianReleaseToPasteMs) {
    $gateFailures.Add("release-to-paste median $($metricSummary.releaseToPasteMs.medianMs) ms exceeds $MaxMedianReleaseToPasteMs ms")
}
if ($MaxP95ReleaseToPasteMs -gt 0 -and
    [long](Get-LongOrZero $metricSummary.releaseToPasteMs.p95Ms) -gt $MaxP95ReleaseToPasteMs) {
    $gateFailures.Add("release-to-paste p95 $($metricSummary.releaseToPasteMs.p95Ms) ms exceeds $MaxP95ReleaseToPasteMs ms")
}
if ($MaxP95TranscriptionMs -gt 0 -and
    [long](Get-LongOrZero $metricSummary.transcriptionWallMs.p95Ms) -gt $MaxP95TranscriptionMs) {
    $gateFailures.Add("transcription p95 $($metricSummary.transcriptionWallMs.p95Ms) ms exceeds $MaxP95TranscriptionMs ms")
}
if ($MaxPasteFailureRate -ge 0 -and $pasteFailureRate -gt $MaxPasteFailureRate) {
    $gateFailures.Add("paste failure rate $([math]::Round($pasteFailureRate, 4)) exceeds $MaxPasteFailureRate")
}
if (-not [string]::IsNullOrWhiteSpace($RequiredEngine)) {
    $unexpectedEngines = @($successfulTraces | Where-Object engine -ne $RequiredEngine)
    if ($unexpectedEngines.Count -gt 0) {
        $gateFailures.Add("$($unexpectedEngines.Count) successful traces did not use engine '$RequiredEngine'")
    }
}
if ($RequireActiveAppDelivery -and
    @($successfulTraces | Where-Object deliveryMode -ne "active-app").Count -gt 0) {
    $gateFailures.Add("one or more successful traces did not use active-app delivery")
}
if ($RequireForegroundConfirmation -and $foregroundSuccesses.Count -ne $activeAppSuccesses.Count) {
    $gateFailures.Add("foreground confirmation failed for one or more active-app deliveries")
}
if ($MaxP95RegressionPercent -ge 0) {
    if ($null -eq $baselineComparison) {
        $gateFailures.Add("a p95 regression threshold requires -BaselinePath")
    } elseif ($baselineComparison.regressionPercent -gt $MaxP95RegressionPercent) {
        $gateFailures.Add("release-to-paste p95 regression $($baselineComparison.regressionPercent)% exceeds $MaxP95RegressionPercent%")
    }
}

$report = [ordered]@{
    schemaVersion = 2
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    logPath = [System.IO.Path]::GetFullPath($LogPath)
    traceCount = $traces.Count
    successfulTraceCount = $successfulTraces.Count
    deliveryAttemptCount = $deliveryAttempts.Count
    pasteFailureCount = $pasteFailures.Count
    pasteFailureRate = [math]::Round($pasteFailureRate, 4)
    activeAppSuccessCount = $activeAppSuccesses.Count
    targetForegroundRate = [math]::Round($foregroundRate, 4)
    engines = @($traces | ForEach-Object engine | Where-Object { $_ } | Sort-Object -Unique)
    models = @($traces | ForEach-Object model | Where-Object { $_ } | Sort-Object -Unique)
    targetProcesses = @($traces | ForEach-Object targetProcess | Where-Object { $_ } | Sort-Object -Unique)
    statusCounts = [ordered]@{
        success = $successfulTraces.Count
        pasteFailed = $pasteFailures.Count
        noSpeech = @($traces | Where-Object status -eq "no-speech").Count
    }
    metrics = $metricSummary
    bottleneckCounts = @($successfulTraces | Group-Object bottleneck | Sort-Object Count -Descending | ForEach-Object {
        [ordered]@{ name = $_.Name; count = $_.Count }
    })
    slowestSuccessfulTraces = @($successfulTraces | Sort-Object releaseToPasteMs -Descending | Select-Object -First 5)
    baselineComparison = $baselineComparison
    gates = [ordered]@{
        passed = $gateFailures.Count -eq 0
        failures = @($gateFailures)
    }
    traces = $traces
}

$json = $report | ConvertTo-Json -Depth 10
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resolvedOutputPath) | Out-Null
    Set-Content -LiteralPath $resolvedOutputPath -Value $json -Encoding UTF8
    Write-Host "Dictation latency report written: $resolvedOutputPath"
}

if ($gateFailures.Count -gt 0) {
    throw "Dictation latency qualification failed: $($gateFailures -join '; ')"
}

if ($MinimumSuccessfulTraces -gt 0 -or
    $MaxMedianReleaseToPasteMs -gt 0 -or
    $MaxP95ReleaseToPasteMs -gt 0 -or
    $MaxP95TranscriptionMs -gt 0 -or
    $MaxPasteFailureRate -ge 0 -or
    -not [string]::IsNullOrWhiteSpace($RequiredEngine) -or
    $RequireActiveAppDelivery -or
    $RequireForegroundConfirmation -or
    $MaxP95RegressionPercent -ge 0) {
    Write-Host "Dictation latency qualification gates passed."
}

$json
