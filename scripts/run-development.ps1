param(
    [switch]$NoBuild,
    [switch]$Background
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $repositoryRoot "windows-native\Muesli.Windows\Muesli.Windows.csproj"
$executable = Join-Path $repositoryRoot "windows-native\Muesli.Windows\bin\Debug\net8.0-windows\Muesli.exe"

$running = @(Get-Process Muesli -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    $descriptions = $running | ForEach-Object {
        try { "PID $($_.Id): $($_.Path)" } catch { "PID $($_.Id): path unavailable" }
    }
    throw "Muesli is already running. Quit it from the tray before starting a development build.`n$($descriptions -join "`n")"
}

if (-not $NoBuild) {
    & dotnet build $project --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "The development build failed (exit $LASTEXITCODE)."
    }
}

if (-not (Test-Path $executable)) {
    throw "The development executable was not found at $executable"
}

$startParameters = @{
    FilePath = $executable
    WorkingDirectory = Split-Path $executable
}

if ($Background) {
    $startParameters.ArgumentList = @("--background")
}

Start-Process @startParameters
Write-Host "Started Muesli Development v0.3.0 from $executable"
