<#
.SYNOPSIS
    Builds the shipping Muesli WinUI 3 app as an MSIX package.

.DESCRIPTION
    Produces an unsigned MSIX by default. Installing an unsigned or loose package requires
    Developer Mode or a sideloading policy, both of which need an elevated session, so this
    script stops at package creation and reports what is still required to install it. It never
    claims the package was installed or qualified.

    Pass -CertificateThumbprint to sign with a certificate already present in the current user's
    certificate store. Production signing is a separate, externally gated step.

.PARAMETER Configuration
    Release (default) or Debug.

.PARAMETER OutputDirectory
    Where the .msix is written. Defaults to artifacts/msix under the repository root.

.PARAMETER CertificateThumbprint
    Optional thumbprint of a code-signing certificate in Cert:\CurrentUser\My.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [string]$CertificateThumbprint
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'release-common.ps1')
$sharedCore = Assert-MuesliSharedCoreRevision -Root $repoRoot -AllowUnpinnedDevOverride
$env:MUESLI_SHARED_CORE_MODE = if ($sharedCore.BridgeRequired) { 'shared-core' } else { 'managed-fallback' }
$project = Join-Path $repoRoot 'windows-native\Muesli.Windows.WinUI\Muesli.Windows.WinUI.csproj'
if (-not (Test-Path $project)) { throw "WinUI project not found at $project" }

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts\msix' }
# MakeAppx rejects forward slashes and a missing trailing separator.
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$appxPackageDir = $OutputDirectory.TrimEnd('\') + '\'

$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

# The shell and its WPF indicator companion lock their own output assemblies while running.
Get-Process -Name 'Muesli.Windows.WinUI','Muesli.Windows.Indicator.Wpf' -ErrorAction SilentlyContinue | Stop-Process -Force

# The same deterministic publish properties the portable release used, so the MSIX payload is
# ReadyToRun, deterministic, CI-built, and carries no debug symbols.
$arguments = @(
    'build', $project,
    '-c', $Configuration,
    '-v', 'minimal',
    '-p:Platform=x64',
    '-p:GenerateAppxPackageOnBuild=true',
    '-p:Deterministic=true',
    '-p:ContinuousIntegrationBuild=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    "-p:AppxPackageDir=$appxPackageDir"
)
if ($Configuration -eq 'Release') {
    # ReadyToRun is a Release-only publish optimization; the project disables it for Debug.
    $arguments += '-p:PublishReadyToRun=true'
}
# When the shared-core lock ships the managed fallback (bridgeRequired=false), the release must not
# require the Swift bridge and must remove any stale staging rather than shipping an old bridge.
# The lock selects this mode for direct packaging and for CI/rehearsal.
if ($env:MUESLI_SHARED_CORE_MODE -eq 'managed-fallback') {
    $arguments += '-p:MuesliRequireSwiftBridge=false'
}

if ($CertificateThumbprint) {
    $arguments += "-p:PackageCertificateThumbprint=$CertificateThumbprint"
} else {
    $arguments += '-p:AppxPackageSigningEnabled=false'
}

Write-Host "Packaging Muesli ($Configuration, x64)..." -ForegroundColor Cyan
& $dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "MSIX packaging failed with exit code $LASTEXITCODE." }

# The single-project MSIX pipeline packs only @(PackagingOutputs); the loose-layout Swift bridge,
# its runtime closure and the WPF indicator companion are copied after Build and are therefore not
# in that item set. Repack deterministically so the packaged app actually ships them.
function Get-MakeAppxPath {
    $candidates = @()
    $fromPackage = Get-ChildItem (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools') -Recurse -File -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
    if ($fromPackage) { $candidates += $fromPackage.FullName }
    $fromKits = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -File -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
    if ($fromKits) { $candidates += $fromKits.FullName }
    foreach ($candidate in $candidates) { if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate } }
    return $null
}

function Test-MsixContains([string]$MsixPath, [string]$EntryPrefix) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $zip = [System.IO.Compression.ZipFile]::OpenRead($MsixPath)
    try {
        return @($zip.Entries | Where-Object { $_.FullName -like $EntryPrefix }).Count -gt 0
    } finally { $zip.Dispose() }
}

