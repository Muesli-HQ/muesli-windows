# Validates an unsigned WinUI MSIX payload without installing it.
#
# This is the MSIX replacement for the retired portable-zip smoke test. It proves the package
# contains the shipping shell, the shared Swift bridge closure, the WPF indicator companion and
# the CPU-only native runtime catalog, and that the package identity matches the release
# properties. Installing an unsigned MSIX needs Developer Mode plus elevation, so launch
# verification is recorded honestly as performed / unavailable and is never counted as a pass.

param(
    [Parameter(Mandatory = $true)]
    [string]$MsixPath,
    [string]$ReportPath = "",
    [string]$NativeInventoryPath = "",
    [string]$WorkDir = "",
    [switch]$SkipLaunch
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "read-release-properties.ps1")
. (Join-Path $PSScriptRoot "release-common.ps1")
$release = Get-MuesliReleaseProperties -Root $root

if (-not (Test-Path -LiteralPath $MsixPath)) { throw "MSIX not found: $MsixPath" }
$resolvedMsix = (Resolve-Path -LiteralPath $MsixPath).Path
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $root "artifacts\msix-smoke-report.json"
}
if ([string]::IsNullOrWhiteSpace($WorkDir)) {
    $WorkDir = Join-Path $env:TEMP ("muesli-msix-smoke-" + [Guid]::NewGuid().ToString("N"))
}

$report = [ordered]@{
    schemaVersion = 1
    passed = $false
    msixPath = $resolvedMsix
    skipLaunch = [bool]$SkipLaunch
    identity = $null
    nativeInventoryPassed = $false
    nativeInventoryPath = ""
    launchVerification = "NotPerformed"
    launchReason = ""
    failures = @()
    startedAtUtc = [DateTime]::UtcNow.ToString("o")
}

function Write-MsixSmokeReport {
    param($Report)
    $Report.finishedAtUtc = [DateTime]::UtcNow.ToString("o")
    Write-Utf8NoBomFile -Path $ReportPath -Content (($Report | ConvertTo-Json -Depth 10) + "`n")
}

$failures = [System.Collections.Generic.List[string]]::new()

