[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputDirectory = "",
    [string]$LogPath = "",
    [long]$LogStartOffset = -1,
    [switch]$SkipDotnetTests,
    [switch]$RunUiTests
)

$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root "artifacts\qualification\wave3-automatable"
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null

function Invoke-CapturedCommand {
    param(
        [Parameter(Mandatory = $true)] [string]$FilePath,
        [Parameter(Mandatory = $true)] [string[]]$ArgumentList,
        [Parameter(Mandatory = $true)] [string]$OutputPath,
        [string]$WorkingDirectory = $root,
        [hashtable]$Environment = @{}
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($name in $Environment.Keys) {
        $startInfo.Environment[$name] = [string]$Environment[$name]
    }
    $startInfo.ArgumentList.Clear()
    foreach ($argument in $ArgumentList) { [void]$startInfo.ArgumentList.Add([string]$argument) }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "Could not start '$FilePath'." }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr))
    $combined = "STDOUT`n$($stdout.Result)`nSTDERR`n$($stderr.Result)"
    [IO.File]::WriteAllText($OutputPath, $combined, [Text.UTF8Encoding]::new($false))
    [pscustomobject]@{
        exitCode = $process.ExitCode
        outputPath = $OutputPath
        output = $combined
    }
}

function Add-Gate {
    param(
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [System.Collections.Generic.List[object]]$List,
        [Parameter(Mandatory = $true)] [string]$ModuleId,
        [Parameter(Mandatory = $true)] [ValidateSet("Complete and verified", "Implemented with verification debt", "Partial", "Missing", "Excluded", "Externally blocked")]
        [string]$Status,
        [Parameter(Mandatory = $true)] [string]$Evidence,
        [Parameter(Mandatory = $true)] [string]$RemainingGate
    )

    $List.Add([ordered]@{
        moduleId = $ModuleId
        status = $Status
        evidence = $Evidence
        remainingGate = $RemainingGate
    })
}

function Parse-Trx {
    param([Parameter(Mandatory = $true)] [string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Test result file was not produced: $Path"
    }
    $document = [xml][IO.File]::ReadAllText($Path)
    $nodes = @($document.SelectNodes("//*[local-name()='UnitTestResult']"))
    if ($nodes.Count -eq 0) { throw "TRX has no UnitTestResult entries: $Path" }
    $results = foreach ($node in $nodes) {
        $message = $node.SelectSingleNode(".//*[local-name()='Message']")
        [pscustomobject]@{
            name = [string]$node.GetAttribute("testName")
            outcome = [string]$node.GetAttribute("outcome")
            duration = [string]$node.GetAttribute("duration")
            skipReason = if ($null -eq $message) { "" } else { ([string]$message.InnerText).Trim() }
        }
    }
    $summary = $document.SelectSingleNode("//*[local-name()='ResultSummary']/*[local-name()='Counters']")
    $skippedResults = @($results | Where-Object { $_.outcome -in @("Skipped", "NotExecuted") })
    [pscustomobject]@{
        path = $Path
        results = @($results)
        passed = if ($null -eq $summary) { @($results | Where-Object outcome -eq "Passed").Count } else { [int]$summary.GetAttribute("passed") }
        failed = if ($null -eq $summary) { @($results | Where-Object outcome -eq "Failed").Count } else { [int]$summary.GetAttribute("failed") }
        # xUnit's TRX adapter reports skipped facts as NotExecuted while leaving
        # both skipped and notExecuted counters at zero. Trust the result nodes,
        # so a prerequisite skip cannot disappear from qualification evidence.
        skipped = $skippedResults.Count
        total = if ($null -eq $summary) { $results.Count } else { [int]$summary.GetAttribute("total") }
    }
}

function Test-PowerShellSyntax {
    param([Parameter(Mandatory = $true)] [string]$Path)
    $tokens = $null
    $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) {
        return [pscustomobject]@{ path = $Path; passed = $false; errors = @($errors | ForEach-Object { $_.Message }) }
    }
    return [pscustomobject]@{ path = $Path; passed = $true; errors = @() }
}

