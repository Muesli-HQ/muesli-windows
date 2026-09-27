param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,
    [string]$OutputDirectory = "",
    [string]$ExecutablePath = "",
    [ValidateRange(2, 20)]
    [int]$Runs = 3,
    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$resolvedManifest = (Resolve-Path -LiteralPath $ManifestPath).Path
$manifestDirectory = Split-Path -Parent $resolvedManifest
$manifest = Get-Content -LiteralPath $resolvedManifest -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) {
    throw "Unsupported media-import manifest schema '$($manifest.schemaVersion)'. Expected 1."
}
$cases = @($manifest.cases)
if ($cases.Count -eq 0) {
    throw "Media-import manifest contains no cases."
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root "artifacts\benchmarks\media-imports"
}
$resolvedOutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $resolvedOutputDirectory | Out-Null
$benchmarkScript = Join-Path $PSScriptRoot "benchmark-native-transcription.ps1"

function Get-Value {
    param($Object, [string]$Name, $Default)
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return $Default }
    return $property.Value
}

function Resolve-ManifestPath {
    param([string]$Path)
    if ([IO.Path]::IsPathRooted($Path)) { return (Resolve-Path -LiteralPath $Path).Path }
    return (Resolve-Path -LiteralPath (Join-Path $manifestDirectory $Path)).Path
}

$validationFailures = [Collections.Generic.List[string]]::new()
$validatedCases = [Collections.Generic.List[object]]::new()
$caseIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($case in $cases) {
    $id = [string](Get-Value $case "id" "")
    if ([string]::IsNullOrWhiteSpace($id) -or $id -notmatch "^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$") {
        $validationFailures.Add("Invalid case id '$id'.")
        continue
    }
    if (-not $caseIds.Add($id)) {
        $validationFailures.Add("Duplicate case id '$id'.")
        continue
    }
    try {
        $mediaPath = Resolve-ManifestPath ([string](Get-Value $case "media" ""))
        $expectedExtension = ([string](Get-Value $case "expectedExtension" ([IO.Path]::GetExtension($mediaPath)))).ToLowerInvariant()
        $actualExtension = [IO.Path]::GetExtension($mediaPath).ToLowerInvariant()
        if (-not $expectedExtension.StartsWith(".")) { $expectedExtension = ".$expectedExtension" }
        if ($actualExtension -ne $expectedExtension) {
            throw "actual extension '$actualExtension' does not match expectedExtension '$expectedExtension'"
        }

        $referencePath = $null
        $referenceValue = [string](Get-Value $case "reference" "")
        if (-not [string]::IsNullOrWhiteSpace($referenceValue)) {
            $referencePath = Resolve-ManifestPath $referenceValue
            $provenance = [string](Get-Value $case "referenceProvenance" "")
            if ($provenance -notin @("human-transcribed", "human-reviewed")) {
                throw "referenceProvenance must be human-transcribed or human-reviewed"
            }
        }

        $expectedOutcome = ([string](Get-Value $case "expectedOutcome" "success")).ToLowerInvariant()
        if ($expectedOutcome -notin @("success", "decode-rejected")) {
            throw "expectedOutcome must be success or decode-rejected"
        }

        $validatedCases.Add([pscustomobject]@{
            id = $id
            mediaPath = $mediaPath
            extension = $actualExtension
            mediaSha256 = (Get-FileHash -LiteralPath $mediaPath -Algorithm SHA256).Hash.ToLowerInvariant()
            referencePath = $referencePath
            maxRealtimeFactor = [double](Get-Value $case "maxRealtimeFactor" (Get-Value $manifest "defaultMaxRealtimeFactor" 0.0))
            maxWarmWallMs = [int](Get-Value $case "maxWarmWallMs" (Get-Value $manifest "defaultMaxWarmWallMs" 0))
            maxWordErrorRate = [double](Get-Value $case "maxWordErrorRate" 1.0)
            maxCharacterErrorRate = [double](Get-Value $case "maxCharacterErrorRate" 1.0)
            minimumTranscriptCharacters = [int](Get-Value $case "minimumTranscriptCharacters" 1)
            minimumDecodedDurationMs = [int](Get-Value $case "minimumDecodedDurationMs" 1)
            expectedOutcome = $expectedOutcome
            expectedErrorContains = [string](Get-Value $case "expectedErrorContains" "")
        })
    }
    catch {
        $validationFailures.Add("Case '$id': $($_.Exception.Message)")
    }
}
if ($validationFailures.Count -gt 0) {
    throw "Media-import manifest validation failed: $($validationFailures -join '; ')"
}

if ($ValidateOnly) {
    [ordered]@{
        schemaVersion = 1
        manifestPath = $resolvedManifest
        validationPassed = $true
        caseCount = $validatedCases.Count
        cases = $validatedCases
    } | ConvertTo-Json -Depth 8
    exit 0
}

