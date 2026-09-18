<#
.SYNOPSIS
    Regenerates the WinUI/MSIX tile and logo assets from the Muesli app icon.

.DESCRIPTION
    The WinUI project was scaffolded with the Visual Studio template's placeholder
    images. This script replaces them with renders of the active WinUI product icon
    (windows-native/Muesli.Windows.WinUI/Assets/AppIcon.ico) so the packaged shell
    carries Muesli branding in the Start menu, taskbar, splash screen, and title bar.

    Square tiles are drawn edge to edge. Wide tile and splash screen letterbox the
    square icon on a transparent canvas, matching the manifest's
    BackgroundColor="transparent".
#>
[CmdletBinding()]
param(
    [string]$SourceIcon,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $SourceIcon) {
    $SourceIcon = Join-Path $repoRoot 'windows-native\Muesli.Windows.WinUI\Assets\AppIcon.ico'
}
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot 'windows-native\Muesli.Windows.WinUI\Assets'
}
if (-not (Test-Path $SourceIcon)) { throw "Source icon not found: $SourceIcon" }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

# name = width, height, mode (square letterboxes to the shorter edge)
$targets = @(
    @{ Name = 'StoreLogo.png';                                        W = 50;  H = 50 },
    @{ Name = 'LockScreenLogo.scale-200.png';                         W = 48;  H = 48 },
    @{ Name = 'Square44x44Logo.scale-200.png';                        W = 88;  H = 88 },
    @{ Name = 'Square44x44Logo.targetsize-24_altform-unplated.png';   W = 24;  H = 24 },
    @{ Name = 'Square44x44Logo.targetsize-48_altform-lightunplated.png'; W = 48; H = 48 },
    @{ Name = 'Square150x150Logo.scale-200.png';                      W = 300; H = 300 },
    @{ Name = 'Wide310x150Logo.scale-200.png';                        W = 620; H = 300 },
    @{ Name = 'SplashScreen.scale-200.png';                           W = 1240; H = 600 }
)

$source = [System.Drawing.Image]::FromFile((Resolve-Path $SourceIcon))
try {
    foreach ($target in $targets) {
        $bitmap = New-Object System.Drawing.Bitmap $target.W, $target.H
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.Clear([System.Drawing.Color]::Transparent)

            $edge = [Math]::Min($target.W, $target.H)
            $left = [int](($target.W - $edge) / 2)
            $top = [int](($target.H - $edge) / 2)
            $graphics.DrawImage($source, $left, $top, $edge, $edge)

            $path = Join-Path $OutputDirectory $target.Name
            $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
            [pscustomobject]@{ Asset = $target.Name; Size = "$($target.W)x$($target.H)" }
        }
        finally {
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }
}
finally {
    $source.Dispose()
}
