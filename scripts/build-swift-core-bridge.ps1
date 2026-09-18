# Builds the shared Swift MuesliCoreABI dynamic library and stages it plus its
# native dependency closure beside the Windows application.
#
# Toolchain discovery lives here (the build process), never in app startup. The
# packaged app must not rely on a globally installed Swift toolchain or a modified
# PATH: every required DLL is copied next to the executable and loaded by full path.
#
# This script deliberately uses only engine cmdlets and .NET types: MSBuild's Exec
# runs Windows PowerShell without a guaranteed module path, so Utility-module cmdlets
# such as Get-FileHash / ConvertTo-Json cannot be assumed.
#
# Required configuration (developer environment / CI):
#   MUESLI_SHARED_CORE_PACKAGE  Path to native/MuesliNative (contains Package.swift)
#   VCPKG_ROOT                  vcpkg root (provides sqlite3/lzfse runtime DLLs)
# Optional:
#   MUESLI_SWIFT_BIN            Explicit path to swift.exe
#   MUESLI_SWIFT_RUNTIME_DIR    Swift runtime bin dir (defaults to the installed Toolkit)
#   MUESLI_SWIFT_BRIDGE_SCRATCH External scratch/cache root (kept out of both repos)

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$AppXDir,
    [string]$Configuration = 'Release',
    [string]$SharedCorePackage,
    [switch]$Require,
    [switch]$SkipBuild,
    [switch]$Force,
    # Run the portable MuesliCore/ABI test suites from the isolated Windows package stage.
    [switch]$Test
)

$ErrorActionPreference = 'Stop'
$ManifestName = 'muesli-swift-bridge-manifest.tsv'
$RequiredDlls = @('MuesliCoreABI.dll', 'swiftCore.dll', 'Foundation.dll')

function Resolve-FirstPath {
    param([string[]]$Candidates)
    foreach ($candidate in $Candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return (Resolve-Path -LiteralPath $candidate).Path }
    }
    return $null
}

