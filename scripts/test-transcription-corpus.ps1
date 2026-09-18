param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,
    [string]$OutputDirectory = "",
    [string]$ExecutablePath = "",
    [string]$ModelId = "",
    [ValidateSet("", "cuda", "cpu")]
    [string]$Provider = "",
    [ValidateRange(2, 20)]
    [int]$Runs = 3,
    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$resolvedManifest = (Resolve-Path -LiteralPath $ManifestPath).Path
$manifestDirectory = Split-Path -Parent $resolvedManifest
$manifest = Get-Content -LiteralPath $resolvedManifest -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 2) {
    throw "Unsupported dictation corpus schema version '$($manifest.schemaVersion)'. Expected 2."
}

function Get-Value {
    param($Object, [string]$Name, $Default)
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return $Default }
    return $property.Value
}

function Resolve-CorpusPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { throw "a required corpus path is empty" }
    if ([System.IO.Path]::IsPathRooted($Path)) { return (Resolve-Path -LiteralPath $Path).Path }
    return (Resolve-Path -LiteralPath (Join-Path $manifestDirectory $Path)).Path
}

function Test-Flag {
    param($Value)
    if ($null -eq $Value) { return $false }
    if ($Value -is [bool]) { return [bool]$Value }
    return [string]$Value -eq "true"
}

function Test-PlaceholderIdentity {
    param([string]$Value)
    return $Value -match "(?i)^(placeholder|todo|tbd|unreviewed|automation|ci|n/?a|unknown|fake|example|dummy)$"
}

