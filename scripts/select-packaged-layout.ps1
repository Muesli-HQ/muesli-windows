<#
.SYNOPSIS
    Selects the packaged WinUI layout that the current build produced.

.DESCRIPTION
    The build output can contain more than one AppxManifest.xml. Alongside the real layout root
    there are historical nested copies such as ...\win-x64\AppX\AppX\ that MSBuild no longer
    refreshes. Selecting by "the directory is called AppX" or by manifest timestamp alone can
    therefore launch a stale Muesli.Windows.WinUI.dll after a successful build, which silently
    runs old code.

    This selector instead:
      * accepts a layout under any directory name, not just "AppX";
      * requires a sibling Muesli.Windows.WinUI.dll next to the manifest;
      * rejects recursively nested layouts (…\AppX\AppX, …\AppX\AppX\AppX);
      * ranks by the payload DLL's write time, not the manifest's;
      * optionally fails when the winning DLL is older than the build that just ran.

.PARAMETER OutputRoot
    The configuration output root to search, e.g. ...\bin\x64\Debug.

.PARAMETER MinimumDllWriteTimeUtc
    When supplied, the selected layout's DLL must be at least this recent. Used to prove the
    layout belongs to the build that just completed rather than an earlier one.

.OUTPUTS
    The full path of the selected AppxManifest.xml.
#>
function Select-MuesliPackagedLayout {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$OutputRoot,
        [datetime]$MinimumDllWriteTimeUtc
    )

    if (-not (Test-Path -LiteralPath $OutputRoot)) {
        throw "Packaged WinUI layout not found: output root '$OutputRoot' does not exist. Run without -SkipBuild first."
    }

    $payloadName = 'Muesli.Windows.WinUI.dll'
    $candidates = @(
        Get-ChildItem -LiteralPath $OutputRoot -Filter 'AppxManifest.xml' -File -Recurse -ErrorAction SilentlyContinue |
            ForEach-Object {
                $dll = Join-Path $_.Directory.FullName $payloadName
                if (-not (Test-Path -LiteralPath $dll)) { return }

                # Reject recursively nested layouts: a directory whose ancestry repeats a layout
                # folder name is a stale copy MSBuild stopped updating.
                $relative = $_.Directory.FullName.Substring($OutputRoot.Length).Trim('\')
                $segments = @($relative -split '\\' | Where-Object { $_ -ne '' })
                $duplicated = ($segments | Group-Object | Where-Object { $_.Count -gt 1 })
                if ($duplicated) { return }

                [pscustomobject]@{
                    Manifest       = $_.FullName
                    Dll            = $dll
                    DllWriteUtc    = (Get-Item -LiteralPath $dll).LastWriteTimeUtc
                    Depth          = $segments.Count
                }
            }
    )

    if ($candidates.Count -eq 0) {
        throw "Packaged WinUI layout not found under $OutputRoot. Run without -SkipBuild first."
    }

    # Newest payload wins; a shallower layout breaks ties so the layout root beats any copy of it.
    $selected = $candidates | Sort-Object -Property @{Expression = 'DllWriteUtc'; Descending = $true},
                                                    @{Expression = 'Depth'; Descending = $false} |
        Select-Object -First 1

    if ($PSBoundParameters.ContainsKey('MinimumDllWriteTimeUtc')) {
        # One second of slack: NTFS and MSBuild do not agree to the tick.
        if ($selected.DllWriteUtc -lt $MinimumDllWriteTimeUtc.AddSeconds(-1)) {
            throw ("Refusing to launch a stale packaged layout. '$($selected.Dll)' was written " +
                   "$($selected.DllWriteUtc.ToString('u')) but the current build produced output at " +
                   "$($MinimumDllWriteTimeUtc.ToString('u')). Delete the stale layout or rebuild.")
        }
    }

    $selected.Manifest
}
