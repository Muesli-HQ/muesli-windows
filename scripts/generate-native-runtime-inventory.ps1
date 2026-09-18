param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory,
    [string]$OutputPath = "",
    [string]$CatalogPath = "",
    # Top-level directory names to skip. The MSIX carries the WPF indicator companion under
    # Indicator\; its self-contained runtime DLLs are not part of the main app's CPU catalog.
    [string[]]$ExcludeDirectory = @(),
    # Recognize the Microsoft platform runtime carried by the Windows App SDK/WebView2/DirectML.
    # These are not Muesli native components; they are recorded as platform-runtime rather than
    # treated as unmanifested. Every other unexpected native DLL still fails the inventory.
    [switch]$AllowPlatformRuntime,
    [switch]$AllowDebugRidExtras
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
if ([string]::IsNullOrWhiteSpace($CatalogPath)) {
    $CatalogPath = Join-Path $root "windows-native\Muesli.Windows.Core\NativeRuntime\public-cpu-native-catalog.json"
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $root "artifacts\native-runtime-inventory.json"
}

$packageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$catalog = Get-Content -LiteralPath $CatalogPath -Raw | ConvertFrom-Json
$failures = [System.Collections.Generic.List[string]]::new()

function Test-ManagedAssembly {
    param([string]$Path)
    try {
        [void][System.Reflection.AssemblyName]::GetAssemblyName($Path)
        return $true
    } catch {
        return $false
    }
}