$testFilter = @(
    "FullyQualifiedName~MeetingCaptureSessionTests",
    "FullyQualifiedName~MeetingCoordinatorCharacterizationTests",
    "FullyQualifiedName~MeetingFinalizationPipelineTests",
    "FullyQualifiedName~MeetingPersistenceBoundaryTests",
    "FullyQualifiedName~MeetingRecoveryWorkflowTests",
    "FullyQualifiedName~Phase3MeetingLifecycleTests",
    "FullyQualifiedName~Phase4LiveTranscriptionTests",
    "FullyQualifiedName~Phase5FinalizationTests",
    "FullyQualifiedName~Phase6DetectionTests",
    "FullyQualifiedName~L23TranscriptIntegrationTests",
    "FullyQualifiedName~TranscriptEditServiceTests",
    "FullyQualifiedName~PlaybackIntegrationContractTests",
    "FullyQualifiedName~WaveformPeakCacheTests",
    "FullyQualifiedName~CapturePrivacyAndRuntimeTests",
    "FullyQualifiedName~AtomicPersistenceTests",
    "FullyQualifiedName~SecretsAndSettingsTests",
    "FullyQualifiedName~Phase9AutomationTests",
    "FullyQualifiedName~Phase9AutomationPersistenceTests",
    "FullyQualifiedName~Phase10ComputerUseTests",
    "FullyQualifiedName~DpiManifestAndPlacementTests",
    "FullyQualifiedName~ShellCharacterizationTests",
    "FullyQualifiedName~SoundFeedbackTests",
    "FullyQualifiedName~StartupRegistrationServiceTests",
    "FullyQualifiedName~OverlayParityTests",
    "FullyQualifiedName~Phase12ProductExperienceTests"
) -join "|"

$testProject = Join-Path $root "windows-native\Muesli.Windows.Tests\Muesli.Windows.Tests.csproj"
$trxPath = Join-Path $output "wave3-automatable.trx"
$testLogPath = Join-Path $output "dotnet-test.log"
$testRun = $null
$testSummary = $null
$testFailure = ""
if (-not $SkipDotnetTests) {
    $dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
    $arguments = @(
        "test", $testProject, "-c", $Configuration, "--no-restore",
        "--filter", $testFilter,
        "--logger", "trx;LogFileName=wave3-automatable.trx",
        "--results-directory", $output,
        "--verbosity", "minimal"
    )
    $testRun = Invoke-CapturedCommand -FilePath $dotnet -ArgumentList $arguments -OutputPath $testLogPath
    try { $testSummary = Parse-Trx -Path $trxPath } catch { $testFailure = $_.Exception.Message }
}

$skipResults = if ($null -eq $testSummary) { @() } else { @($testSummary.results | Where-Object outcome -in @("Skipped", "NotExecuted")) }
$unexplainedSkips = @($skipResults | Where-Object {
    [string]::IsNullOrWhiteSpace($_.skipReason) -or
    $_.skipReason -notmatch "(?i)Set [A-Z0-9_]+ to an existing qualification-fixture directory"
})

$syntaxFiles = @(
    Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter "*.ps1" |
        Where-Object { $_.Name -match "^(qualify|benchmark|rehearse|test|verify|fresh-machine-qa)" }
)
$syntax = @($syntaxFiles | ForEach-Object { Test-PowerShellSyntax -Path $_.FullName })
$syntaxFailures = @($syntax | Where-Object { -not $_.passed })
$syntaxPath = Join-Path $output "powershell-syntax.json"
$syntax | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $syntaxPath -Encoding UTF8

