<#
.SYNOPSIS
    Regenerates the complete WinUI/MSIX icon asset family from the canonical Muesli icon.

.DESCRIPTION
    Windows selects target-size assets for Start, taskbar, Alt+Tab, task view, and shell surfaces.
    A partial target-size family makes Windows shrink the icon onto a system-colored backplate.
    This script uses WinApp CLI's manifest-aware generator to produce the complete default, dark,
    and light asset families from the canonical 1024px Muesli artwork.
#>
[CmdletBinding()]
param(
    [string]$SourceIcon,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing

# Bar heights measured from the blue waveform inside the canonical 1024px app icon
# (Branding\MuesliAppIcon.png), normalized to the tallest bar. The small app-list frames resample
# this envelope instead of scaling the raster, because bicubic downscaling merges 13 thin bars into
# a blob below ~36px. Keeping the master's own profile is what makes the taskbar mark match.
$script:MasterBarProfile = @(
    0.302, 0.579, 0.852, 1.000, 0.899, 0.602, 0.302,
    0.602, 0.899, 1.000, 0.852, 0.579, 0.302
)

function Get-ResampledBarHeights {
    param(
        [Parameter(Mandatory)][double[]]$Profile,
        [Parameter(Mandatory)][int]$Count
    )

    if ($Count -ge $Profile.Count) {
        return $Profile
    }
    if ($Count -le 1) {
        return @($Profile[[int][Math]::Floor($Profile.Count / 2.0)])
    }

    $result = [System.Collections.Generic.List[double]]::new()
    for ($index = 0; $index -lt $Count; $index++) {
        $position = $index * ($Profile.Count - 1) / ($Count - 1)
        $lower = [int][Math]::Floor($position)
        $upper = [Math]::Min($lower + 1, $Profile.Count - 1)
        $weight = $position - $lower
        $result.Add(($Profile[$lower] * (1 - $weight)) + ($Profile[$upper] * $weight))
    }
    return $result.ToArray()
}

function New-RoundedBarPath {
    param(
        [Parameter(Mandatory)][single]$X,
        [Parameter(Mandatory)][single]$Y,
        [Parameter(Mandatory)][single]$Width,
        [Parameter(Mandatory)][single]$Height
    )

    # The brand mark's bars are capsules, not rectangles. Round the ends to match the macOS
    # design-system mark, but fall back to a plain rectangle when the bar is too small for the
    # arc to be visible so the pixel-snapped small sizes stay crisp.
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $radius = [single]([Math]::Min($Width, $Height) / 2.0)
    if ($Width -lt 2 -or $Height -lt 4) {
        $path.AddRectangle((New-Object System.Drawing.RectangleF($X, $Y, $Width, $Height)))
        return $path
    }

    $diameter = $radius * 2
    $right = $X + $Width - $diameter
    $bottom = $Y + $Height - $diameter
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($right, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($right, $bottom, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $bottom, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-OpticallySizedAppListBitmap {
    param(
        [Parameter(Mandatory)][int]$Size
    )

    # Pixel-snap the master mark: use as many of its bars as the frame can hold with one-pixel gaps
    # (7 at 16px up to the full 13 at 30-32px, where 13 one-pixel bars fit exactly), resampling the
    # master's own envelope. This is what makes the taskbar/Start mark read like the 1024px artwork
    # instead of a differently-proportioned approximation. Caller owns the bitmap.
    $barCount = if ($Size -lt 18) { 7 } elseif ($Size -lt 22) { 9 } elseif ($Size -lt 28) { 11 } else { 13 }
    $barHeights = Get-ResampledBarHeights -Profile $script:MasterBarProfile -Count $barCount

    $bitmap = New-Object System.Drawing.Bitmap $Size, $Size
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $backgroundPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

        $margin = [Math]::Max(0.5, $Size / 64.0)
        $diameter = $Size * 0.43
        $right = $Size - (2 * $margin)
        $backgroundPath.AddArc($margin, $margin, $diameter, $diameter, 180, 90)
        $backgroundPath.AddArc($right - $diameter + $margin, $margin, $diameter, $diameter, 270, 90)
        $backgroundPath.AddArc($right - $diameter + $margin, $right - $diameter + $margin, $diameter, $diameter, 0, 90)
        $backgroundPath.AddArc($margin, $right - $diameter + $margin, $diameter, $diameter, 90, 90)
        $backgroundPath.CloseFigure()

        $background = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 22, 23, 25))
        try {
            $graphics.FillPath($background, $backgroundPath)
        }
        finally {
            $background.Dispose()
        }

        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        # One-pixel bars with one-pixel gaps: the master's mark is ~635/1024 of the icon wide, so at
        # these sizes even 13 bars are only about a pixel thick. Two-pixel bars would force a lower
        # bar count and drift away from the artwork.
        $barWidth = 1
        $gap = 1
        $markWidth = ($barHeights.Count * $barWidth) + (($barHeights.Count - 1) * $gap)
        $left = [int][Math]::Floor(($Size - $markWidth) / 2.0)
        $maximumHeight = [int][Math]::Round($Size * 0.58)
        $blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 107, 163, 247))
        try {
            for ($index = 0; $index -lt $barHeights.Count; $index++) {
                $x = $left + ($index * ($barWidth + $gap))
                $height = [Math]::Max(2, [int][Math]::Round($maximumHeight * $barHeights[$index]))
                $top = [int][Math]::Floor(($Size - $height) / 2.0)
                $path = New-RoundedBarPath -X $x -Y $top -Width $barWidth -Height $height
                try {
                    $graphics.FillPath($blue, $path)
                }
                finally {
                    $path.Dispose()
                }
            }
        }
        finally {
            $blue.Dispose()
        }

        return $bitmap
    }
    catch {
        $bitmap.Dispose()
        throw
    }
    finally {
        $backgroundPath.Dispose()
        $graphics.Dispose()
    }
}

