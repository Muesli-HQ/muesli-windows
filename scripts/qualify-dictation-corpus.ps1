param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,
    [Parameter(Mandatory = $true)]
    [string]$ModelId,
    [string]$ExecutablePath = "",
    [string]$OutputDirectory = "",
    [ValidateRange(2, 20)]
    [int]$Runs = 3
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root "artifacts\benchmarks\dictation-corpus\$ModelId"
}
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null
$runner = Join-Path $PSScriptRoot "test-transcription-corpus.ps1"
$summaries = [System.Collections.Generic.List[object]]::new()

foreach ($provider in @("cpu", "cuda")) {
    $providerOutput = Join-Path $resolvedOutput $provider
    $arguments = @{
        ManifestPath = $ManifestPath
        ModelId = $ModelId
        Provider = $provider
        Runs = $Runs
        OutputDirectory = $providerOutput
    }
    if (-not [string]::IsNullOrWhiteSpace($ExecutablePath)) { $arguments.ExecutablePath = $ExecutablePath }
    & $runner @arguments | Out-Null
    $summaries.Add((Get-Content -LiteralPath (Join-Path $providerOutput "corpus-summary.json") -Raw | ConvertFrom-Json))
}

$wrongModel = @($summaries | Where-Object modelId -ne $ModelId)
$missingProvider = @($summaries | Where-Object provider -notin @("cpu", "cuda"))
$failed = @($summaries | Where-Object { -not $_.passed })
$report = [ordered]@{
    schemaVersion = 1
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    manifestPath = [System.IO.Path]::GetFullPath($ManifestPath)
    modelId = $ModelId
    providers = @($summaries.provider)
    passed = $failed.Count -eq 0 -and $wrongModel.Count -eq 0 -and $missingProvider.Count -eq 0 -and $summaries.Count -eq 2
    runs = $summaries
}
$reportPath = Join-Path $resolvedOutput "cpu-cuda-summary.json"
$json = $report | ConvertTo-Json -Depth 10
Set-Content -LiteralPath $reportPath -Value $json -Encoding UTF8
Write-Host "CPU/CUDA dictation qualification written: $reportPath"
if (-not $report.passed) { throw "CPU/CUDA dictation qualification did not pass for model '$ModelId'." }
$json
