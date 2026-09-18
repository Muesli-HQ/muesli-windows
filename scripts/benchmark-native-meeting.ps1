param(
    [string]$MicAudioPath = "",
    [string]$SystemAudioPath = "",
    [ValidateRange(2, 20)]
    [int]$Runs = 3,
    [string]$OutputPath = "",
    [string]$ExecutablePath = "",
    [ValidateRange(0, 10)]
    [double]$MaxTotalRealtimeFactor = 0,
    [ValidateRange(0, 10)]
    [double]$MaxDiarizationRealtimeFactor = 0,
    [ValidateRange(0, 100)]
    [int]$MinSpeakerCount = 0,
    [ValidateRange(0, 100)]
    [int]$MaxSpeakerCount = 0,
    [ValidateRange(0, 1)]
    [double]$MinSpeakerCoverage = 0,
    [string]$ExpectedAsrProvider = "",
    [string]$ExpectedDiarizationProvider = "",
    [string]$ExpectedTranscriptSha256 = "",
    [string]$ExpectedDiarizationSha256 = "",
    [string]$ExpectedMergedSha256 = "",
    [switch]$RequireDeterministic,
    [switch]$RequireModelReuse
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($MicAudioPath) -and [string]::IsNullOrWhiteSpace($SystemAudioPath)) {
    throw "Pass -MicAudioPath, -SystemAudioPath, or both."
}

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $root "windows-native\Muesli.Windows.CommandHost\bin\Debug\net10.0-windows\Muesli.Windows.CommandHost.exe"
}
if (-not (Test-Path -LiteralPath $ExecutablePath)) {
    throw "Muesli command host not found: $ExecutablePath"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $benchmarkDirectory = Join-Path $root "artifacts\benchmarks"
    New-Item -ItemType Directory -Force -Path $benchmarkDirectory | Out-Null
    $OutputPath = Join-Path $benchmarkDirectory "meeting-qualification-$(Get-Date -Format 'yyyyMMdd-HHmmss').json"
}
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)

$arguments = [System.Collections.Generic.List[string]]::new()
$arguments.Add("--benchmark-meeting")
$arguments.Add("--output")
$arguments.Add($resolvedOutput)
$arguments.Add("--runs")
$arguments.Add($Runs.ToString([Globalization.CultureInfo]::InvariantCulture))
if (-not [string]::IsNullOrWhiteSpace($MicAudioPath)) {
    $arguments.Add("--mic-audio")
    $arguments.Add((Resolve-Path -LiteralPath $MicAudioPath).Path)
}
if (-not [string]::IsNullOrWhiteSpace($SystemAudioPath)) {
    $arguments.Add("--system-audio")
    $arguments.Add((Resolve-Path -LiteralPath $SystemAudioPath).Path)
}

$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = [System.IO.Path]::GetFullPath($ExecutablePath)
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.WorkingDirectory = Split-Path -Parent $startInfo.FileName
$parakeetCache = if ([string]::IsNullOrWhiteSpace($env:MUESLI_NATIVE_PARAKEET_CACHE)) {
    Join-Path $env:USERPROFILE ".cache\muesli\native-parakeet"
} else { $env:MUESLI_NATIVE_PARAKEET_CACHE }
$diarizationCache = if ([string]::IsNullOrWhiteSpace($env:MUESLI_NATIVE_DIARIZATION_CACHE)) {
    Join-Path $env:USERPROFILE ".cache\muesli\native-diarization"
} else { $env:MUESLI_NATIVE_DIARIZATION_CACHE }
$startInfo.EnvironmentVariables["MUESLI_NATIVE_PARAKEET_CACHE"] = $parakeetCache
$startInfo.EnvironmentVariables["MUESLI_NATIVE_DIARIZATION_CACHE"] = $diarizationCache
$startInfo.Arguments = ($arguments | ForEach-Object { '"' + ([string]$_).Replace('"', '\"') + '"' }) -join " "
$process = [Diagnostics.Process]::Start($startInfo)
$process.WaitForExit()
if ($process.ExitCode -ne 0) {
    if (Test-Path -LiteralPath $resolvedOutput) {
        Get-Content -LiteralPath $resolvedOutput -Raw | Write-Error
    }
    throw "Native meeting qualification failed with exit code $($process.ExitCode)."
}
if (-not (Test-Path -LiteralPath $resolvedOutput)) {
    throw "Meeting qualification completed without report: $resolvedOutput"
}

