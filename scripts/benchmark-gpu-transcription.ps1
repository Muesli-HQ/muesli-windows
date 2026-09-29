<#
.SYNOPSIS
    Reproducible CPU-versus-GPU transcription qualification for Muesli Windows.

.DESCRIPTION
    Runs the shipping CommandHost benchmark once per mapping of provider x model and
    writes one JSON per run plus a summary Markdown table. Every number comes from a real
    inference through the production native path; the harness never fabricates a GPU claim.

    Models whose packaged test audio is not present are skipped and reported as skipped.

.PARAMETER OutputDirectory
    Where the JSON evidence and BENCHMARK-SUMMARY.md are written.

.PARAMETER Providers
    Providers to compare. `auto` follows the persisted product setting.

.PARAMETER Runs
    Warm runs inside each benchmark invocation. Two runs also prove transcript determinism.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = "qualification/gpu-qualification",
    [string[]]$Providers = @("cpu", "cuda"),
    [string[]]$Models,
    [int]$Runs = 2,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $root $OutputDirectory
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

$hostProject = Join-Path $root "windows-native/Muesli.Windows.CommandHost/Muesli.Windows.CommandHost.csproj"
$commandHost = Join-Path $root "windows-native/Muesli.Windows.CommandHost/bin/Debug/net10.0-windows/Muesli.Windows.CommandHost.exe"

if (-not $SkipBuild) {
    Write-Host "Building CommandHost..."
    $localDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    $dotnet = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }
    & $dotnet build $hostProject --no-restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "CommandHost build failed." }
}
if (-not (Test-Path $commandHost)) { throw "CommandHost not found at $commandHost" }

$cache = Join-Path $env:USERPROFILE ".cache/muesli"
$cases = @(
    @{ Id = "parakeet-v3"; Audio = "$cache/native-parakeet/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/test_wavs/en.wav" },
    @{ Id = "parakeet-unified-en-int8"; Audio = "$cache/native-asr/sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-non-streaming/test_wavs/0.wav" },
    @{ Id = "parakeet-v2-en-int8"; Audio = "$cache/native-asr/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8/test_wavs/0.wav" },
    @{ Id = "whisper-tiny-en"; Audio = "$cache/native-asr/sherpa-onnx-whisper-tiny.en/test_wavs/0.wav" },
    @{ Id = "whisper-small-en"; Audio = "$cache/native-asr/sherpa-onnx-whisper-small.en/test_wavs/0.wav" },
    @{ Id = "whisper-medium-en"; Audio = "$cache/native-asr/sherpa-onnx-whisper-medium.en/test_wavs/0.wav" },
    @{ Id = "whisper-large-turbo-multilingual"; Audio = "$cache/native-asr/sherpa-onnx-whisper-turbo/test_wavs/0.wav" },
    @{ Id = "sensevoice-small-int8"; Audio = "$cache/native-asr/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17/test_wavs/en.wav" },
    @{ Id = "qwen3-asr-0.6b-int8"; Audio = "$cache/native-asr/sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25/test_wavs/noise1-en.wav" },
    @{ Id = "cohere-transcribe-int8-en"; Audio = "$cache/native-asr/sherpa-onnx-cohere-transcribe-14-lang-int8-2026-04-01/test_wavs/en.wav" }
)

if ($Models) {
    # `powershell -File` delivers a comma-joined string as one argument, so commas are split here.
    $wanted = @($Models | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $cases = $cases | Where-Object { $wanted -contains $_.Id }
}

$results = @()
foreach ($provider in $Providers) {
    foreach ($case in $cases) {
        if (-not (Test-Path -LiteralPath $case.Audio)) {
            Write-Warning "Skipping $($case.Id): test audio missing at $($case.Audio)"
            $results += [pscustomobject]@{
                Model = $case.Id; Provider = $provider; Status = "audio-missing"
                InitMs = $null; WarmMs = $null; Rtf = $null; Deterministic = $null; TranscriptSha256 = ""
            }
            continue
        }

        $outputPath = Join-Path $outputRoot ("{0}.{1}.json" -f $case.Id, $provider)
        Write-Host "Benchmarking $($case.Id) on $provider..."
        & $commandHost --benchmark-native --audio $case.Audio --model $case.Id --runs $Runs `
            --provider $provider --output $outputPath | Out-Null
        $exit = $LASTEXITCODE
        if (-not (Test-Path $outputPath)) {
            $results += [pscustomobject]@{
                Model = $case.Id; Provider = $provider; Status = "failed(exit=$exit)"
                InitMs = $null; WarmMs = $null; Rtf = $null; Deterministic = $null; TranscriptSha256 = ""
            }
            continue
        }

        $payload = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json
        $result = $payload.Report.Results | Select-Object -First 1
        $results += [pscustomobject]@{
            Model = $case.Id
            Provider = $provider
            Status = if ($result.Success) { "ok" } else { "inference-failed" }
            InitMs = $result.FirstRunModelInitMs
            WarmMs = $result.WarmInferenceMs
            Rtf = [math]::Round([double]$result.RealtimeFactor, 4)
            Deterministic = $result.DeterministicOutput
            TranscriptSha256 = $result.TranscriptSha256
        }
    }
}

# Transcript equality between providers is the strong form of "the same model ran".
$comparisons = @()
foreach ($group in $results | Where-Object { $_.Status -eq "ok" } | Group-Object Model) {
    $hashes = $group.Group | Where-Object { $_.TranscriptSha256 } | Select-Object -ExpandProperty TranscriptSha256 -Unique
    $comparisons += [pscustomobject]@{
        Model = $group.Name
        Providers = ($group.Group.Provider -join ", ")
        TranscriptMatch = ($hashes.Count -le 1)
    }
}

$summaryPath = Join-Path $outputRoot "BENCHMARK-SUMMARY.md"
$lines = @(
    "# CPU versus GPU transcription benchmarks",
    "",
    "Generated $(Get-Date -Format o) on the local qualification host.",
    "",
    "Each row is one real inference run through the shipping native path with the packaged CPU",
    "runtime or the verified NVIDIA CUDA acceleration pack. `TranscriptMatch` compares the",
    "SHA-256 of the produced transcript across providers for the same model and audio.",
    "",
    "| Model | Provider | Status | Model init ms | Warm inference ms | RTF | Deterministic |",
    "|---|---|---|---:|---:|---:|---|"
)
foreach ($row in $results) {
    $lines += ("| {0} | {1} | {2} | {3} | {4} | {5} | {6} |" -f `
        $row.Model, $row.Provider, $row.Status, $row.InitMs, $row.WarmMs, $row.Rtf, $row.Deterministic)
}
$lines += @("", "## Transcript equality across providers", "", "| Model | Providers | Transcript match |", "|---|---|---|")
foreach ($comparison in $comparisons) {
    $lines += ("| {0} | {1} | {2} |" -f $comparison.Model, $comparison.Providers, $comparison.TranscriptMatch)
}
Set-Content -LiteralPath $summaryPath -Value ($lines -join [Environment]::NewLine)

$results | Format-Table -AutoSize
Write-Host "Wrote $summaryPath"