# Per-monitor DPI is declared statically in the WinUI application manifest. The retired WPF phase-12
# UI verifier no longer exists (archived), so validate the shipping manifest directly. Physical
# 100/125/150/200% capture remains an explicit operator gate.
$dpiLogPath = Join-Path $output "dpi-matrix-validation.json"
$shell = (Get-Command pwsh.exe,powershell.exe -ErrorAction Stop | Select-Object -First 1).Source
$dpiScales = @(100, 125, 150, 200)
$dpiFailures = [System.Collections.Generic.List[string]]::new()
$winUiManifestPath = Join-Path $root "windows-native\Muesli.Windows.WinUI\app.manifest"
if (-not (Test-Path -LiteralPath $winUiManifestPath)) {
    $dpiFailures.Add("WinUI app.manifest was not found: $winUiManifestPath")
} else {
    [xml]$winUiManifest = Get-Content -LiteralPath $winUiManifestPath -Raw
    $dpiAwareness = @($winUiManifest.assembly.application.windowsSettings.dpiAwareness) | Select-Object -First 1
    if ([string]$dpiAwareness -ne "PerMonitorV2") {
        $dpiFailures.Add("WinUI app.manifest must declare dpiAwareness PerMonitorV2; found '$dpiAwareness'.")
    }
}
[ordered]@{
    schemaVersion = 1
    scales = $dpiScales
    manifestPath = $winUiManifestPath
    passed = $dpiFailures.Count -eq 0
    failures = @($dpiFailures)
    note = "Static per-monitor-DPI declaration check; physical capture at each scale remains an operator step."
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $dpiLogPath -Encoding UTF8
$dpiRun = [pscustomobject]@{
    exitCode = if ($dpiFailures.Count -eq 0) { 0 } else { 1 }
    logPath = $dpiLogPath
}

$rehearsalPath = Join-Path $output "dictation-qualification-rehearsal.json"
$rehearsalLogPath = Join-Path $output "dictation-qualification-rehearsal.log"
$rehearsalRun = Invoke-CapturedCommand -FilePath $shell -ArgumentList @(
    "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $PSScriptRoot "rehearse-dictation-qualification.ps1"),
    "-OutputPath", $rehearsalPath
) -OutputPath $rehearsalLogPath -WorkingDirectory $root
$rehearsal = $null
if (Test-Path -LiteralPath $rehearsalPath) {
    try { $rehearsal = Get-Content -LiteralPath $rehearsalPath -Raw | ConvertFrom-Json } catch {}
}

$logScan = [ordered]@{
    status = "Skipped"
    path = $LogPath
    startOffset = $LogStartOffset
    forbiddenErrorCount = $null
    secretLikeFieldCount = $null
    remainingGate = "Launch Muesli visibly, capture the pre-launch byte offset, then rerun with -LogPath and -LogStartOffset."
}
if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
    $resolvedLog = [IO.Path]::GetFullPath($LogPath)
    if (-not (Test-Path -LiteralPath $resolvedLog -PathType Leaf)) { throw "LogPath does not exist: $resolvedLog" }
    if ($LogStartOffset -lt 0) { throw "-LogStartOffset is required when -LogPath is provided; a full historical log is not a fresh launch slice." }
    $bytes = [IO.File]::ReadAllBytes($resolvedLog)
    if ($LogStartOffset -gt $bytes.Length) { throw "LogStartOffset $LogStartOffset exceeds log length $($bytes.Length)." }
    $slice = [Text.Encoding]::UTF8.GetString($bytes, [int]$LogStartOffset, $bytes.Length - [int]$LogStartOffset)
    $forbiddenErrors = @([regex]::Matches($slice, '(?im)^.*(?:\] ERROR\b|Unhandled UI exception|XamlParseException).*$') | ForEach-Object Value)
    $secretLikeFields = @([regex]::Matches($slice, '(?im)\b(?:targetWindowTitle|meetingCode|transcript|utterance|audioPath)\s*=') | ForEach-Object Value)
    $logScan.status = if ($forbiddenErrors.Count -eq 0 -and $secretLikeFields.Count -eq 0) { "Complete and verified" } else { "Partial" }
    $logScan.path = $resolvedLog
    $logScan.forbiddenErrorCount = $forbiddenErrors.Count
    $logScan.secretLikeFieldCount = $secretLikeFields.Count
    $logScan.remainingGate = if ($forbiddenErrors.Count -eq 0 -and $secretLikeFields.Count -eq 0) { "No remaining log-slice gate." } else { "Investigate forbidden fresh-log entries before release." }
}

$uiEvidence = if ($RunUiTests) {
    if ($env:MUESLI_UI_AUTOMATION -notin @("1", "true", "TRUE", "yes", "YES")) {
        [ordered]@{ status = "Skipped"; reason = "RunUiTests requested, but MUESLI_UI_AUTOMATION is not enabled; set it to 1 on an interactive desktop." }
    } else {
        [ordered]@{ status = "Not run"; reason = "UI test execution is intentionally not embedded in this evidence runner; invoke the elevated UI test project separately." }
    }
} else {
    [ordered]@{ status = "Skipped"; reason = "Set MUESLI_UI_AUTOMATION=1 on an interactive desktop and invoke the UI test project; profile parking is an explicit user-data boundary." }
}

$gates = [System.Collections.Generic.List[object]]::new()
$automatedTestsPassed = $null -ne $testSummary -and $testSummary.failed -eq 0 -and $unexplainedSkips.Count -eq 0 -and $testSummary.total -gt 0
$automatedEvidence = if ($SkipDotnetTests) { "Dotnet test run was intentionally skipped." } elseif ($null -eq $testSummary) { "Dotnet test result unavailable: $testFailure" } else { "Release $($testSummary.passed) passed / $($testSummary.failed) failed / $($testSummary.skipped) skipped / $($testSummary.total) total; TRX=$trxPath" }