function Get-RelativeUnixPath {
    param(
        [string]$BasePath,
        [string]$TargetPath
    )
    $fullBase = [IO.Path]::GetFullPath($BasePath).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $fullTarget = [IO.Path]::GetFullPath($TargetPath)
    if ($fullTarget.StartsWith($fullBase, [StringComparison]::OrdinalIgnoreCase)) {
        return $fullTarget.Substring($fullBase.Length).Replace('\', '/')
    }
    return $fullTarget.Replace('\', '/')
}

function ConvertFrom-AppxEntryName {
    # makeappx percent-encodes characters such as '+' in package entry names (libstdc%2B%2B-6.dll).
    param([string]$Name)
    try { return [Uri]::UnescapeDataString($Name) } catch { return $Name }
}

$platformRuntimePatterns = @(
    '^Microsoft\.WindowsAppRuntime\.',
    '^Microsoft\.Windows\.AI\.',
    '^Microsoft\.Windows\.ApplicationModel\.',
    '^Microsoft\.Web\.',
    '^WebView2Loader\.dll$',
    '^DirectML\.dll$'
)
$platformRuntime = [System.Collections.Generic.List[object]]::new()

$forbiddenNames = @($catalog.forbiddenPublicCudaFiles | ForEach-Object { $_.ToLowerInvariant() })
$forbiddenDirs = @($catalog.forbiddenPublicCudaDirectoryNames | ForEach-Object { $_.ToLowerInvariant() })

$fileMap = @{}
foreach ($component in $catalog.components) {
    if ($component.nativeFiles) {
        foreach ($name in $component.nativeFiles) {
            $fileMap[$name.ToLowerInvariant()] = $component
        }
    }
}

$patternComponents = @($catalog.components | Where-Object { $_.nativeFilePatterns })

$excludeSet = @{}
foreach ($name in @($ExcludeDirectory)) {
    if (-not [string]::IsNullOrWhiteSpace($name)) { $excludeSet[$name.ToLowerInvariant()] = $true }
}
$nativeFiles = Get-ChildItem -LiteralPath $packageDirectory -Recurse -File -Force -ErrorAction SilentlyContinue |
    Where-Object {
        if ($_.Extension -ne ".dll" -or (Test-ManagedAssembly $_.FullName)) { return $false }
        if ($excludeSet.Count -gt 0) {
            $relative = Get-RelativeUnixPath -BasePath $packageDirectory -TargetPath $_.FullName
            $top = ($relative -split '/')[0].ToLowerInvariant()
            if ($excludeSet.ContainsKey($top)) { return $false }
        }
        return $true
    }

$inventory = [System.Collections.Generic.List[object]]::new()
$seenRequired = @{}

foreach ($file in $nativeFiles) {
    $relative = ConvertFrom-AppxEntryName (Get-RelativeUnixPath -BasePath $packageDirectory -TargetPath $file.FullName)
    $name = ConvertFrom-AppxEntryName $file.Name
    $lower = $name.ToLowerInvariant()
    $inForbiddenDir = $false
    foreach ($segment in $relative.Split('/')) {
        if ($forbiddenDirs -contains $segment.ToLowerInvariant()) {
            $inForbiddenDir = $true
            break
        }
    }

    if ($forbiddenNames -contains $lower -or $inForbiddenDir) {
        $failures.Add("Public CPU package contains a forbidden CUDA artifact: $relative")
        $inventory.Add([ordered]@{
            fileName = $name
            relativePath = $relative
            bytes = $file.Length
            kind = "forbidden-cuda"
            componentId = $null
            licenseFile = $null
        })
        continue
    }

    $component = $null
    if ($fileMap.ContainsKey($lower)) {
        $component = $fileMap[$lower]
    } else {
        foreach ($candidate in $patternComponents) {
            foreach ($pattern in $candidate.nativeFilePatterns) {
                if ([regex]::IsMatch($name, $pattern)) {
                    $component = $candidate
                    break
                }
            }
            if ($component) { break }
        }
    }

    if (-not $component) {
        if ($AllowPlatformRuntime -and @($platformRuntimePatterns | Where-Object { [regex]::IsMatch($name, $_) }).Count -gt 0) {
            $platformRuntime.Add([ordered]@{
                fileName = $name
                relativePath = $relative
                bytes = $file.Length
                kind = "platform-runtime"
                componentId = $null
                licenseFile = $null
            })
            continue
        }
        $failures.Add("Packaged native file is missing from the inventory catalog/notices: $relative")
        $inventory.Add([ordered]@{
            fileName = $name
            relativePath = $relative
            bytes = $file.Length
            kind = "unmanifested"
            componentId = $null
            licenseFile = $null
        })
        continue
    }

    if ($component.requiredInPublicWinX64 -eq $true) {
        $seenRequired[$lower] = $true
    }
    if ($component.allowedInPublicWinX64 -eq $false -and -not $AllowDebugRidExtras) {
        $failures.Add("Public win-x64 package includes a native file that is not part of the CPU catalog: $relative")
    }

    $inventory.Add([ordered]@{
        fileName = $name
        relativePath = $relative
        bytes = $file.Length
        kind = "native"
        componentId = $component.id
        source = $component.source
        version = $component.version
        license = $component.license
        licenseFile = $component.licenseFile
        noticesHeading = $component.noticesHeading
    })
}

foreach ($component in $catalog.components) {
    if ($component.requiredInPublicWinX64 -ne $true) { continue }
    foreach ($name in @($component.nativeFiles)) {
        if (-not $seenRequired.ContainsKey($name.ToLowerInvariant())) {
            $failures.Add("Required public CPU native file is missing: $name ($($component.id))")
        }
    }
}

$noticesPath = Join-Path $packageDirectory "THIRD-PARTY-NOTICES.md"
if (Test-Path -LiteralPath $noticesPath) {
    $notices = Get-Content -LiteralPath $noticesPath -Raw
    if ($notices -match [regex]::Escape("The primary Muesli package includes the version-matched sherpa-onnx CUDA provider")) {
        $failures.Add("Packaged THIRD-PARTY-NOTICES.md still claims the public package includes a CUDA provider.")
    }
    if ($notices -notmatch "CPU Sherpa / ONNX Runtime provider only" -and $notices -notmatch "CPU Sherpa provider only") {
        $failures.Add("Packaged THIRD-PARTY-NOTICES.md does not disclose that the public package is CPU-only.")
    }
    foreach ($component in $catalog.components) {
        if ($component.requiredInPublicWinX64 -ne $true) { continue }
        if ($notices -notmatch [regex]::Escape($component.noticesHeading)) {
            $failures.Add("Packaged notices are missing component heading '$($component.noticesHeading)'.")
        }
    }
}

$metadataCandidates = Get-ChildItem -LiteralPath $packageDirectory -File -Filter "RELEASE-METADATA.json" -ErrorAction SilentlyContinue
if ($metadataCandidates) {
    $metadata = Get-Content -LiteralPath $metadataCandidates[0].FullName -Raw | ConvertFrom-Json
    if ($metadata.transcriptionRuntime.cpuProviderIncluded -ne $true -or $metadata.transcriptionRuntime.cudaProviderIncluded -ne $false) {
        $failures.Add("RELEASE-METADATA.json does not record cpuProviderIncluded=true and cudaProviderIncluded=false.")
    }
}

$report = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTime]::UtcNow.ToString("o")
    packageDirectory = $packageDirectory
    catalogPath = Get-RelativeUnixPath -BasePath $root -TargetPath ((Resolve-Path -LiteralPath $CatalogPath).Path)
    publicPackage = $catalog.publicPackage
    nativeFileCount = $inventory.Count
    files = $inventory
    platformRuntimeFiles = $platformRuntime
    failures = @($failures)
    passed = $failures.Count -eq 0
}

$destination = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destination)) | Out-Null
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $destination -Encoding UTF8
Write-Host "Wrote native-runtime inventory: $destination"

if ($failures.Count -gt 0) {
    throw "Native-runtime inventory failed:`n - $($failures -join "`n - ")"
}