$results = [Collections.Generic.List[object]]::new()
foreach ($case in $validatedCases) {
    $reportPath = Join-Path $resolvedOutputDirectory "$($case.id).json"
    $arguments = @{
        AudioPath = $case.mediaPath
        Runs = $Runs
        OutputPath = $reportPath
        RequireDeterministic = $true
        RequireModelReuse = $true
    }
    if (-not [string]::IsNullOrWhiteSpace($ExecutablePath)) { $arguments["ExecutablePath"] = $ExecutablePath }
    if ($case.maxRealtimeFactor -gt 0) { $arguments["MaxRealtimeFactor"] = $case.maxRealtimeFactor }
    if ($case.maxWarmWallMs -gt 0) { $arguments["MaxWarmWallMs"] = $case.maxWarmWallMs }
    if ($null -ne $case.referencePath) {
        $arguments["ReferencePath"] = $case.referencePath
        $arguments["MaxWordErrorRate"] = $case.maxWordErrorRate
        $arguments["MaxCharacterErrorRate"] = $case.maxCharacterErrorRate
    }

    $invocationPassed = $true
    $invocationError = ""
    try {
        & $benchmarkScript @arguments | Out-Null
    }
    catch {
        $invocationPassed = $false
        $invocationError = $_.Exception.Message
    }

    $benchmarkResult = $null
    if (Test-Path -LiteralPath $reportPath) {
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        $benchmarkResult = @($report.Report.Results)[0]
    }
    $actualError = if ($null -ne $benchmarkResult -and -not [string]::IsNullOrWhiteSpace($benchmarkResult.ErrorMessage)) {
        [string]$benchmarkResult.ErrorMessage
    } else { $invocationError }
    if ($case.expectedOutcome -eq "decode-rejected") {
        $passed = -not $invocationPassed -and $null -ne $benchmarkResult -and -not $benchmarkResult.Success
        if ($passed -and -not [string]::IsNullOrWhiteSpace($case.expectedErrorContains) -and
            $actualError.IndexOf($case.expectedErrorContains, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            $passed = $false
        }
        $errorMessage = if ($passed) { "" } else {
            "Expected a clear decode rejection containing '$($case.expectedErrorContains)', received '$actualError'."
        }
    } else {
        $passed = $invocationPassed -and $null -ne $benchmarkResult -and $benchmarkResult.Success
        $errorMessage = if ($passed) { "" } else { $actualError }
        if ($passed -and $benchmarkResult.TranscriptCharacterCount -lt $case.minimumTranscriptCharacters) {
            $passed = $false
            $errorMessage = "Transcript contained $($benchmarkResult.TranscriptCharacterCount) characters; required $($case.minimumTranscriptCharacters)."
        }
        if ($passed -and $benchmarkResult.AudioDurationMs -lt $case.minimumDecodedDurationMs) {
            $passed = $false
            $errorMessage = "Decoded audio duration was $($benchmarkResult.AudioDurationMs) ms; required $($case.minimumDecodedDurationMs) ms."
        }
    }
    $results.Add([pscustomobject]@{
        id = $case.id
        extension = $case.extension
        expectedOutcome = $case.expectedOutcome
        passed = $passed
        error = $errorMessage
        warmWallMs = if ($null -eq $benchmarkResult) { 0 } else { $benchmarkResult.WarmWallMs }
        decodedAudioDurationMs = if ($null -eq $benchmarkResult) { 0 } else { $benchmarkResult.AudioDurationMs }
        realtimeFactor = if ($null -eq $benchmarkResult) { 0 } else { $benchmarkResult.RealtimeFactor }
        transcriptCharacters = if ($null -eq $benchmarkResult) { 0 } else { $benchmarkResult.TranscriptCharacterCount }
        wordErrorRate = if ($null -eq $benchmarkResult) { $null } else { $benchmarkResult.WordErrorRate }
        characterErrorRate = if ($null -eq $benchmarkResult) { $null } else { $benchmarkResult.CharacterErrorRate }
        reportPath = $reportPath
    })
}

$failed = @($results | Where-Object { -not $_.passed })
$summary = [ordered]@{
    schemaVersion = 1
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    manifestPath = $resolvedManifest
    caseCount = $results.Count
    passedCaseCount = $results.Count - $failed.Count
    failedCaseCount = $failed.Count
    passed = $failed.Count -eq 0
    formats = @($results | ForEach-Object extension | Sort-Object -Unique)
    cases = $results
}
$summaryPath = Join-Path $resolvedOutputDirectory "media-import-summary.json"
$json = $summary | ConvertTo-Json -Depth 8
Set-Content -LiteralPath $summaryPath -Value $json -Encoding UTF8
Write-Host "Media-import qualification report written: $summaryPath"
if ($failed.Count -gt 0) {
    throw "Media-import qualification failed for: $($failed.id -join ', ')"
}
$json
