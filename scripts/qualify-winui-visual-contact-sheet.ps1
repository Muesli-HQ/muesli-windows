[CmdletBinding()]
param(
    [string]$ReferenceDirectory = (Join-Path $PSScriptRoot '..\docs\ui-reference\macos-current-2026-08-27'),
    [string]$CaptureDirectory = (Join-Path $PSScriptRoot '..\artifacts\ui-automation'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\ui-parity-final')
)

$ErrorActionPreference = 'Stop'
$referenceRoot = (Resolve-Path -LiteralPath $ReferenceDirectory).Path
$captureRoot = if (Test-Path -LiteralPath $CaptureDirectory) { (Resolve-Path -LiteralPath $CaptureDirectory).Path } else { $null }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null

Add-Type -AssemblyName System.Drawing
$files = @()
if ($referenceRoot) {
    $files += Get-ChildItem -LiteralPath $referenceRoot -Filter '*.png' -File | Sort-Object Name
}
if ($captureRoot) {
    $files += Get-ChildItem -LiteralPath $captureRoot -Filter '*.png' -File |
        Where-Object { $_.Name -like 'winui-populated-*' -or $_.Name -like 'winui-secondary-*' } |
        Sort-Object Name
}
if ($files.Count -eq 0) { throw 'No reference or WinUI capture PNGs were found.' }

$columns = 4
$tileWidth = 320
$tileHeight = 220
$labelHeight = 28
$rows = [Math]::Ceiling($files.Count / $columns)
$sheet = [Drawing.Bitmap]::new($columns * $tileWidth, $rows * ($tileHeight + $labelHeight))
$graphics = [Drawing.Graphics]::FromImage($sheet)
$graphics.Clear([Drawing.Color]::FromArgb(24, 25, 28))
$font = [Drawing.Font]::new('Segoe UI', 9)
$brush = [Drawing.SolidBrush]::new([Drawing.Color]::White)
try {
    for ($index = 0; $index -lt $files.Count; $index++) {
        $file = $files[$index]
        $x = ($index % $columns) * $tileWidth
        $y = [Math]::Floor($index / $columns) * ($tileHeight + $labelHeight)
        $image = [Drawing.Image]::FromFile($file.FullName)
        try {
            $scale = [Math]::Min(($tileWidth - 16) / $image.Width, ($tileHeight - 12) / $image.Height)
            $width = [Math]::Max(1, [int]($image.Width * $scale))
            $height = [Math]::Max(1, [int]($image.Height * $scale))
            $graphics.DrawImage($image, $x + (($tileWidth - $width) / 2), $y + (($tileHeight - $height) / 2), $width, $height)
        }
        finally { $image.Dispose() }
        $graphics.DrawString($file.BaseName, $font, $brush, $x + 8, $y + $tileHeight + 5)
    }
    $path = Join-Path $outputRoot 'winui-macos-contact-sheet.png'
    $sheet.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    Write-Output $path
}
finally {
    $brush.Dispose(); $font.Dispose(); $graphics.Dispose(); $sheet.Dispose()
}
