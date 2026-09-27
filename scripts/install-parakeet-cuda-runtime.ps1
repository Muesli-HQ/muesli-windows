<#
.SYNOPSIS
    Optional local helper that stages NVIDIA CUDA 12 / cuDNN 9 DLLs for a
    version-matched sherpa-onnx CUDA provider.

.DESCRIPTION
    This script is NOT part of the public Wave 0 package. The ZIP and installer
    ship the CPU Sherpa provider only. Do not describe a public build as
    CUDA-capable because this script exists.

    CUDA packaging, provenance, and NVIDIA hardware qualification are module L11.
    Until L11 completes, this helper is optional external staging: it copies
    selected CUDA Toolkit DLLs from a user install and may download pinned cuDNN
    bits. It does not add CUDA files to the public package.
#>
param(
    [string]$CudaPath = $env:CUDA_PATH,
    [string]$DependencyDirectory = "",
    [string]$DownloadCache = ""
)

$ErrorActionPreference = "Stop"

$cudaFiles = @(
    "cublasLt64_12.dll",
    "cublas64_12.dll",
    "cufft64_11.dll",
    "cudart64_12.dll"
)
$cudnnFiles = @(
    "cudnn64_9.dll",
    "cudnn_graph64_9.dll",
    "cudnn_engines_runtime_compiled64_9.dll",
    "cudnn_engines_precompiled64_9.dll",
    "cudnn_heuristic64_9.dll",
    "cudnn_ops64_9.dll",
    "cudnn_adv64_9.dll",
    "cudnn_cnn64_9.dll"
)
$cudnnVersion = "9.10.2.21"
$cudnnArchiveName = "cudnn-windows-x86_64-$($cudnnVersion)_cuda12-archive.zip"
$cudnnUrl = "https://developer.download.nvidia.com/compute/cudnn/redist/cudnn/windows-x86_64/$cudnnArchiveName"
$cudnnSha256 = "C1A4567D822EBDA7373FA1F19255DFF4942302DE741F830160B6C7D1FB31AF23"

if ([string]::IsNullOrWhiteSpace($DependencyDirectory)) {
    $DependencyDirectory = Join-Path $env:LOCALAPPDATA "muesli\native-sherpa-cuda-dependencies\cuda12-cudnn9"
}
$DependencyDirectory = [System.IO.Path]::GetFullPath($DependencyDirectory)

if ([string]::IsNullOrWhiteSpace($DownloadCache)) {
    $DownloadCache = Join-Path $env:LOCALAPPDATA "muesli\downloads\sherpa-onnx-cuda"
}
$DownloadCache = [System.IO.Path]::GetFullPath($DownloadCache)

if ([string]::IsNullOrWhiteSpace($CudaPath) -or -not (Test-Path -LiteralPath $CudaPath)) {
    $cudaRoot = "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA"
    $CudaPath = Get-ChildItem -LiteralPath $cudaRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

if ([string]::IsNullOrWhiteSpace($CudaPath) -or -not (Test-Path -LiteralPath $CudaPath)) {
    throw "CUDA Toolkit 12.x was not found. Install CUDA Toolkit 12.x, then rerun this script or pass -CudaPath."
}

$cudaBin = Join-Path $CudaPath "bin"
$missingCudaFiles = @($cudaFiles | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $cudaBin $_))
})
if ($missingCudaFiles.Count -gt 0) {
    throw "CUDA Toolkit at '$CudaPath' is incomplete. Missing: $($missingCudaFiles -join ', ')"
}

New-Item -ItemType Directory -Force -Path $DependencyDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $DownloadCache | Out-Null

foreach ($file in $cudaFiles) {
    Copy-Item -LiteralPath (Join-Path $cudaBin $file) -Destination (Join-Path $DependencyDirectory $file) -Force
}

