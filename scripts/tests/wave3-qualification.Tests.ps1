$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptRoot
$runner = Join-Path $scriptRoot "qualify-wave3-automatable.ps1"

Describe "Wave 3 automatable qualification" {
    It "writes an honest report when the test run is deliberately skipped" {
        $scratch = Join-Path ([IO.Path]::GetTempPath()) ("muesli-wave3-pester-" + [guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Force -Path $scratch | Out-Null
        try {
            $output = & pwsh -NoProfile -ExecutionPolicy Bypass -File $runner -SkipDotnetTests -OutputDirectory $scratch 2>&1 | Out-String
            $LASTEXITCODE | Should Be 1
            $reportPath = Join-Path $scratch "wave3-automatable.json"
            Test-Path -LiteralPath $reportPath | Should Be $true
            $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
            $report.passed | Should Be $false
            (@($report.gates.moduleId) -contains "L18") | Should Be $true
            (@($report.gates.moduleId) -contains "L39") | Should Be $true
            $report.uiAutomation.status | Should Be "Skipped"
            (@($report.nonClaims) -contains "No live Zoom, Teams, Google Meet, or Webex session was started.") | Should Be $true
            $report.powershellSyntax.passed | Should Be $true
            $report.dpiMatrix.passed | Should Be $true
            $report.dictationRehearsal.qualificationRemainsBlockedOnHuman | Should Be $true
        } finally {
            Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It "requires an explicit fresh-log offset instead of scanning historical logs" {
        $scratch = Join-Path ([IO.Path]::GetTempPath()) ("muesli-wave3-log-" + [guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Force -Path $scratch | Out-Null
        $log = Join-Path $scratch "muesli.log"
        Set-Content -LiteralPath $log -Value "[2026-08-26 00:00:00.000] INFO historical" -Encoding UTF8
        try {
            $output = & pwsh -NoProfile -ExecutionPolicy Bypass -File $runner -SkipDotnetTests -LogPath $log -OutputDirectory $scratch 2>&1 | Out-String
            $LASTEXITCODE | Should Be 1
            $output | Should Match "LogStartOffset is required"
        } finally {
            Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It "treats xUnit NotExecuted fixture results as prerequisite skips" {
        $source = Get-Content -LiteralPath $runner -Raw
        $source | Should Match ([regex]::Escape('outcome -in @("Skipped", "NotExecuted")'))
        $source | Should Match "unexplainedSkips"
        $source | Should Match "MUESLI_STREAMING_QUALIFICATION_MODEL"
        $source | Should Match "MUESLI_MULTISPEAKER_FIXTURE_SOURCE"
    }
}