Add-Gate $gates "L18" $(if ($automatedTestsPassed) { "Implemented with verification debt" } else { "Partial" }) $automatedEvidence "Real Zoom, Teams, Google Meet, and Webex process-tree/endpoint runs plus physical mic/system route evidence."
Add-Gate $gates "L19" $(if ($automatedTestsPassed) { "Implemented with verification debt" } else { "Partial" }) $automatedEvidence "Physical lifecycle stress, suspend/resume, interruption recovery, and fresh-log evidence."
Add-Gate $gates "L20" $(if ($automatedTestsPassed) { "Implemented with verification debt" } else { "Partial" }) $automatedEvidence "Set MUESLI_STREAMING_QUALIFICATION_MODEL to a real streaming fixture and run the long soak; fixture skips are recorded in the TRX."
Add-Gate $gates "L21" $(if ($automatedTestsPassed) { "Implemented with verification debt" } else { "Partial" }) $automatedEvidence "Set MUESLI_MULTISPEAKER_FIXTURE_SOURCE to real distinct-speaker audio and complete human alias review."
Add-Gate $gates "L22" $(if ($automatedTestsPassed) { "Implemented with verification debt" } else { "Partial" }) $automatedEvidence "Run the live detection/prompt matrix against real provider windows without claiming excluded integrations."
Add-Gate $gates "L23" $(if ($automatedTestsPassed) { "Implemented with verification debt" } else { "Partial" }) $automatedEvidence "Retained-audio quality, destructive UI flow, and current-candidate foreground evidence."
Add-Gate $gates "L34" "Implemented with verification debt" "Static shell/startup tests are included; UI automation status=$($uiEvidence.status)." "Run the opt-in clean-profile UI suite on an interactive desktop and complete clean-machine onboarding/tray checks."
Add-Gate $gates "L35" $(if ($automatedTestsPassed -and $dpiRun.exitCode -eq 0) { "Implemented with verification debt" } else { "Partial" }) "DPI ValidateOnly exit=$($dpiRun.exitCode); shell/sound tests included." "Capture physical 100/125/150/200% DPI behavior on every target monitor and confirm audible routes."
Add-Gate $gates "L36" $(if ($automatedTestsPassed) { "Implemented with verification debt" } else { "Partial" }) $automatedEvidence "Run deletion against a retained profile and inspect all owned audio, journals, waveform caches, and search artifacts."
Add-Gate $gates "L37" $(if ($automatedTestsPassed) { "Implemented with verification debt" } else { "Partial" }) $automatedEvidence "Complete two sandbox workflows in the real Computer Use host."
Add-Gate $gates "L38" "Implemented with verification debt" $automatedEvidence "Keep local-only insights scope explicitly approved and review output on representative local data."
Add-Gate $gates "L39" "Implemented with verification debt" "Qualification harness ran without promoting release readiness." "Run the full current-candidate matrix, package/VM checks, clean logs, and release-owner approval."

$report = [ordered]@{
    schemaVersion = 1
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    repositoryRoot = $root
    configuration = $Configuration
    passed = $automatedTestsPassed -and $syntaxFailures.Count -eq 0 -and $dpiRun.exitCode -eq 0 -and $null -ne $rehearsal -and $rehearsal.qualified -eq $false -and $rehearsalRun.exitCode -eq 0
    test = if ($null -eq $testSummary) { [ordered]@{ skipped = [bool]$SkipDotnetTests; failure = $testFailure } } else { $testSummary }
    unexplainedSkips = @($unexplainedSkips | ForEach-Object { [ordered]@{ name = $_.name; reason = $_.skipReason } })
    powershellSyntax = [ordered]@{ passed = $syntaxFailures.Count -eq 0; reportPath = $syntaxPath; checkedFileCount = $syntax.Count; failures = $syntaxFailures }
    dpiMatrix = [ordered]@{ passed = $dpiRun.exitCode -eq 0; logPath = $dpiLogPath; exitCode = $dpiRun.exitCode }
    dictationRehearsal = [ordered]@{ passed = $null -ne $rehearsal -and $rehearsal.qualified -eq $false -and $rehearsalRun.exitCode -eq 0; reportPath = $rehearsalPath; logPath = $rehearsalLogPath; exitCode = $rehearsalRun.exitCode; qualificationRemainsBlockedOnHuman = $true }
    logScan = $logScan
    uiAutomation = $uiEvidence
    gates = @($gates)
    nonClaims = @(
        "No live Zoom, Teams, Google Meet, or Webex session was started.",
        "No physical audio-device, multi-monitor DPI, clean-VM, human corpus, or production signing evidence was fabricated.",
        "No user profile, application data, registry value, or app process was changed by this runner.",
        "No authoritative ledger, parity matrix, or launch plan status was promoted by this runner."
    )
}
$reportPath = Join-Path $output "wave3-automatable.json"
$report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Host "Wave 3 automatable qualification report: $reportPath"
Write-Host "Tests: $automatedEvidence"
Write-Host "DPI matrix ValidateOnly exit: $($dpiRun.exitCode); dictation rehearsal exit: $($rehearsalRun.exitCode)."
if (-not $report.passed) { throw "Wave 3 automatable qualification failed. See $reportPath." }