$missingCudnnFiles = @($cudnnFiles | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $DependencyDirectory $_))
})
if ($missingCudnnFiles.Count -gt 0) {
    $archivePath = Join-Path $DownloadCache $cudnnArchiveName
    if (-not (Test-Path -LiteralPath $archivePath)) {
        Write-Host "Downloading official NVIDIA cuDNN $cudnnVersion for CUDA 12..."
        $downloadPath = "$archivePath.$([Guid]::NewGuid().ToString('N')).download"
        try {
            Invoke-WebRequest -Uri $cudnnUrl -OutFile $downloadPath -TimeoutSec 1800
            $downloadHash = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash
            if (-not $downloadHash.Equals($cudnnSha256, [StringComparison]::OrdinalIgnoreCase)) {
                throw "cuDNN archive integrity check failed. Expected SHA-256 $cudnnSha256 but found $downloadHash."
            }
            Move-Item -LiteralPath $downloadPath -Destination $archivePath -Force
        } finally {
            Remove-Item -LiteralPath $downloadPath -Force -ErrorAction SilentlyContinue
        }
    }

    $actualCudnnSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    if (-not $actualCudnnSha256.Equals($cudnnSha256, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
        throw "cuDNN archive integrity check failed. Expected SHA-256 $cudnnSha256 but found $actualCudnnSha256."
    }

    $extractDirectory = Join-Path $DownloadCache "cudnn-$cudnnVersion"
    if (Test-Path -LiteralPath $extractDirectory) {
        Remove-Item -LiteralPath $extractDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $extractDirectory | Out-Null
    $extractRoot = [System.IO.Path]::GetFullPath($extractDirectory).TrimEnd('\') + '\'
    $zip = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        foreach ($entry in $zip.Entries) {
            $entryPath = $entry.FullName.Replace('/', '\')
            $destination = [System.IO.Path]::GetFullPath((Join-Path $extractDirectory $entryPath))
            if (-not $destination.StartsWith($extractRoot, [StringComparison]::OrdinalIgnoreCase)) {
                throw "cuDNN archive entry escapes the verified extraction directory: $($entry.FullName)"
            }

            if ([string]::IsNullOrEmpty($entry.Name)) {
                New-Item -ItemType Directory -Force -Path $destination | Out-Null
                continue
            }

            $destinationDirectory = Split-Path -Parent $destination
            New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
            $input = $entry.Open()
            $output = [System.IO.File]::Open($destination, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
            try {
                $input.CopyTo($output)
                $output.Flush($true)
            } finally {
                $output.Dispose()
                $input.Dispose()
            }
        }
    } finally {
        $zip.Dispose()
    }

    foreach ($file in $cudnnFiles) {
        $source = Get-ChildItem -LiteralPath $extractDirectory -Recurse -File -Filter $file |
            Select-Object -First 1
        if (-not $source) {
            throw "The official cuDNN archive did not contain required file '$file'."
        }

        Copy-Item -LiteralPath $source.FullName -Destination (Join-Path $DependencyDirectory $file) -Force
    }
}

$allRequired = $cudaFiles + $cudnnFiles
$missingFinal = @($allRequired | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $DependencyDirectory $_))
})
if ($missingFinal.Count -gt 0) {
    throw "Parakeet CUDA dependency installation is incomplete. Missing: $($missingFinal -join ', ')"
}

$manifest = [ordered]@{
    schemaVersion = 1
    runtimeKind = "nvidia-cuda-dependencies"
    cudaMajor = 12
    cudnnVersion = $cudnnVersion
    cudnnArchiveSha256 = $cudnnSha256
    architecture = "win-x64"
    sourceCudaPath = $CudaPath
    installedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    requiredFiles = $allRequired
}
$manifest |
    ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $DependencyDirectory "muesli-parakeet-cuda-dependencies.json") -Encoding UTF8

$installedBytes = (
    Get-ChildItem -LiteralPath $DependencyDirectory -File |
        Measure-Object -Property Length -Sum
).Sum

Write-Host "Parakeet CUDA dependencies installed."
Write-Host "Directory: $DependencyDirectory"
Write-Host "Size: $([math]::Round($installedBytes / 1GB, 2)) GB"
Write-Host "Restart Muesli so the CUDA provider can be selected."