$json = Get-Content -LiteralPath $resolvedOutput -Raw
$payload = $json | ConvertFrom-Json
$result = $payload.Report.Result
$failures = [System.Collections.Generic.List[string]]::new()
if ($MaxTotalRealtimeFactor -gt 0 -and $result.TotalRealtimeFactor -gt $MaxTotalRealtimeFactor) {
    $failures.Add("total RTF $($result.TotalRealtimeFactor) exceeds $MaxTotalRealtimeFactor")
}
$diarizationRtf = if ($result.SystemDurationMs -le 0) { 0.0 } else { $result.WarmDiarizationWallMs / [double]$result.SystemDurationMs }
if ($MaxDiarizationRealtimeFactor -gt 0 -and $diarizationRtf -gt $MaxDiarizationRealtimeFactor) {
    $failures.Add("diarization RTF $diarizationRtf exceeds $MaxDiarizationRealtimeFactor")
}
if ($MinSpeakerCount -gt 0 -and $result.SpeakerCount -lt $MinSpeakerCount) {
    $failures.Add("speaker count $($result.SpeakerCount) is below $MinSpeakerCount")
}
if ($MaxSpeakerCount -gt 0 -and $result.SpeakerCount -gt $MaxSpeakerCount) {
    $failures.Add("speaker count $($result.SpeakerCount) exceeds $MaxSpeakerCount")
}
if ($MinSpeakerCoverage -gt 0 -and $result.SpeakerCoverage -lt $MinSpeakerCoverage) {
    $failures.Add("speaker coverage $($result.SpeakerCoverage) is below $MinSpeakerCoverage")
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedAsrProvider) -and $result.AsrProvider -ne $ExpectedAsrProvider) {
    $failures.Add("ASR provider '$($result.AsrProvider)' does not match '$ExpectedAsrProvider'")
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedDiarizationProvider) -and $result.DiarizationProvider -ne $ExpectedDiarizationProvider) {
    $failures.Add("diarization provider '$($result.DiarizationProvider)' does not match '$ExpectedDiarizationProvider'")
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedTranscriptSha256) -and $result.TranscriptSha256 -ne $ExpectedTranscriptSha256.ToLowerInvariant()) {
    $failures.Add("transcript hash changed")
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedDiarizationSha256) -and $result.DiarizationLayoutSha256 -ne $ExpectedDiarizationSha256.ToLowerInvariant()) {
    $failures.Add("diarization layout hash changed")
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedMergedSha256) -and $result.MergedTranscriptSha256 -ne $ExpectedMergedSha256.ToLowerInvariant()) {
    $failures.Add("merged transcript hash changed")
}
if ($RequireDeterministic -and
    (-not $result.DeterministicTranscript -or
     -not $result.DeterministicSegments -or
     -not $result.DeterministicDiarization -or
     -not $result.DeterministicMerge -or
     -not $result.DiarizationSegmentsChronological)) {
    $failures.Add("meeting transcript, segments, diarization, or merge was not deterministic and chronological")
}
if ($RequireModelReuse) {
    $requiresAsrReuse = -not [string]::IsNullOrWhiteSpace($MicAudioPath) -or
        -not [string]::IsNullOrWhiteSpace($SystemAudioPath)
    $requiresDiarizationReuse = -not [string]::IsNullOrWhiteSpace($SystemAudioPath)
    if ($requiresAsrReuse -and -not $result.AsrModelReused) {
        $failures.Add("ASR model instance was not reused for a requested ASR stream")
    }
    if ($requiresDiarizationReuse -and -not $result.DiarizationModelReused) {
        $failures.Add("diarization model instance was not reused for a requested system-audio stream")
    }
}

Write-Host "Native meeting qualification complete: $resolvedOutput"
if ($failures.Count -gt 0) {
    throw "Native meeting qualification gates failed: $($failures -join '; ')"
}
Write-Host "Native meeting qualification gates passed."
$json
