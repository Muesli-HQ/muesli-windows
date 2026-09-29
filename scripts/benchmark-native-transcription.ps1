param(
    [Parameter(Mandatory = $true)]
    [string]$AudioPath,
    [string]$ReferencePath = "",
    [string]$ModelId = "parakeet-v3",
    [ValidateRange(2, 20)]
    [int]$Runs = 3,
    [string]$OutputPath = "",
    [string]$ExecutablePath = "",
    [ValidateRange(0, 10)]
    [double]$MaxRealtimeFactor = 0,
    [ValidateRange(0, 3600000)]
    [int]$MaxWarmWallMs = 0,
    [ValidateRange(-1, 1)]
    [double]$MaxWordErrorRate = -1,
    [ValidateRange(-1, 1)]
    [double]$MaxCharacterErrorRate = -1,
    [string]$ExpectedTranscriptSha256 = "",
    [string]$ExpectedSegmentSha256 = "",
    [string]$ExpectedProvider = "",
    [switch]$RequireDeterministic,
    [switch]$RequireModelReuse,
    [switch]$RequireReference
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$resolvedAudio = (Resolve-Path -LiteralPath $AudioPath).Path

$qualityGateRequested = $MaxWordErrorRate -ge 0 -or $MaxCharacterErrorRate -ge 0 -or $RequireReference
if ($qualityGateRequested -and [string]::IsNullOrWhiteSpace($ReferencePath)) {
    throw "A human-reviewed -ReferencePath is required when WER/CER or -RequireReference is requested."
}
if (-not [string]::IsNullOrWhiteSpace($ReferencePath)) {
    $resolvedReference = (Resolve-Path -LiteralPath $ReferencePath).Path
    $referenceText = [System.IO.File]::ReadAllText($resolvedReference)
    if ($qualityGateRequested -and [string]::IsNullOrWhiteSpace($referenceText)) {
        throw "The reference transcript is empty: $resolvedReference"
    }
}

if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $root "windows-native\Muesli.Windows.CommandHost\bin\Debug\net10.0-windows\Muesli.Windows.CommandHost.exe"
}

