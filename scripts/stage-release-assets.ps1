# Stages the files a GitHub release of the unsigned developer build carries, and refuses to
# stage a package that must not ship on that channel.
#
# Output (default artifacts\release):
#   Muesli-Windows-<version>-x64-unsigned.msix   the package built by rehearse-windows-release.ps1
#   Install-MuesliDeveloperBuild.ps1             scripts\install-developer-build.ps1
#   SHA256SUMS.txt                               sha256sum-compatible checksums of the two above
#   RELEASE-NOTES.md                             docs\release-notes\<version>.md plus install steps
#
# Used by .github/workflows/windows-release.yml. It builds nothing and publishes nothing.

param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$MsixPath = "",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "read-release-properties.ps1")
. (Join-Path $PSScriptRoot "release-common.ps1")

$release = Get-MuesliReleaseProperties -Root $root
if ($Version -ne $release.Version) {
    throw "Requested version '$Version' does not match MuesliVersion '$($release.Version)' in Directory.Build.props."
}
if ([string]::IsNullOrWhiteSpace($MsixPath)) {
    $MsixPath = Join-Path $root "artifacts\msix\Muesli.Windows.WinUI_${Version}_x64.msix"
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root "artifacts\release"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

$notesPath = Get-MuesliReleaseNotesPath -Root $root -Version $Version
Assert-MuesliReleaseNotes -Path $notesPath

$identity = Assert-MuesliUnsignedReleasePackage -MsixPath $MsixPath -Version $Version

$names = Get-MuesliReleaseAssetNames -Version $Version

# Only ever clear files this script staged. A caller-chosen folder holding anything else (the
# repository, the folder the package is in) is refused rather than emptied.
$MsixPath = [IO.Path]::GetFullPath($MsixPath)
$outputPrefix = $OutputDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if ($MsixPath.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The package '$MsixPath' is inside the output directory '$OutputDirectory'. Choose a separate output directory."
}
if (Test-Path -LiteralPath $OutputDirectory -PathType Leaf) { throw "Output directory '$OutputDirectory' is a file." }
if (Test-Path -LiteralPath $OutputDirectory) {
    $stagedPatterns = @('Muesli-Windows-*-x64-unsigned.msix', $names.Installer, $names.Checksums, $names.Notes)
    foreach ($item in @(Get-ChildItem -LiteralPath $OutputDirectory -Force)) {
        $isStaged = -not $item.PSIsContainer -and @($stagedPatterns | Where-Object { $item.Name -like $_ }).Count -gt 0
        if (-not $isStaged) {
            throw "Output directory '$OutputDirectory' contains '$($item.Name)', which this script did not stage. Choose an empty or dedicated directory; nothing was deleted."
        }
    }
    Get-ChildItem -LiteralPath $OutputDirectory -Force | Remove-Item -Force
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$stagedMsix = Join-Path $OutputDirectory $names.Msix
$stagedInstaller = Join-Path $OutputDirectory $names.Installer
Copy-Item -LiteralPath $MsixPath -Destination $stagedMsix
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "install-developer-build.ps1") -Destination $stagedInstaller

$checksumsPath = Join-Path $OutputDirectory $names.Checksums
$lines = foreach ($file in @($stagedMsix, $stagedInstaller)) {
    "{0}  {1}" -f (Get-Sha256HexFromFile -Path $file), ([IO.Path]::GetFileName($file))
}
Write-Utf8NoBomFile -Path $checksumsPath -Content (($lines -join "`n") + "`n")
Assert-MuesliReleaseChecksums -Directory $OutputDirectory -ChecksumsFileName $names.Checksums | Out-Null

$notes = ([IO.File]::ReadAllText($notesPath)).Replace("`r`n", "`n").TrimEnd()
$install = @"

---

### Install (unsigned developer build)

This build is **not code-signed**, so Windows will not install it by double-clicking the ``.msix``.
It is meant for developers and testers. A signed build for everyone is planned through the Microsoft Store.

1. Turn on **Settings > System > For developers > Developer Mode**.
2. Download ``$($names.Msix)``, ``$($names.Installer)`` and ``$($names.Checksums)`` into one folder.
3. In PowerShell, in that folder, run:

   ``````powershell
   powershell -ExecutionPolicy Bypass -File .\$($names.Installer)
   ``````

The script checks the download against ``$($names.Checksums)``, unpacks the package into
``%LOCALAPPDATA%\MuesliDeveloperBuild`` and registers it for your user. It needs no administrator rights.
Your history and settings stay in ``%APPDATA%\muesli``. Speech models are not included; you download them inside the app.

To remove it: ``Get-AppxPackage Muesli.Windows | Remove-AppxPackage``, then delete ``%LOCALAPPDATA%\MuesliDeveloperBuild``.

Requires Windows 10 22H2 or Windows 11, x64. Usage analytics are off in this build.

### SHA-256

``````
$($lines -join "`n")
``````
"@
Write-Utf8NoBomFile -Path (Join-Path $OutputDirectory $names.Notes) -Content ($notes + "`n" + $install.Replace("`r`n", "`n") + "`n")

Write-Host "Staged release assets for $($identity.Name) $($identity.Version) in $OutputDirectory"
Get-ChildItem -LiteralPath $OutputDirectory | ForEach-Object { Write-Host ("  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)) }