try {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $archive = [System.IO.Compression.ZipFile]::OpenRead($resolvedMsix)
    try {
        $entryNames = @($archive.Entries | ForEach-Object { [Uri]::UnescapeDataString($_.FullName.Replace('\', '/')) })
    } finally {
        $archive.Dispose()
    }

    if ($entryNames -notcontains "AppxManifest.xml") {
        throw "The MSIX has no AppxManifest.xml at its root."
    }

    $manifestEntry = Join-Path $env:TEMP ("muesli-appxmanifest-" + [Guid]::NewGuid().ToString("N") + ".xml")
    & (Join-Path $PSScriptRoot "extract-win32-manifest.ps1") -MsixPath $resolvedMsix -OutputPath $manifestEntry | Out-Null
    [xml]$manifest = Get-Content -LiteralPath $manifestEntry -Raw
    Remove-Item -LiteralPath $manifestEntry -Force -ErrorAction SilentlyContinue

    $identity = $manifest.Package.Identity
    $report.identity = [ordered]@{
        name = [string]$identity.Name
        publisher = [string]$identity.Publisher
        version = [string]$identity.Version
    }
    $expectedVersion = "$($release.Version).0"
    if ([string]$identity.Name -ne "Muesli.Windows") {
        $failures.Add("MSIX identity name '$($identity.Name)' is not 'Muesli.Windows'.")
    }
    if ([string]$identity.Version -ne $expectedVersion) {
        $failures.Add("MSIX identity version '$($identity.Version)' does not match release version '$expectedVersion'.")
    }
    if ([string]::IsNullOrWhiteSpace([string]$identity.Publisher)) {
        $failures.Add("MSIX identity publisher is empty.")
    }
    # The development publisher placeholder is a release gate (IDENT-01), not a payload defect for
    # an unsigned rehearsal. Record it so no report claims a production publisher.
    $publisherIsPlaceholder = [string]$identity.Publisher -match 'CN=AppPublisher'
    $report.identity.publisherIsPlaceholder = $publisherIsPlaceholder

    $requiredEntries = @(
        "Muesli.Windows.WinUI.exe",
        "Muesli.Windows.WinUI.dll",
        "MuesliCoreABI.dll",
        "Indicator/Muesli.Windows.Indicator.Wpf.exe",
        "sherpa-onnx.dll",
        "sherpa-onnx-c-api.dll",
        "onnxruntime.dll",
        "LLamaSharp.dll",
        "llama.dll",
        "e_sqlite3.dll",
        "THIRD-PARTY-NOTICES.md",
        "WINDOWS-PRIVACY.md",
        "licenses/Apache-2.0.txt"
    )
    foreach ($required in $requiredEntries) {
        # Native dependencies can be resolved under runtimes\<rid>\native\; match by file name.
        $leaf = [IO.Path]::GetFileName($required)
        $present = $entryNames -contains $required -or @($entryNames | Where-Object { $_.EndsWith("/$leaf") }).Count -gt 0
        if (-not $present) {
            $failures.Add("MSIX payload is missing a required entry: $required")
        }
    }

    # A win-x64 package must not carry ARM64/x86/foreign runtime binaries.
    $foreignRid = @($entryNames | Where-Object {
        $segments = $_ -split '/'
        for ($index = 0; $index -lt $segments.Count - 1; $index++) {
            if ($segments[$index] -ieq 'runtimes') {
                $rid = $segments[$index + 1].ToLowerInvariant()
                if ($rid -in @('win-arm64', 'win-x86', 'win-arm', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64', 'android', 'ios')) { return $true }
            }
        }
        return $false
    })
    if ($foreignRid.Count -gt 0) {
        $failures.Add("CPU win-x64 MSIX payload contains foreign-RID content: $($foreignRid[0])")
    }

    $forbiddenCuda = @(
        "onnxruntime_providers_cuda.dll",
        "cublas64_12.dll",
        "cudart64_12.dll",
        "cudnn64_9.dll"
    )
    foreach ($name in $forbiddenCuda) {
        if ($entryNames -contains $name -or ($entryNames | Where-Object { $_.EndsWith("/$name") })) {
            $failures.Add("CPU-only MSIX payload contains a CUDA/NVIDIA runtime file: $name")
        }
    }
    $pdb = @($entryNames | Where-Object { $_.EndsWith(".pdb", [StringComparison]::OrdinalIgnoreCase) })
    if ($pdb.Count -gt 0) {
        $failures.Add("MSIX payload includes debug symbols: $($pdb[0])")
    }

    # Expand once for the native-runtime inventory against the real payload contents.
    if (Test-Path -LiteralPath $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue }
    [System.IO.Compression.ZipFile]::ExtractToDirectory($resolvedMsix, $WorkDir)
    $inventoryPath = Join-Path $WorkDir "native-runtime-inventory.json"
    try {
        & (Join-Path $PSScriptRoot "generate-native-runtime-inventory.ps1") `
            -PackageDirectory $WorkDir `
            -OutputPath $inventoryPath `
            -ExcludeDirectory Indicator `
            -AllowPlatformRuntime
        $report.nativeInventoryPassed = $true
        if (-not [string]::IsNullOrWhiteSpace($NativeInventoryPath)) {
            Copy-Item -LiteralPath $inventoryPath -Destination $NativeInventoryPath -Force
            $report.nativeInventoryPath = [IO.Path]::GetFullPath($NativeInventoryPath)
        }
    } catch {
        $failures.Add("Native-runtime inventory generation failed for the MSIX payload: $($_.Exception.Message)")
    }

    if (-not $SkipLaunch) {
        # An unsigned MSIX can only be installed when Developer Mode/sideloading is enabled and the
        # session is elevated. Record which precondition is missing rather than claiming a launch.
        $devMode = $false
        try {
            $devMode = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Name AllowDevelopmentWithoutDevLicense -ErrorAction Stop).AllowDevelopmentWithoutDevLicense -eq 1
        } catch {
            $devMode = $false
        }
        if ($devMode) {
            try {
                Add-AppxPackage -Path $resolvedMsix -ErrorAction Stop
                $report.launchVerification = "InstalledUnverified"
                $report.launchReason = "Package registered; interactive activation is exercised by the packaged UI automation suite."
            } catch {
                $report.launchVerification = "Unavailable"
                $report.launchReason = "Add-AppxPackage failed: $($_.Exception.Message)"
            }
        } else {
            $report.launchVerification = "Unavailable"
            $report.launchReason = "Developer Mode/sideloading is not enabled; an unsigned MSIX cannot be installed in this session."
        }
    }

    $report.failures = @($failures)
    $report.passed = $failures.Count -eq 0
    Write-MsixSmokeReport $report
    if ($failures.Count -gt 0) {
        throw "MSIX smoke test failed:`n - $($failures -join "`n - ")"
    }
    Write-Host "MSIX smoke test passed: $resolvedMsix"
    Write-Host "Wrote MSIX smoke report: $ReportPath"
} catch {
    if ($report.failures.Count -eq 0) { $report.failures = @("$($_.Exception.Message)") }
    $report.passed = $false
    Write-MsixSmokeReport $report
    throw
} finally {
    Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
}