function Get-FileSha256 {
    param([string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash([System.IO.File]::ReadAllBytes($Path)))).Replace('-', '').ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

function Get-SwiftSourceHash {
    param([string]$Package)
    $files = @()
    $packageSwift = Join-Path $Package 'Package.swift'
    if (Test-Path -LiteralPath $packageSwift) { $files += (Get-Item -LiteralPath $packageSwift) }
    # The Windows lock is part of the build input: changing it must invalidate staged output.
    $windowsLock = Join-Path $Package 'Package.resolved.windows'
    if (Test-Path -LiteralPath $windowsLock) { $files += (Get-Item -LiteralPath $windowsLock) }
    $sourcesDir = Join-Path $Package 'Sources'
    if (Test-Path -LiteralPath $sourcesDir) {
        $files += Get-ChildItem -Path $sourcesDir -Recurse -File -Filter '*.swift' -ErrorAction SilentlyContinue
    }

    $builder = New-Object System.Text.StringBuilder
    foreach ($file in ($files | Sort-Object FullName)) {
        $relative = $file.FullName.Substring($Package.Length).TrimStart('\', '/').Replace('\', '/')
        [void]$builder.Append($relative).Append(':').Append($file.Length).Append(';')
        [void]$builder.Append((Get-FileSha256 -Path $file.FullName))
    }
    return (Get-FileSha256 -Path $packageSwift) + '-' + ([System.BitConverter]::ToString(
        [System.Security.Cryptography.SHA256]::Create().ComputeHash(
            [System.Text.Encoding]::UTF8.GetBytes($builder.ToString())))).Replace('-', '').ToLowerInvariant()
}

function Write-BridgeManifest {
    param([string]$Target, [string]$SourceHash, [string[]]$Files)
    $lines = @(
        'schemaVersion=1',
        "sourceHash=$SourceHash",
        "generatedAtUtc=$([DateTime]::UtcNow.ToString('o'))"
    )
    foreach ($file in ($Files | Sort-Object)) {
        $info = Get-Item -LiteralPath $file
        $lines += ("file`t{0}`t{1}`t{2}" -f $info.Name, $info.Length, (Get-FileSha256 -Path $file))
    }
    $path = Join-Path $Target $ManifestName
    [System.IO.File]::WriteAllLines($path, [string[]]$lines)

    # Names-only list so MSBuild can add the files to @(PackagingOutputs) for MSIX.
    $names = @($Files | ForEach-Object { Split-Path -Leaf $_ } | Sort-Object)
    [System.IO.File]::WriteAllLines((Join-Path $Target 'muesli-swift-bridge-files.txt'), [string[]]$names)
}

function Read-BridgeManifest {
    param([string]$Target)
    $path = Join-Path $Target $ManifestName
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $sourceHash = ''
    $files = @()
    foreach ($line in [System.IO.File]::ReadAllLines($path)) {
        if ($line.StartsWith('sourceHash=')) { $sourceHash = $line.Substring('sourceHash='.Length) }
        elseif ($line.StartsWith("file`t")) {
            $parts = $line -split "`t"
            $files += [pscustomobject]@{ name = $parts[1]; size = [int64]$parts[2] }
        }
    }
    return [pscustomobject]@{ sourceHash = $sourceHash; files = $files }
}

function Test-StagedManifest {
    param([string]$Target, $Manifest, [string]$SourceHash)
    if (-not $Manifest -or $Manifest.sourceHash -ne $SourceHash) { return $false }
    foreach ($file in $Manifest.files) {
        $path = Join-Path $Target $file.name
        if (-not (Test-Path -LiteralPath $path)) { return $false }
        if ((Get-Item -LiteralPath $path).Length -ne [int64]$file.size) { return $false }
    }
    return $true
}

function Remove-StagedBridge {
    param([string[]]$Targets, [string]$Reason)
    foreach ($target in $Targets) {
        if (-not (Test-Path -LiteralPath $target)) { continue }
        $manifest = Read-BridgeManifest -Target $target
        $names = if ($manifest -and $manifest.files.Count -gt 0) {
            @($manifest.files | ForEach-Object { $_.name })
        } else {
            @('MuesliCoreABI.dll', 'swiftCore.dll', 'swiftCRT.dll', 'Foundation.dll', 'FoundationEssentials.dll', 'FoundationInternationalization.dll', 'FoundationNetworking.dll', '_FoundationICU.dll', 'BlocksRuntime.dll', 'dispatch.dll', 'lzfse.dll', 'sqlite3.dll', 'z.dll')
        }
        foreach ($name in $names) { Remove-Item -LiteralPath (Join-Path $target $name) -Force -ErrorAction SilentlyContinue }
        Remove-Item -LiteralPath (Join-Path $target $ManifestName) -Force -ErrorAction SilentlyContinue
    }
    if ($Reason) { Write-Warning $Reason }
}

# Windows builds resolve against the Windows lock, never the macOS Package.resolved. Copying the
# consumed sources into an isolated staging package keeps the macOS lock untouched and prevents
# SwiftPM from fetching Apple-only packages or rewriting the macOS resolution.
function New-WindowsSwiftPackageStage {
    param([string]$Package, [string]$DestinationRoot)
    if (Test-Path -LiteralPath $DestinationRoot) {
        Remove-Item -LiteralPath $DestinationRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $DestinationRoot -Force | Out-Null

    Copy-Item -LiteralPath (Join-Path $Package 'Package.swift') -Destination (Join-Path $DestinationRoot 'Package.swift') -Force
    $windowsLock = Join-Path $Package 'Package.resolved.windows'
    if (-not (Test-Path -LiteralPath $windowsLock)) {
        throw "Windows dependency lock '$windowsLock' is missing. Release builds must resolve the shared core from a committed Windows lock."
    }
    Copy-Item -LiteralPath $windowsLock -Destination (Join-Path $DestinationRoot 'Package.resolved') -Force

    foreach ($relative in @(
        'Sources\CLZFSE',
        'Sources\CSQLite',
        'Sources\MuesliCore',
        'Sources\MuesliCoreABI',
        'Tests\MuesliCoreTests',
        'Tests\MuesliCoreABITests'
    )) {
        $source = Join-Path $Package $relative
        if (-not (Test-Path -LiteralPath $source)) {
            throw "Shared Swift source '$relative' is missing from '$Package'."
        }
        $destination = Join-Path $DestinationRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Recurse -Force
    }
    return $DestinationRoot
}

$outDirFull = [System.IO.Path]::GetFullPath($OutDir)
$targets = @($outDirFull)
if ($AppXDir) { $targets += [System.IO.Path]::GetFullPath($AppXDir) }

$package = Resolve-FirstPath @($SharedCorePackage, $env:MUESLI_SHARED_CORE_PACKAGE)
if (-not $package -or -not (Test-Path (Join-Path $package 'Package.swift'))) {
    $message = "Shared Swift package not found. Set MUESLI_SHARED_CORE_PACKAGE to native/MuesliNative."
    if ($Require) { throw $message }
    Remove-StagedBridge -Targets $targets -Reason "$message Quarantined stale bridge output."
    return
}

$sourceHash = Get-SwiftSourceHash -Package $package
$stagedDll = Join-Path $outDirFull 'MuesliCoreABI.dll'

# Incremental fast path: skip compilation only when the content hash matches AND every staged file is
# present at the expected size. Re-staging and verification still run for every target (including AppX).
if (-not $Force -and -not $SkipBuild -and (Test-Path -LiteralPath $stagedDll)) {
    $manifest = Read-BridgeManifest -Target $outDirFull
    if (Test-StagedManifest -Target $outDirFull -Manifest $manifest -SourceHash $sourceHash) {
        $names = @($manifest.files | ForEach-Object { $_.name })
        foreach ($target in $targets) {
            if ($target -eq $outDirFull) { continue }
            New-Item -ItemType Directory -Path $target -Force | Out-Null
            foreach ($name in $names) {
                Copy-Item -LiteralPath (Join-Path $outDirFull $name) -Destination (Join-Path $target $name) -Force
            }
            Write-BridgeManifest -Target $target -SourceHash $sourceHash -Files ($names | ForEach-Object { Join-Path $outDirFull $_ })
        }
        Write-Host "Shared Swift bridge is up to date ($($names.Count) files); re-staged and verified."
        return
    }
}

$swift = Resolve-FirstPath @($env:MUESLI_SWIFT_BIN, (Join-Path $env:LOCALAPPDATA 'Programs\Swift\Toolchains\6.4.0+Asserts\usr\bin\swift.exe'))
if (-not $swift) {
    $command = Get-Command swift -ErrorAction SilentlyContinue
    if ($command) { $swift = $command.Source }
}
if (-not $swift) {
    $message = 'swift.exe not found. Add the Swift toolchain to PATH or set MUESLI_SWIFT_BIN.'
    if ($Require) { throw $message }
    Remove-StagedBridge -Targets $targets -Reason "$message Quarantined stale bridge output."
    return
}

$scratchRoot = if ($env:MUESLI_SWIFT_BRIDGE_SCRATCH) { $env:MUESLI_SWIFT_BRIDGE_SCRATCH } else { Join-Path $env:TEMP 'muesli-swift-bridge' }
$scratch = Join-Path $scratchRoot 'scratch'
$cache = Join-Path $scratchRoot 'cache'
New-Item -ItemType Directory -Path $scratchRoot -Force | Out-Null

# Every Windows Swift invocation resolves against this isolated copy, so the macOS lock in the
# shared repository is never read or rewritten by a Windows build.
$stagingPackage = New-WindowsSwiftPackageStage -Package $package -DestinationRoot (Join-Path $scratchRoot 'package')

if ($Test) {
    # Staging contains only MuesliCoreTests and MuesliCoreABITests, so an unfiltered run executes
    # exactly the portable and ABI suites Windows consumes.
    & $swift test --package-path $stagingPackage --scratch-path $scratch --cache-path $cache
    if ($LASTEXITCODE -ne 0) { throw "Swift shared-core tests failed with exit code $LASTEXITCODE." }
    Write-Host "Swift shared-core portable and ABI tests passed from isolated package staging."
    return
}

if (-not $SkipBuild) {
    & $swift build -c $Configuration.ToLowerInvariant() --product MuesliCoreABI --scratch-path $scratch --cache-path $cache --package-path $stagingPackage
    if ($LASTEXITCODE -ne 0) { throw "swift build for MuesliCoreABI failed with exit code $LASTEXITCODE." }
}

$bridgeDll = Get-ChildItem -Path $scratch -Recurse -Filter 'MuesliCoreABI.dll' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if (-not $bridgeDll) {
    $message = 'MuesliCoreABI.dll was not produced by the Swift build.'
    if ($Require) { throw $message }
    Remove-StagedBridge -Targets $targets -Reason "$message Quarantined stale bridge output."
    return
}

$runtimeDir = Resolve-FirstPath @($env:MUESLI_SWIFT_RUNTIME_DIR, (Join-Path $env:LOCALAPPDATA 'Programs\Swift\Runtimes\6.4.0\usr\bin'))
$vcpkgCandidates = @()
foreach ($root in @($env:VCPKG_ROOT, $env:MUESLI_VCPKG_ROOT)) {
    if ($root) { $vcpkgCandidates += (Join-Path $root 'installed\x64-windows\bin') }
}
$vcpkgBin = Resolve-FirstPath $vcpkgCandidates

$searchDirs = @($runtimeDir, $vcpkgBin) | Where-Object { $_ }
$resolved = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
$toVisit = New-Object System.Collections.Generic.Queue[string]
$toVisit.Enqueue($bridgeDll.FullName)

$readobj = Resolve-FirstPath @(
    (Join-Path (Split-Path -Parent $swift) 'llvm-readobj.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Swift\Toolchains\6.4.0+Asserts\usr\bin\llvm-readobj.exe'))

function Get-ImportedModuleNames {
    param([string]$Path)
    if (-not $readobj) { return @() }
    $imports = & $readobj --coff-imports $Path 2>$null | Select-String -Pattern 'Name:\s+(\S+\.dll)' | ForEach-Object { $_.Matches[0].Groups[1].Value }
    return $imports | Sort-Object -Unique
}

while ($toVisit.Count -gt 0) {
    $current = $toVisit.Dequeue()
    if (-not $resolved.Add($current)) { continue }
    foreach ($importName in (Get-ImportedModuleNames -Path $current)) {
        foreach ($dir in $searchDirs) {
            $candidate = Join-Path $dir $importName
            if (Test-Path -LiteralPath $candidate) { $toVisit.Enqueue((Resolve-Path -LiteralPath $candidate).Path); break }
        }
    }
}

if (-not $resolved.Contains($bridgeDll.FullName)) { [void]$resolved.Add($bridgeDll.FullName) }

# Fall back to the full runtime set when the closure tool is unavailable.
if (-not $readobj -and $runtimeDir) {
    Get-ChildItem -Path $runtimeDir -Filter '*.dll' | ForEach-Object { [void]$resolved.Add($_.FullName) }
}
if ($vcpkgBin) {
    foreach ($name in @('sqlite3.dll', 'lzfse.dll', 'z.dll')) {
        $candidate = Join-Path $vcpkgBin $name
        if (Test-Path -LiteralPath $candidate) { [void]$resolved.Add((Resolve-Path -LiteralPath $candidate).Path) }
    }
}

if ($Require) {
    foreach ($required in $RequiredDlls) {
        if (-not ($resolved | Where-Object { [System.IO.Path]::GetFileName($_) -ieq $required })) {
            throw "Required native dependency '$required' was not found; the packaged application would be incomplete."
        }
    }
}

foreach ($target in $targets) {
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    foreach ($source in $resolved) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $target (Split-Path -Leaf $source)) -Force
    }
}

# Verify every staged file (present and non-empty) and write the manifest that makes staging
# self-describing and reusable by the incremental path.
foreach ($target in $targets) {
    foreach ($source in $resolved) {
        $staged = Join-Path $target (Split-Path -Leaf $source)
        if (-not (Test-Path -LiteralPath $staged) -or (Get-Item -LiteralPath $staged).Length -le 0) {
            throw "Staged dependency '$staged' is missing or empty after staging."
        }
    }
    Write-BridgeManifest -Target $target -SourceHash $sourceHash -Files $resolved
}

Write-Host "Staged shared Swift bridge ($($resolved.Count) files x $($targets.Count) layout(s)) into $($targets -join ', ')."