function Get-Sha256 {
    param([string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try {
            return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace("-", "").ToLowerInvariant()
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $sha.Dispose()
    }
}

$cases = @($manifest.cases)
if ($cases.Count -eq 0) { throw "Dictation corpus contains no cases: $resolvedManifest" }
$failures = [System.Collections.Generic.List[string]]::new()
$validated = [System.Collections.Generic.List[object]]::new()
$ids = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$covered = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$requiredCategories = @("short-command", "paragraph", "dictionary", "numbers-punctuation", "accent", "silence", "background-noise")
$allowedCategories = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($requiredCategory in $requiredCategories) { [void]$allowedCategories.Add($requiredCategory) }

foreach ($case in $cases) {
    $id = [string](Get-Value $case "id" "")
    if ($id -notmatch "^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$") {
        $failures.Add("Invalid case id '$id'.")
        continue
    }
    if (-not $ids.Add($id)) {
        $failures.Add("Duplicate case id '$id'.")
        continue
    }

    try {
        $outcome = ([string](Get-Value $case "expectedOutcome" "")).Trim().ToLowerInvariant()
        if ($outcome -notin @("transcript", "no-speech")) { throw "expectedOutcome must be transcript or no-speech" }
        $categories = @((Get-Value $case "categories" @()) | ForEach-Object { ([string]$_).Trim().ToLowerInvariant() })
        if ($categories.Count -eq 0) { throw "at least one category is required" }
        foreach ($category in $categories) {
            if (-not $allowedCategories.Contains($category)) {
                throw "unknown corpus category '$category'; allowed categories are $($requiredCategories -join ', ')"
            }
            [void]$covered.Add($category)
        }

        if (Test-Flag (Get-Value $case "placeholder" $false)) {
            throw "placeholder case is not human-reviewed evidence; record WAV, listen, write the reference, fill reviewedBy/reviewedAt, and remove placeholder"
        }

        $provenance = ([string](Get-Value $case "referenceProvenance" "")).Trim().ToLowerInvariant()
        if ($provenance -match "model") {
            throw "model-only references are rejected; a human must listen and set human-transcribed or human-reviewed"
        }
        if ($provenance -notin @("human-transcribed", "human-reviewed")) {
            throw "referenceProvenance must be human-transcribed or human-reviewed"
        }
        if ($outcome -eq "no-speech" -and $provenance -ne "human-reviewed") {
            throw "no-speech audio must be explicitly human-reviewed"
        }
        $reviewedBy = ([string](Get-Value $case "reviewedBy" "")).Trim()
        $reviewedAtValue = Get-Value $case "reviewedAt" ""
        $reviewedAt = [string]$reviewedAtValue
        $parsedReviewDate = [DateTimeOffset]::MinValue
        $reviewedAtValid = $false
        if ($reviewedAtValue -is [DateTimeOffset]) {
            $parsedReviewDate = [DateTimeOffset]$reviewedAtValue
            $reviewedAtValid = $true
        } elseif ($reviewedAtValue -is [DateTime]) {
            $parsedReviewDate = [DateTimeOffset]$reviewedAtValue
            $reviewedAtValid = $true
        } elseif (-not [string]::IsNullOrWhiteSpace($reviewedAt)) {
            $reviewedAtValid = [DateTimeOffset]::TryParse(
                $reviewedAt,
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::RoundtripKind,
                [ref]$parsedReviewDate)
        }
        if ([string]::IsNullOrWhiteSpace($reviewedBy) -or
            (Test-PlaceholderIdentity $reviewedBy) -or
            -not $reviewedAtValid) {
            throw "reviewedBy must be a real human reviewer identity and reviewedAt must be a valid timestamp"
        }

        $audioPath = Resolve-CorpusPath ([string](Get-Value $case "audio" ""))
        $referencePath = ""
        $referenceSha = ""
        $maxWer = -1.0
        $maxCer = -1.0
        if ($outcome -eq "transcript") {
            $referencePath = Resolve-CorpusPath ([string](Get-Value $case "reference" ""))
            $referenceBytes = [System.IO.File]::ReadAllBytes($referencePath)
            $referenceText = [System.Text.Encoding]::UTF8.GetString($referenceBytes).Trim([char]0xFEFF)
            if ($referenceBytes.Length -eq 0 -or [string]::IsNullOrWhiteSpace($referenceText)) {
                throw "reference transcript is empty"
            }
            if ($referenceText -match "(?i)^\s*(placeholder|todo|tbd|\[model)") {
                throw "reference transcript still looks like a placeholder or model dump; a human must listen and write it"
            }
            $referenceSha = Get-Sha256 $referencePath
            $maxWer = [double](Get-Value $case "maxWordErrorRate" (Get-Value $manifest "defaultMaxWordErrorRate" -1))
            $maxCer = [double](Get-Value $case "maxCharacterErrorRate" (Get-Value $manifest "defaultMaxCharacterErrorRate" -1))
            if ($maxWer -lt 0 -or $maxWer -ge 1 -or $maxCer -lt 0 -or $maxCer -ge 1) {
                throw "transcript cases require explicit WER and CER gates between 0 (inclusive) and 1 (exclusive)"
            }
        }

        $maxRtf = [double](Get-Value $case "maxRealtimeFactor" (Get-Value $manifest "defaultMaxRealtimeFactor" -1))
        if ($maxRtf -le 0 -or $maxRtf -gt 1) {
            throw "every corpus case requires an explicit positive RTF gate no greater than 1.0 (target ≤0.20)"
        }

        $validated.Add([pscustomobject]@{
            id = $id
            audioPath = $audioPath
            referencePath = $referencePath
            expectedOutcome = $outcome
            categories = $categories
            referenceProvenance = $provenance
            reviewedBy = $reviewedBy
            reviewedAt = $parsedReviewDate.ToString("O")
            audioSha256 = Get-Sha256 $audioPath
            referenceSha256 = $referenceSha
            maxWordErrorRate = $maxWer
            maxCharacterErrorRate = $maxCer
            maxRealtimeFactor = $maxRtf
            maxWarmWallMs = [int](Get-Value $case "maxWarmWallMs" (Get-Value $manifest "defaultMaxWarmWallMs" 0))
        })
    }
    catch {
        $failures.Add("Case '$id': $($_.Exception.Message)")
    }
}

foreach ($category in $requiredCategories) {
    if (-not $covered.Contains($category)) { $failures.Add("Corpus is missing required category '$category'.") }
}
if ($failures.Count -gt 0) { throw "Dictation corpus validation failed: $($failures -join '; ')" }

if ($ValidateOnly) {
    [ordered]@{
        schemaVersion = 2
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        manifestPath = $resolvedManifest
        validationPassed = $true
        caseCount = $validated.Count
        coveredCategories = @($covered)
        cases = $validated
    } | ConvertTo-Json -Depth 8
    exit 0
}

if ([string]::IsNullOrWhiteSpace($ModelId)) { throw "-ModelId is required; corpus runs may not use an implicit model." }
if ($Provider -notin @("cpu", "cuda")) { throw "-Provider cpu or -Provider cuda is required for a qualification run." }
$catalogPath = Join-Path $root "qualification\cpu-catalog\advertised-cpu-models.json"
$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
$advertisedModelIds = @($catalog.models | ForEach-Object { [string]$_.id })
if ($advertisedModelIds -notcontains $ModelId) {
    throw "-ModelId '$ModelId' is not an advertised offline model in $catalogPath"
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root "artifacts\benchmarks\dictation-corpus\$ModelId-$Provider"
}
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null
$benchmarkScript = Join-Path $PSScriptRoot "benchmark-native-transcription.ps1"
$oldParakeetProvider = $env:MUESLI_PARAKEET_PROVIDER
$oldAsrProvider = $env:MUESLI_ASR_PROVIDER
$env:MUESLI_PARAKEET_PROVIDER = $Provider
$env:MUESLI_ASR_PROVIDER = $Provider
$results = [System.Collections.Generic.List[object]]::new()

try {
    foreach ($case in $validated) {
        $reportPath = Join-Path $resolvedOutput "$($case.id).json"
        $arguments = @{
            AudioPath = $case.audioPath
            ModelId = $ModelId
            Runs = $Runs
            OutputPath = $reportPath
            ExpectedProvider = $Provider
            RequireDeterministic = $true
            RequireModelReuse = $true
            RequireReference = $case.expectedOutcome -eq "transcript"
        }
        if (-not [string]::IsNullOrWhiteSpace($ExecutablePath)) { $arguments.ExecutablePath = $ExecutablePath }
        if ($case.expectedOutcome -eq "transcript") {
            $arguments.ReferencePath = $case.referencePath
            $arguments.MaxWordErrorRate = $case.maxWordErrorRate
            $arguments.MaxCharacterErrorRate = $case.maxCharacterErrorRate
        }
        if ($case.maxRealtimeFactor -gt 0) { $arguments.MaxRealtimeFactor = $case.maxRealtimeFactor }
        if ($case.maxWarmWallMs -gt 0) { $arguments.MaxWarmWallMs = $case.maxWarmWallMs }

        $passed = $true
        $errorMessage = ""
        try { & $benchmarkScript @arguments | Out-Null } catch { $passed = $false; $errorMessage = $_.Exception.Message }
        $nativeResult = $null
        if (Test-Path -LiteralPath $reportPath) {
            $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
            $nativeResult = @($report.Report.Results)[0]
        }
        if ($passed -and $null -ne $nativeResult) {
            if ($nativeResult.ModelName -ne $ModelId) {
                $passed = $false
                $errorMessage = "Model identity was '$($nativeResult.ModelName)' instead of '$ModelId'."
            } elseif ($case.expectedOutcome -eq "no-speech" -and [int]$nativeResult.TranscriptCharacterCount -ne 0) {
                $passed = $false
                $errorMessage = "Expected no speech, but the model emitted $($nativeResult.TranscriptCharacterCount) characters."
            } elseif ($case.expectedOutcome -eq "transcript" -and [int]$nativeResult.TranscriptCharacterCount -le 0) {
                $passed = $false
                $errorMessage = "Expected transcript text, but the model emitted none."
            } elseif ([int]$nativeResult.RunCount -lt $Runs) {
                $passed = $false
                $errorMessage = "Only $($nativeResult.RunCount) run(s) were recorded; qualification requires $Runs."
            } elseif ($case.expectedOutcome -eq "transcript" -and
                      (-not [bool]$nativeResult.DeterministicOutput -or
                       -not [bool]$nativeResult.DeterministicSegments -or
                       -not [bool]$nativeResult.ModelInstanceReused)) {
                $passed = $false
                $errorMessage = "Transcript qualification requires deterministic text/segments and model reuse."
            }
        }
        $results.Add([pscustomobject]@{
            id = $case.id
            categories = $case.categories
            expectedOutcome = $case.expectedOutcome
            passed = $passed
            error = $errorMessage
            modelId = if ($null -eq $nativeResult) { "" } else { $nativeResult.ModelName }
            provider = if ($null -eq $nativeResult) { "" } else { $nativeResult.WarmBackend }
            wordErrorRate = if ($null -eq $nativeResult) { $null } else { $nativeResult.WordErrorRate }
            characterErrorRate = if ($null -eq $nativeResult) { $null } else { $nativeResult.CharacterErrorRate }
            realtimeFactor = if ($null -eq $nativeResult) { $null } else { $nativeResult.RealtimeFactor }
            warmWallMs = if ($null -eq $nativeResult) { $null } else { $nativeResult.WarmWallMs }
            runCount = if ($null -eq $nativeResult) { $null } else { $nativeResult.RunCount }
            deterministicOutput = if ($null -eq $nativeResult) { $null } else { $nativeResult.DeterministicOutput }
            deterministicSegments = if ($null -eq $nativeResult) { $null } else { $nativeResult.DeterministicSegments }
            modelInstanceReused = if ($null -eq $nativeResult) { $null } else { $nativeResult.ModelInstanceReused }
            reportPath = $reportPath
        })
    }
}
finally {
    $env:MUESLI_PARAKEET_PROVIDER = $oldParakeetProvider
    $env:MUESLI_ASR_PROVIDER = $oldAsrProvider
}

$failed = @($results | Where-Object { -not $_.passed })
$summary = [ordered]@{
    schemaVersion = 2
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    manifestPath = $resolvedManifest
    modelId = $ModelId
    provider = $Provider
    qualificationContract = [ordered]@{
        requiredProvider = $Provider
        maxWordErrorRate = [double](Get-Value $manifest "defaultMaxWordErrorRate" -1)
        maxCharacterErrorRate = [double](Get-Value $manifest "defaultMaxCharacterErrorRate" -1)
        maxRealtimeFactor = [double](Get-Value $manifest "defaultMaxRealtimeFactor" -1)
        requiredRuns = $Runs
        requiresHumanReference = $true
        requiresDeterministicOutput = $true
        requiresModelReuse = $true
        requiredCorpusCategories = $requiredCategories
    }
    coveredCategories = @($covered | Sort-Object)
    caseCount = $results.Count
    passed = $failed.Count -eq 0
    cases = $results
}
$summaryPath = Join-Path $resolvedOutput "corpus-summary.json"
$json = $summary | ConvertTo-Json -Depth 8
Set-Content -LiteralPath $summaryPath -Value $json -Encoding UTF8
Write-Host "Dictation corpus report written: $summaryPath"
if ($failed.Count -gt 0) { throw "Dictation corpus qualification failed for: $($failed.id -join ', ')" }
$json
