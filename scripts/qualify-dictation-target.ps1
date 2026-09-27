param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Notepad", "Chrome", "Office", "Other")]
    [string]$TargetKind,
    [Parameter(Mandatory = $true)]
    [string]$TargetProcess,
    [Parameter(Mandatory = $true)]
    [string]$TraceId,
    [Parameter(Mandatory = $true)]
    [string]$RequiredModelId,
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 1000000)]
    [long]$MaxReleaseToPasteMs,
    [Parameter(Mandatory = $true)]
    [string]$ReviewedBy,
    [Parameter(Mandatory = $true)]
    [DateTimeOffset]$ReviewedAt,
    [string]$LogPath = "",
    [string]$OutputPath = "",
    [Parameter(Mandatory = $true)]
    [switch]$TextVerified
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($ReviewedBy)) { throw "ReviewedBy cannot be empty." }
$suiteLatencyTargetMs = 3000L
if ($MaxReleaseToPasteMs -gt $suiteLatencyTargetMs) {
    throw "MaxReleaseToPasteMs cannot exceed the four-app qualification target of $suiteLatencyTargetMs ms."
}
if ($ReviewedBy -match "(?i)^(placeholder|todo|tbd|unreviewed|automation|ci|n/?a|unknown|fake|example|dummy)$") {
    throw "ReviewedBy must be a real human reviewer identity, not a placeholder."
}
if ($TraceId -notmatch "^[0-9a-f]{12}$") {
    throw "TraceId must be the 12-character hex id from a Dictation latency trace log line. Placeholders are rejected."
}

$allowedTraceKeys = @(
    "timestamp", "trace", "status", "engine", "model", "audioDurationMs", "chars",
    "releaseToPasteMs", "releaseToUiSettledMs", "captureTotalMs", "captureStopDisposeMs",
    "captureFlushWaitMs", "capturePreparationMs", "capturePreparation", "transcriptionWallMs",
    "coordinatorTotalMs", "cleanupDictionaryMs", "fillerRemoval", "deliveryMode", "deliveryMs",
    "clipboardMs", "focusWaitMs", "inputMs", "targetForeground", "targetProcess",
    "targetProcessId", "historyPersisted", "persistenceUiMs"
)
$forbiddenTraceKeys = @(
    "title", "windowtitle", "targetwindowtitle", "window", "transcript", "text",
    "utterance", "reference", "expected", "observed"
)
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $logDirectory = Join-Path $env:APPDATA "muesli\logs"
    $LogPath = Get-ChildItem -LiteralPath $logDirectory -Filter "muesli-*.log" -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if ([string]::IsNullOrWhiteSpace($LogPath) -or -not (Test-Path -LiteralPath $LogPath)) {
    throw "Muesli log not found. Pass -LogPath after completing a fresh real dictation."
}

$traces = @(
    Get-Content -LiteralPath $LogPath |
        Where-Object { $_ -match "^\[(?<timestamp>[^\]]+)\].*Dictation latency trace\. (?<pairs>.+)$" } |
        ForEach-Object {
            $record = [ordered]@{ timestamp = $Matches.timestamp }
            foreach ($pair in ($Matches.pairs -split ";\s*")) {
                $parts = $pair.Split("=", 2)
                if ($parts.Count -ne 2) { continue }
                $name = $parts[0].Trim()
                $value = $parts[1].Trim()
                $normalized = $name.ToLowerInvariant()
                if ($forbiddenTraceKeys -contains $normalized) {
                    throw "Latency trace contained forbidden field '$name'. Window titles and transcript text must not be captured."
                }
                if ($allowedTraceKeys -contains $name) {
                    $record[$name] = $value
                }
            }
            [pscustomobject]$record
        }
)
$trace = $traces | Where-Object trace -eq $TraceId | Select-Object -Last 1
if ($null -eq $trace) { throw "No latency trace '$TraceId' was found. A specific fresh trace is required." }