# The single-project MSIX pipeline packs the loose build output, which includes foreign-RID native
# libraries (win-arm64, etc.) from transitive NuGet packages but not the post-Build staged Swift
# bridge, the WPF companion, or the license/notice documents. Every package is therefore unpacked,
# normalized and repacked.
$foreignRidSegments = @('win-arm64', 'win-x86', 'win-arm', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64', 'android', 'ios')

function Test-ForeignRidPath {
    param([string]$RelativePath)
    $segments = $RelativePath.Replace('\', '/') -split '/'
    for ($index = 0; $index -lt $segments.Count - 1; $index++) {
        if ($segments[$index] -ieq 'runtimes') {
            $rid = $segments[$index + 1]
            if ($foreignRidSegments -contains $rid.ToLowerInvariant()) { return $true }
        }
    }
    return $false
}

function Rebuild-MsixPayload {
    param([string]$MsixPath, [string]$LooseDir, [string]$MakeAppx, [string]$RepoRoot)

    $scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('muesli-msix-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $scratch | Out-Null
    try {
        & $MakeAppx unpack /p $MsixPath /d $scratch /o | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "makeappx unpack failed with exit code $LASTEXITCODE." }

        $namesFile = Join-Path $LooseDir 'muesli-swift-bridge-files.txt'
        if (Test-Path -LiteralPath $namesFile) {
            foreach ($name in [System.IO.File]::ReadAllLines($namesFile)) {
                if ([string]::IsNullOrWhiteSpace($name)) { continue }
                $source = Join-Path $LooseDir $name
                if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $scratch $name) -Force }
            }
        }
        $companion = Join-Path $LooseDir 'Indicator'
        if (Test-Path -LiteralPath $companion) {
            Copy-Item -LiteralPath $companion -Destination (Join-Path $scratch 'Indicator') -Recurse -Force
        }

        # The public package must ship its disclosures and license texts.
        foreach ($document in @(
            @{ Source = (Join-Path $RepoRoot 'THIRD-PARTY-NOTICES.md'); Name = 'THIRD-PARTY-NOTICES.md' },
            @{ Source = (Join-Path $RepoRoot 'docs\WINDOWS_PRIVACY.md'); Name = 'WINDOWS-PRIVACY.md' }
        )) {
            if (Test-Path -LiteralPath $document.Source) {
                Copy-Item -LiteralPath $document.Source -Destination (Join-Path $scratch $document.Name) -Force
            }
        }
        $licenseSource = Join-Path $RepoRoot 'licenses'
        if (Test-Path -LiteralPath $licenseSource) {
            Copy-Item -LiteralPath $licenseSource -Destination (Join-Path $scratch 'licenses') -Recurse -Force
        }

        # Reject foreign-RID content last, after the staged bridge/companion copies, so a win-x64
        # package can never carry ARM64/x86/foreign runtimes from any source.
        $foreign = @(Get-ChildItem -LiteralPath $scratch -Recurse -File -Force | Where-Object {
            Test-ForeignRidPath -RelativePath (Get-MuesliRelativePath -BasePath $scratch -TargetPath $_.FullName)
        })
        foreach ($file in $foreign) { Remove-Item -LiteralPath $file.FullName -Force -ErrorAction SilentlyContinue }
        if ($foreign.Count -gt 0) {
            Write-Host "Removed $($foreign.Count) foreign-RID payload file(s) from the MSIX." -ForegroundColor Cyan
        }

        & $MakeAppx pack /d $scratch /p $MsixPath /o | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed with exit code $LASTEXITCODE." }
        return $true
    } finally {
        Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$makeAppx = Get-MakeAppxPath
$primaryMsix = Get-ChildItem -Path $OutputDirectory -Recurse -File -Filter 'Muesli.Windows.WinUI_*.msix' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '[\\/]Dependencies[\\/]' } |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
$looseExe = Get-ChildItem (Join-Path $repoRoot "windows-native\Muesli.Windows.WinUI\bin\x64\$Configuration") -Recurse -Filter 'Muesli.Windows.WinUI.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($primaryMsix -and $looseExe -and $makeAppx) {
    if (Rebuild-MsixPayload -MsixPath $primaryMsix.FullName -LooseDir $looseExe.DirectoryName -MakeAppx $makeAppx -RepoRoot $repoRoot) {
        Write-Host "Added the WPF companion and any pinned Swift bridge, pruned foreign-RID content, and included notices/licenses." -ForegroundColor Cyan
    }

    # Publish one stable top-level filename. Selecting the first recursive result previously
    # repacked an older artifact while leaving the package produced by this build untouched.
    [xml]$versionProperties = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
    $productVersion = [string]($versionProperties.Project.PropertyGroup.MuesliVersion | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($productVersion)) { throw 'MuesliVersion is missing from Directory.Build.props.' }
    $canonicalMsixPath = Join-Path $OutputDirectory ("Muesli.Windows.WinUI_{0}_x64.msix" -f $productVersion)
    if (-not $primaryMsix.FullName.Equals($canonicalMsixPath, [StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $primaryMsix.FullName -Destination $canonicalMsixPath -Force
        $primaryMsix = Get-Item -LiteralPath $canonicalMsixPath
    }
} elseif (-not $makeAppx) {
    throw 'MakeAppx.exe was not found; the MSIX cannot be normalized (bridge, companion, licenses, foreign-RID pruning).'
} else {
    throw 'The WinUI MSIX or loose executable was not found; the package cannot be normalized.'
}

$packages = Get-ChildItem -Path $OutputDirectory -Recurse -Include '*.msix', '*.msixbundle' -ErrorAction SilentlyContinue
if (-not $packages) { throw "The build reported success but produced no .msix under $OutputDirectory." }

foreach ($package in $packages) {
    [pscustomobject]@{
        Package  = $package.Name
        SizeMB   = [Math]::Round($package.Length / 1MB, 2)
        Path     = $package.FullName
        Signed   = [bool]$CertificateThumbprint
    }
}

if (-not $CertificateThumbprint) {
    Write-Host ''
    Write-Warning @'
The package is UNSIGNED and has not been installed or qualified by this script.
Installing it requires an elevated session to either:
  * enable Developer Mode (Settings > System > For developers), or
  * apply a sideloading policy and install a signed package whose certificate is trusted.
Signed release qualification remains a separate step.
'@
}
