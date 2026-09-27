param(
    [string]$ManifestPath = "",
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $root "qualification\dictation-corpus\windows-dictation-human-qualification.template.json"
}
$resolvedManifest = (Resolve-Path -LiteralPath $ManifestPath).Path
$corpusRunner = Join-Path $PSScriptRoot "test-transcription-corpus.ps1"
$targetRunner = Join-Path $PSScriptRoot "qualify-dictation-target.ps1"
$suiteRunner = Join-Path $PSScriptRoot "qualify-dictation-target-suite.ps1"
$steps = [System.Collections.Generic.List[object]]::new()

function Invoke-ExpectFailure {
    param(
        [Parameter(Mandatory = $true)] [string]$Name,
        [Parameter(Mandatory = $true)] [scriptblock]$Action,
        [string[]]$MustContain = @()
    )

    $output = ""
    $failedAsExpected = $false
    try {
        $output = & $Action 2>&1 | Out-String
    }
    catch {
        $failedAsExpected = $true
        $output = $_.Exception.Message
        if ($_.ErrorDetails.Message) { $output = "$output $($_.ErrorDetails.Message)" }
        if ($_.InvocationInfo.PositionMessage) { $output = "$output" }
        if ($_.Exception.InnerException) { $output = "$output $($_.Exception.InnerException.Message)" }
        $output = "$output $($_.ToString())"
    }

    if (-not $failedAsExpected) {
        throw "Rehearsal step '$Name' unexpectedly succeeded. Qualification tooling must not provide a fake success path."
    }
    foreach ($fragment in $MustContain) {
        if ($output -notmatch [regex]::Escape($fragment)) {
            throw "Rehearsal step '$Name' failed, but the message did not contain '$fragment'. Actual: $output"
        }
    }

    $steps.Add([pscustomobject]@{
        name = $Name
        expected = "failure"
        observed = "failure"
        passed = $true
        detail = ($output -replace "\s+", " ").Trim()
    })
}

function New-SilentWav {
    param([string]$Path, [int]$Milliseconds = 250)
    $sampleRate = 16000
    $channels = [int16]1
    $bits = [int16]16
    $samples = [int]($sampleRate * $Milliseconds / 1000)
    $dataSize = $samples * $channels * ($bits / 8)
    $stream = [System.IO.File]::Create($Path)
    try {
        $writer = [System.IO.BinaryWriter]::new($stream)
        $writer.Write([Text.Encoding]::ASCII.GetBytes("RIFF"))
        $writer.Write([int](36 + $dataSize))
        $writer.Write([Text.Encoding]::ASCII.GetBytes("WAVE"))
        $writer.Write([Text.Encoding]::ASCII.GetBytes("fmt "))
        $writer.Write([int]16)
        $writer.Write([int16]1)
        $writer.Write($channels)
        $writer.Write([int]$sampleRate)
        $writer.Write([int]($sampleRate * $channels * $bits / 8))
        $writer.Write([int16]($channels * $bits / 8))
        $writer.Write($bits)
        $writer.Write([Text.Encoding]::ASCII.GetBytes("data"))
        $writer.Write([int]$dataSize)
        $writer.Write([byte[]]::new($dataSize))
        $writer.Flush()
    }
    finally {
        $stream.Dispose()
    }
}

if ($resolvedManifest -match "(?i)(expected|observed).{0,40}(transcript)") {
    throw "Rehearsal arguments must not contain expected or observed transcript text."
}

Invoke-ExpectFailure -Name "template-validate-only" -MustContain @("placeholder case is not human-reviewed evidence") -Action {
    & $corpusRunner -ManifestPath $resolvedManifest -ValidateOnly
}