if (-not (Test-Path -LiteralPath $ExecutablePath)) {
    throw "Muesli command host not found: $ExecutablePath. Build Muesli.Windows.CommandHost first or pass -ExecutablePath."
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $benchmarkDirectory = Join-Path $root "artifacts\benchmarks"
    New-Item -ItemType Directory -Force -Path $benchmarkDirectory | Out-Null
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputPath = Join-Path $benchmarkDirectory "native-transcription-$timestamp.json"
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = [System.IO.Path]::GetFullPath($ExecutablePath)
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.WorkingDirectory = Split-Path -Parent $startInfo.FileName
$nativeParakeetCache = if (-not [string]::IsNullOrWhiteSpace($env:MUESLI_NATIVE_PARAKEET_CACHE)) {
    $env:MUESLI_NATIVE_PARAKEET_CACHE
} else {
    Join-Path $env:USERPROFILE ".cache\muesli\native-parakeet"
}
$startInfo.EnvironmentVariables["MUESLI_NATIVE_PARAKEET_CACHE"] = $nativeParakeetCache
$arguments = @(
    "--benchmark-native",
    "--audio",
    $resolvedAudio,
    "--output",
    $resolvedOutput,
    "--runs",
    $Runs.ToString([System.Globalization.CultureInfo]::InvariantCulture),
    "--model",
    $ModelId
)

if (-not [string]::IsNullOrWhiteSpace($ReferencePath)) {
    $arguments += "--reference"
    $arguments += $resolvedReference
}

$startInfo.Arguments = ($arguments | ForEach-Object {
    '"' + ([string]$_).Replace('"', '\"') + '"'
}) -join " "

$process = [System.Diagnostics.Process]::Start($startInfo)
$process.WaitForExit()
if ($process.ExitCode -ne 0) {
    if (Test-Path -LiteralPath $resolvedOutput) {
        Get-Content -LiteralPath $resolvedOutput -Raw | Write-Error
    }

    throw "Native transcription benchmark failed with exit code $($process.ExitCode)."
}

if (-not (Test-Path -LiteralPath $resolvedOutput)) {
    throw "Benchmark completed without creating the expected report: $resolvedOutput"
}

Write-Host "Native transcription benchmark complete: $resolvedOutput"
$reportJson = Get-Content -LiteralPath $resolvedOutput -Raw
$report = $reportJson | ConvertFrom-Json
$result = @($report.Report.Results | Where-Object Success | Select-Object -First 1)
if ($result.Count -eq 0) {
    throw "Benchmark report contains no successful result for $ModelId`: $resolvedOutput"
}

$result = $result[0]
$failures = [System.Collections.Generic.List[string]]::new()
$requiredRunCount = $Runs
if ($result.RunCount -lt $requiredRunCount) {
    $failures.Add("benchmark recorded $($result.RunCount) run(s), but $requiredRunCount were required")
}
if ($result.ModelName -ne $ModelId) {
    throw "Benchmark routed to '$($result.ModelName)' instead of requested model '$ModelId'."
}
if (-not $result.Success) {
    $failures.Add("benchmark result was not successful: $($result.ErrorMessage)")
}
if ([int]$result.AudioDurationMs -le 0) {
    $failures.Add("decoded audio duration was not positive")
}
if ([double]::IsNaN([double]$result.RealtimeFactor) -or
    [double]::IsInfinity([double]$result.RealtimeFactor) -or
    [double]$result.RealtimeFactor -lt 0) {
    $failures.Add("RTF was not a finite non-negative number")
}
if ($MaxRealtimeFactor -gt 0 -and $result.RealtimeFactor -gt $MaxRealtimeFactor) {
    $failures.Add("RTF $($result.RealtimeFactor) exceeds $MaxRealtimeFactor")
}
if ($MaxWarmWallMs -gt 0 -and $result.WarmWallMs -gt $MaxWarmWallMs) {
    $failures.Add("warm wall $($result.WarmWallMs) ms exceeds $MaxWarmWallMs ms")
}
if ($MaxWordErrorRate -ge 0) {
    if ($null -eq $result.WordErrorRate) {
        $failures.Add("WER threshold was requested but no reference transcript was supplied")
    } elseif ($result.WordErrorRate -gt $MaxWordErrorRate) {
        $failures.Add("WER $($result.WordErrorRate) exceeds $MaxWordErrorRate")
    }
}
if ($MaxCharacterErrorRate -ge 0) {
    if ($null -eq $result.CharacterErrorRate) {
        $failures.Add("CER threshold was requested but no reference transcript was supplied")
    } elseif ($result.CharacterErrorRate -gt $MaxCharacterErrorRate) {
        $failures.Add("CER $($result.CharacterErrorRate) exceeds $MaxCharacterErrorRate")
    }
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedTranscriptSha256) -and
    $result.TranscriptSha256 -ne $ExpectedTranscriptSha256.ToLowerInvariant()) {
    $failures.Add("transcript SHA-256 $($result.TranscriptSha256) does not match $ExpectedTranscriptSha256")
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedSegmentSha256) -and
    $result.SegmentLayoutSha256 -ne $ExpectedSegmentSha256.ToLowerInvariant()) {
    $failures.Add("segment SHA-256 $($result.SegmentLayoutSha256) does not match $ExpectedSegmentSha256")
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedProvider) -and
    ($result.WarmBackend -ne $ExpectedProvider.ToLowerInvariant() -or
     $result.FirstRunBackend -ne $ExpectedProvider.ToLowerInvariant())) {
    $failures.Add("provider first=$($result.FirstRunBackend), warm=$($result.WarmBackend) does not match $ExpectedProvider")
}
if ($RequireDeterministic -and
    (-not $result.DeterministicOutput -or -not $result.DeterministicSegments -or
     [int]$result.DistinctTranscriptCount -ne 1 -or
     [int]$result.DistinctSegmentLayoutCount -ne 1)) {
    $failures.Add("transcript or segment output was not deterministic across $($result.RunCount) runs")
}
if ($RequireModelReuse -and ([int]$result.RunCount -lt 2 -or -not $result.ModelInstanceReused)) {
    $failures.Add("model instance was not reused across the required warm runs")
}

if ($failures.Count -gt 0) {
    throw "Native transcription regression gate failed: $($failures -join '; '). Report: $resolvedOutput"
}

if ($MaxRealtimeFactor -gt 0 -or
    $MaxWarmWallMs -gt 0 -or
    $MaxWordErrorRate -ge 0 -or
    $MaxCharacterErrorRate -ge 0 -or
    -not [string]::IsNullOrWhiteSpace($ExpectedTranscriptSha256) -or
    -not [string]::IsNullOrWhiteSpace($ExpectedSegmentSha256) -or
    -not [string]::IsNullOrWhiteSpace($ExpectedProvider) -or
    $RequireDeterministic -or
    $RequireModelReuse -or
    $RequireReference) {
    Write-Host "Native transcription regression gates passed."
}

$reportJson