function Write-OpticallySizedAppListIcon {
    param(
        [Parameter(Mandatory)][int]$Size,
        [Parameter(Mandatory)][string]$Directory
    )

    $bitmap = New-OpticallySizedAppListBitmap -Size $Size
    try {
        $names = @(
            "Square44x44Logo.targetsize-$Size.png",
            "Square44x44Logo.targetsize-$($Size)_altform-unplated.png",
            "Square44x44Logo.targetsize-$($Size)_altform-lightunplated.png"
        )
        foreach ($name in $names) {
            $bitmap.Save((Join-Path $Directory $name), [System.Drawing.Imaging.ImageFormat]::Png)
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

function Write-IconFile {
    param(
        [Parameter(Mandatory)][System.Collections.IEnumerable]$Frames,
        [Parameter(Mandatory)][string]$DestinationPath
    )

    # Windows Vista and later accept PNG-compressed frames inside an .ico, and the tray
    # (System.Drawing.Icon) and AppWindow.SetIcon both load them. This keeps the builder
    # dependency-free while still emitting a real multi-resolution shell icon.
    $encoded = [System.Collections.Generic.List[object]]::new()
    foreach ($frame in $Frames) {
        $stream = New-Object System.IO.MemoryStream
        try {
            $frame.Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $encoded.Add([pscustomobject]@{ Size = [int]$frame.Size; Bytes = $stream.ToArray() })
        }
        finally {
            $stream.Dispose()
            $frame.Bitmap.Dispose()
        }
    }

    $file = [System.IO.File]::Create($DestinationPath)
    $writer = New-Object System.IO.BinaryWriter($file)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$encoded.Count)

        $offset = 6 + (16 * $encoded.Count)
        foreach ($frame in $encoded) {
            $dimension = if ($frame.Size -ge 256) { [byte]0 } else { [byte]$frame.Size }
            $writer.Write($dimension)
            $writer.Write($dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frame.Bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $frame.Bytes.Length
        }

        foreach ($frame in $encoded) {
            $writer.Write($frame.Bytes)
        }
    }
    finally {
        $writer.Dispose()
        $file.Dispose()
    }
}

function Write-ProductAppIcon {
    param(
        [Parameter(Mandatory)][string]$MasterPath,
        [Parameter(Mandatory)][string]$DestinationPath
    )

    # AppWindow.SetIcon and the tray bind Assets\AppIcon.ico directly, so its frames matter more
    # than the package tile assets. Bicubic-downscale from the 1024px master for 48px and above;
    # below 36px the master collapses into two diamonds, so reuse the pixel-snapped drawing.
    $frames = [System.Collections.Generic.List[object]]::new()
    $master = [System.Drawing.Bitmap]::FromFile($MasterPath)
    try {
        foreach ($size in 16, 20, 24, 30, 32) {
            $frames.Add([pscustomobject]@{ Size = $size; Bitmap = (New-OpticallySizedAppListBitmap -Size $size) })
        }

        foreach ($size in 48, 64, 256) {
            $bitmap = New-Object System.Drawing.Bitmap $size, $size
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.DrawImage($master, 0, 0, $size, $size)
            }
            finally {
                $graphics.Dispose()
            }

            $frames.Add([pscustomobject]@{ Size = $size; Bitmap = $bitmap })
        }

        Write-IconFile -Frames $frames -DestinationPath $DestinationPath
    }
    finally {
        $master.Dispose()
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectDirectory = Join-Path $repoRoot 'windows-native\Muesli.Windows.WinUI'
$manifestPath = Join-Path $projectDirectory 'Package.appxmanifest'
$defaultOutputDirectory = Join-Path $projectDirectory 'Assets'

if (-not $SourceIcon) {
    $SourceIcon = Join-Path $projectDirectory 'Branding\MuesliAppIcon.png'
}
if (-not $OutputDirectory) {
    $OutputDirectory = $defaultOutputDirectory
}

$sourceIconPath = (Resolve-Path -LiteralPath $SourceIcon).Path
$outputDirectoryPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$defaultOutputDirectoryPath = [System.IO.Path]::GetFullPath($defaultOutputDirectory)
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "WinUI package manifest not found: $manifestPath"
}

$winApp = Get-Command winapp -ErrorAction SilentlyContinue
if (-not $winApp) {
    throw 'WinApp CLI 0.6+ is required. Install/repair the WinUI development toolchain first.'
}

$workingDirectory = $projectDirectory
$temporaryDirectory = $null
if (-not $outputDirectoryPath.Equals($defaultOutputDirectoryPath, [StringComparison]::OrdinalIgnoreCase)) {
    $temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("muesli-winui-assets-{0}" -f [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $temporaryDirectory 'Package.appxmanifest')
    $workingDirectory = $temporaryDirectory
}

try {
    $workingManifest = Join-Path $workingDirectory 'Package.appxmanifest'
    & $winApp.Source manifest update-assets $sourceIconPath `
        --manifest $workingManifest `
        --light-image $sourceIconPath
    if ($LASTEXITCODE -ne 0) {
        throw "WinApp CLI asset generation failed with exit code $LASTEXITCODE."
    }

    $generatedDirectory = Join-Path $workingDirectory 'Assets'
    if (-not (Test-Path -LiteralPath $generatedDirectory)) {
        throw "WinApp CLI did not create the expected asset directory: $generatedDirectory"
    }

    if ($temporaryDirectory) {
        New-Item -ItemType Directory -Force -Path $outputDirectoryPath | Out-Null
        Copy-Item -Path (Join-Path $generatedDirectory '*') -Destination $outputDirectoryPath -Force
    }

    foreach ($smallSize in 16, 20, 24, 30, 32) {
        Write-OpticallySizedAppListIcon -Size $smallSize -Directory $outputDirectoryPath
    }

    # Scale-qualified tile/splash files are kept at the project's established 200% baseline.
    # Retaining every generated scale/colorful variant makes MSBuild split resource packs and
    # surfaces unrelated neutral-resource warnings. Target-size app-list assets are different:
    # Windows requires their complete default/dark/light family to avoid the fallback backplate.
    $rootAssetsToKeep = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @(
        'LockScreenLogo.scale-200.png',
        'MuesliGlyph.png',
        'SplashScreen.scale-200.png',
        'Square150x150Logo.scale-200.png',
        'Square44x44Logo.scale-200.png',
        'StoreLogo.png',
        'Wide310x150Logo.scale-200.png'
    )) {
        [void]$rootAssetsToKeep.Add($name)
    }
    foreach ($targetVariant in Get-ChildItem -LiteralPath $outputDirectoryPath -Filter 'Square44x44Logo.targetsize-*.png' -File) {
        [void]$rootAssetsToKeep.Add($targetVariant.Name)
    }
    foreach ($generatedPng in Get-ChildItem -LiteralPath $outputDirectoryPath -Filter '*.png' -File) {
        if (-not $rootAssetsToKeep.Contains($generatedPng.Name)) {
            Remove-Item -LiteralPath $generatedPng.FullName -Force
        }
    }

    # WinApp's app.ico downsamples a single raster, so its sub-36px frames turn the waveform
    # into an unreadable "8". Rebuild the product icon from the master with the same
    # optically-sized small frames the app-list assets use; the tray and taskbar bind this file.
    $productAppIcon = Join-Path $outputDirectoryPath 'AppIcon.ico'
    Write-ProductAppIcon -MasterPath $sourceIconPath -DestinationPath $productAppIcon

    $targetVariants = @(Get-ChildItem -LiteralPath $outputDirectoryPath -Filter 'Square44x44Logo.targetsize-*.png' -File)
    if ($targetVariants.Count -ne 42) {
        throw "Expected 42 Start/taskbar target-size variants, found $($targetVariants.Count)."
    }

    [pscustomobject]@{
        Source = $sourceIconPath
        OutputDirectory = $outputDirectoryPath
        TargetSizeVariants = $targetVariants.Count
        AppIcon = $productAppIcon
    }
}
finally {
    if ($temporaryDirectory -and (Test-Path -LiteralPath $temporaryDirectory)) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