$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("muesli-dictation-rehearsal-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
try {
    $audio = Join-Path $fixtureRoot "case.wav"
    New-SilentWav -Path $audio
    $emptyReference = Join-Path $fixtureRoot "empty.txt"
    [System.IO.File]::WriteAllBytes($emptyReference, [byte[]]@())
    $modelReference = Join-Path $fixtureRoot "model.txt"
    [System.IO.File]::WriteAllText($modelReference, "model draft text")

    $emptyManifest = Join-Path $fixtureRoot "empty-reference.json"
    @{
        schemaVersion = 2
        name = "rehearsal-empty-reference"
        defaultMaxWordErrorRate = 0.15
        defaultMaxCharacterErrorRate = 0.08
        defaultMaxRealtimeFactor = 0.20
        cases = @(
            @{
                id = "short-command"
                audio = "case.wav"
                reference = "empty.txt"
                expectedOutcome = "transcript"
                categories = @("short-command", "paragraph", "dictionary", "numbers-punctuation", "accent", "silence", "background-noise")
                referenceProvenance = "human-reviewed"
                reviewedBy = "rehearsal-operator"
                reviewedAt = [DateTimeOffset]::UtcNow.ToString("O")
            }
        )
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $emptyManifest -Encoding UTF8

    Invoke-ExpectFailure -Name "empty-reference" -MustContain @("reference transcript is empty") -Action {
        & $corpusRunner -ManifestPath $emptyManifest -ValidateOnly
    }

    $modelManifest = Join-Path $fixtureRoot "model-only.json"
    @{
        schemaVersion = 2
        name = "rehearsal-model-only"
        defaultMaxWordErrorRate = 0.15
        defaultMaxCharacterErrorRate = 0.08
        defaultMaxRealtimeFactor = 0.20
        cases = @(
            @{
                id = "short-command"
                audio = "case.wav"
                reference = "model.txt"
                expectedOutcome = "transcript"
                categories = @("short-command", "paragraph", "dictionary", "numbers-punctuation", "accent", "silence", "background-noise")
                referenceProvenance = "model-generated"
                reviewedBy = "rehearsal-operator"
                reviewedAt = [DateTimeOffset]::UtcNow.ToString("O")
            }
        )
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $modelManifest -Encoding UTF8

    Invoke-ExpectFailure -Name "model-only-reference" -MustContain @("model-only references are rejected") -Action {
        & $corpusRunner -ManifestPath $modelManifest -ValidateOnly
    }

    $missingLog = Join-Path $fixtureRoot "missing-muesli.log"
    Invoke-ExpectFailure -Name "placeholder-trace-id" -MustContain @("12-character hex") -Action {
        & $targetRunner `
            -TargetKind Notepad `
            -TargetProcess notepad `
            -TraceId placeholder `
            -RequiredModelId parakeet-v3 `
            -MaxReleaseToPasteMs 3000 `
            -ReviewedBy rehearsal-operator `
            -ReviewedAt (Get-Date) `
            -TextVerified `
            -LogPath $missingLog
    }

    Set-Content -LiteralPath $missingLog -Value "[2026-08-18 12:00:00.000] INFO no dictation traces" -Encoding UTF8
    Invoke-ExpectFailure -Name "missing-fresh-trace" -MustContain @("No latency trace") -Action {
        & $targetRunner `
            -TargetKind Notepad `
            -TargetProcess notepad `
            -TraceId abcdef123456 `
            -RequiredModelId parakeet-v3 `
            -MaxReleaseToPasteMs 3000 `
            -ReviewedBy rehearsal-operator `
            -ReviewedAt (Get-Date) `
            -TextVerified `
            -LogPath $missingLog
    }

    Invoke-ExpectFailure -Name "suite-missing-reports" -MustContain @() -Action {
        & $suiteRunner -ReportPaths (Join-Path $fixtureRoot "does-not-exist.json")
    }
}
finally {
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$help = @(
    (Get-Help $corpusRunner | Out-String),
    (Get-Help $targetRunner | Out-String),
    (Get-Help $suiteRunner | Out-String)
) -join "`n"
if ($help -match "(?i)(expectedTranscript|observedTranscript)") {
    throw "Qualification help text must not include expected or observed transcript text."
}

$report = [ordered]@{
    schemaVersion = 1
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    qualified = $false
    phase2Status = "Qualification pending / Blocked on human"
    fourAppPassed = @()
    manifestPath = $resolvedManifest
    steps = $steps
    remainingHumanGates = @(
        "Record and listen to every WAV in qualification/dictation-corpus/audio",
        "Write or correct every transcript reference after listening",
        "Fill reviewedBy/reviewedAt and remove placeholder flags",
        ".\scripts\test-transcription-corpus.ps1 -ManifestPath <reviewed-manifest.json> -ValidateOnly",
        ".\scripts\qualify-dictation-corpus.ps1 -ManifestPath <reviewed-manifest.json> -ModelId parakeet-v3 -Runs 3",
        "Launch Muesli visibly, dictate into Notepad/Chrome/Office/Other, then qualify each fresh trace",
        ".\scripts\qualify-dictation-target-suite.ps1 with the four passed reports"
    )
}
$json = $report | ConvertTo-Json -Depth 8
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resolvedOutput) | Out-Null
    Set-Content -LiteralPath $resolvedOutput -Value $json -Encoding UTF8
}
Write-Host "Dictation qualification rehearsal finished. qualified=false. Phase 2 remains blocked on human audio and four paste reviews."
$json