$failures = [System.Collections.Generic.List[string]]::new()
$traceTimestamp = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse([string]$trace.timestamp, [ref]$traceTimestamp)) {
    $failures.Add("trace timestamp could not be parsed")
} elseif ($ReviewedAt -lt $traceTimestamp -or ($ReviewedAt - $traceTimestamp) -gt [TimeSpan]::FromHours(2)) {
    $failures.Add("human review must occur after and within two hours of the fresh dictation trace")
}
if ($ReviewedAt -gt [DateTimeOffset]::Now.AddMinutes(5)) { $failures.Add("reviewedAt cannot be in the future") }
if ($trace.status -ne "success") { $failures.Add("dictation status was '$($trace.status)' instead of success") }
if ($trace.deliveryMode -ne "active-app") { $failures.Add("delivery mode was '$($trace.deliveryMode)' instead of active-app") }
if ($trace.targetForeground -ne "True") { $failures.Add("the original target was not restored to the foreground") }
if ($trace.historyPersisted -ne "True") { $failures.Add("the delivered transcript was not durably persisted in history") }
if (-not ([string]$trace.engine).StartsWith("native-sherpa-onnx/", [StringComparison]::OrdinalIgnoreCase)) {
    $failures.Add("engine was '$($trace.engine)' instead of an explicit native sherpa-onnx family")
}
if ($trace.model -ne $RequiredModelId) { $failures.Add("model was '$($trace.model)' instead of '$RequiredModelId'") }
if (-not ([string]$trace.targetProcess).Equals($TargetProcess, [StringComparison]::OrdinalIgnoreCase)) {
    $failures.Add("target process '$($trace.targetProcess)' did not match '$TargetProcess'")
}

$kindMatches = switch ($TargetKind) {
    "Notepad" { $TargetProcess -match "^notepad$" }
    "Chrome" { $TargetProcess -match "^chrome$" }
    "Office" { $TargetProcess -match "^(winword|excel|powerpnt|outlook|onenote)$" }
    "Other" { $TargetProcess -notmatch "^(notepad|chrome|winword|excel|powerpnt|outlook|onenote)$" }
}
if (-not $kindMatches) { $failures.Add("target process '$TargetProcess' does not satisfy target kind '$TargetKind'") }

$releaseToPasteMs = 0L
if (-not [long]::TryParse([string]$trace.releaseToPasteMs, [ref]$releaseToPasteMs)) {
    $failures.Add("trace did not contain a valid release-to-paste measurement")
} elseif ($releaseToPasteMs -gt $MaxReleaseToPasteMs) {
    $failures.Add("release-to-paste $releaseToPasteMs ms exceeded $MaxReleaseToPasteMs ms")
}
$characterCount = 0L
if (-not [long]::TryParse([string]$trace.chars, [ref]$characterCount) -or $characterCount -le 0) {
    $failures.Add("trace did not contain a non-empty transcript")
}
if (-not $TextVerified) { $failures.Add("the complete pasted text was not human-verified") }

$report = [ordered]@{
    schemaVersion = 2
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    targetKind = $TargetKind
    targetProcess = $TargetProcess
    traceId = $TraceId
    requiredModelId = $RequiredModelId
    maxReleaseToPasteMs = $MaxReleaseToPasteMs
    releaseToPasteMs = $releaseToPasteMs
    reviewedBy = $ReviewedBy
    reviewedAt = $ReviewedAt.ToString("O")
    textVerified = [bool]$TextVerified
    prerequisites = [ordered]@{
        freshTraceFound = $true
        reviewWithinTwoHours = $traceTimestamp -ne [DateTimeOffset]::MinValue -and $ReviewedAt -ge $traceTimestamp -and ($ReviewedAt - $traceTimestamp) -le [TimeSpan]::FromHours(2)
        nativeParakeet = ([string]$trace.engine).StartsWith("native-sherpa-onnx/", [StringComparison]::OrdinalIgnoreCase) -and [string]$trace.model -eq $RequiredModelId
        foregroundActiveApp = [string]$trace.deliveryMode -eq "active-app" -and [string]$trace.targetForeground -eq "True"
        historyPersisted = [string]$trace.historyPersisted -eq "True"
        completeTextHumanVerified = [bool]$TextVerified -and $characterCount -gt 0
        latencyTargetMs = $suiteLatencyTargetMs
    }
    passed = $failures.Count -eq 0
    failures = @($failures)
    trace = $trace
}
$json = $report | ConvertTo-Json -Depth 8
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resolvedOutputPath) | Out-Null
    Set-Content -LiteralPath $resolvedOutputPath -Value $json -Encoding UTF8
    Write-Host "Dictation target qualification written: $resolvedOutputPath"
}
if ($failures.Count -gt 0) { throw "Dictation target qualification failed: $($failures -join '; ')" }
Write-Host "Dictation target qualification passed for $TargetKind / $TargetProcess."
$json
