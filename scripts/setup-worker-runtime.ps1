param(
    [switch]$WithPostProcessing,
    [switch]$WithParakeet,
    [switch]$WithDiarization,
    [switch]$CheckOnly
)

$ErrorActionPreference = "Stop"

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$sourceProject = Join-Path $repositoryRoot "windows-native\Muesli.Windows\Muesli.Windows.csproj"
$isSourceCheckout = Test-Path $sourceProject

if (Test-Path (Join-Path $scriptDirectory "Muesli.exe")) {
    # The script is copied beside Muesli.exe in packaged builds.
    $appDir = $scriptDirectory
} elseif ($isSourceCheckout) {
    # Development builds use the repository worker and the same .venv-worker
    # location that WorkerRuntimeLocator searches at runtime.
    $appDir = $repositoryRoot
} else {
    throw "Could not identify a Muesli source checkout or packaged installation."
}

$requirements = Join-Path $appDir "worker\requirements.txt"
$postRequirements = Join-Path $appDir "worker\requirements-postprocess.txt"
$parakeetRequirements = Join-Path $appDir "worker\requirements-parakeet.txt"
$diarizationRequirements = Join-Path $appDir "worker\requirements-diarization.txt"

if (-not (Test-Path $requirements)) {
    throw "Could not find worker requirements at $requirements"
}

$bundledPython = Join-Path $appDir "python\python.exe"
$useBundled = Test-Path $bundledPython

if ($useBundled) {
    $siteTarget = Join-Path $appDir "python\site-packages-muesli"

    if ($CheckOnly) {
        & $bundledPython -c "import sys; raise SystemExit(0 if sys.version_info[:2] == (3, 12) else 1)"
        if ($LASTEXITCODE -ne 0) {
            throw "Bundled python at $bundledPython is not the expected 3.12 runtime."
        }
        Write-Host "Bundled CPython 3.12 runtime check passed at $bundledPython"
        return
    }

    New-Item -ItemType Directory -Force -Path $siteTarget | Out-Null

    if (-not (Test-Path (Join-Path $siteTarget "faster_whisper")) -and -not (Test-Path (Join-Path $siteTarget "faster_whisper-*.dist-info"))) {
        Write-Host "Installing base worker dependencies into bundled site-packages"
        & $bundledPython -m pip install --upgrade pip --no-warn-script-location
        if ($LASTEXITCODE -ne 0) { throw "pip self-upgrade failed (exit $LASTEXITCODE)." }
        & $bundledPython -m pip install --no-warn-script-location --target $siteTarget -r $requirements
        if ($LASTEXITCODE -ne 0) { throw "pip install of base worker requirements failed (exit $LASTEXITCODE)." }
    }

    if ($WithPostProcessing) {
        if (-not (Test-Path $postRequirements)) {
            throw "Could not find post-processing requirements at $postRequirements"
        }
        & $bundledPython -m pip install --no-warn-script-location --target $siteTarget -r $postRequirements
        if ($LASTEXITCODE -ne 0) { throw "pip install of Qwen post-processing requirements failed (exit $LASTEXITCODE)." }
    }

    if ($WithParakeet) {
        if (-not (Test-Path $parakeetRequirements)) {
            throw "Could not find Parakeet requirements at $parakeetRequirements"
        }
        & $bundledPython -m pip install --no-warn-script-location --target $siteTarget -r $parakeetRequirements
        if ($LASTEXITCODE -ne 0) { throw "pip install of Parakeet requirements failed (exit $LASTEXITCODE)." }
    }

    if ($WithDiarization) {
        if (-not (Test-Path $diarizationRequirements)) {
            throw "Could not find diarization requirements at $diarizationRequirements"
        }
        & $bundledPython -m pip install --no-warn-script-location --target $siteTarget -r $diarizationRequirements
        if ($LASTEXITCODE -ne 0) { throw "pip install of diarization requirements failed (exit $LASTEXITCODE)." }
    }

    Write-Host "Muesli worker runtime is ready (bundled CPython) at $bundledPython"
    return
}

$venv = if ($isSourceCheckout) {
    Join-Path $repositoryRoot ".venv-worker"
} else {
    Join-Path $appDir ".venv"
}
$python = Join-Path $venv "Scripts\python.exe"

function Find-PythonLauncher {
    $candidates = @(
        @{ File = "py"; Args = @("-3.12") },
        @{ File = "py"; Args = @("-3.11") },
        @{ File = "python"; Args = @() },
        @{ File = "python3"; Args = @() }
    )

    foreach ($candidate in $candidates) {
        try {
            if (-not (Get-Command $candidate.File -ErrorAction SilentlyContinue)) {
                continue
            }
            $versionArgs = @()
            $versionArgs += $candidate.Args
            $versionArgs += @("-c", "import sys; raise SystemExit(0 if sys.version_info[:2] in [(3, 11), (3, 12)] else 1)")
            & $candidate.File @versionArgs | Out-Null
            if ($LASTEXITCODE -eq 0) {
                return $candidate
            }
        } catch {
        }
    }

    throw "Supported Python was not found. Install Python 3.11 or 3.12, then run setup-worker-runtime.ps1 again. Python 3.13+ is not used for this v1 package."
}

if (-not (Test-Path $python)) {
    if ($CheckOnly) {
        $launcher = Find-PythonLauncher
        Write-Host "Supported Python launcher found: $($launcher.File) $($launcher.Args -join ' ')"
        return
    }

    $launcher = Find-PythonLauncher
    $venvArgs = @()
    $venvArgs += $launcher.Args
    $venvArgs += @("-m", "venv", $venv)
    & $launcher.File @venvArgs
}

if ($CheckOnly) {
    & $python -c "import sys; raise SystemExit(0 if sys.version_info[:2] in [(3, 11), (3, 12)] else 1)"
    if ($LASTEXITCODE -ne 0) {
        throw "Existing worker runtime uses an unsupported Python version. Delete .venv and rerun setup with Python 3.11 or 3.12."
    }

    Write-Host "Muesli worker runtime check passed at $python"
    return
}

& $python -m pip install --upgrade pip
& $python -m pip install -r $requirements

if ($WithPostProcessing) {
    if (-not (Test-Path $postRequirements)) {
        throw "Could not find post-processing requirements at $postRequirements"
    }
    & $python -m pip install -r $postRequirements
}

if ($WithParakeet) {
    if (-not (Test-Path $parakeetRequirements)) {
        throw "Could not find Parakeet requirements at $parakeetRequirements"
    }
    & $python -m pip install -r $parakeetRequirements
}

if ($WithDiarization) {
    if (-not (Test-Path $diarizationRequirements)) {
        throw "Could not find diarization requirements at $diarizationRequirements"
    }
    & $python -m pip install -r $diarizationRequirements
}

Write-Host "Muesli worker runtime is ready at $python"
