# Shared helpers for the unsigned Windows release path (L04).
# Do not sign. Do not claim CUDA is included in the public package.

function Get-MuesliRepoRootFromScript {
    param([string]$ScriptRoot)
    return (Resolve-Path (Join-Path $ScriptRoot "..")).Path
}

function Read-MuesliGlobalJsonSdk {
    param([string]$Root)
    $globalJsonPath = Join-Path $Root "global.json"
    if (-not (Test-Path -LiteralPath $globalJsonPath)) {
        throw "Pinned SDK file was not found at '$globalJsonPath'."
    }
    return Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
}

function Assert-MuesliPinnedSdk {
    param([string]$Root)
    $globalJson = Read-MuesliGlobalJsonSdk -Root $Root
    $pinnedText = [string]$globalJson.sdk.version
    $rollForward = [string]$globalJson.sdk.rollForward
    $actualText = (& dotnet --version)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($actualText)) {
        throw "dotnet --version failed; cannot verify the pinned SDK $pinnedText."
    }
    $actualText = $actualText.Trim()
    if ($rollForward -ne "disable") {
        throw "global.json must set rollForward=disable for reproducible release inputs; found '$rollForward'."
    }
    if ($actualText -ne $pinnedText) {
        throw "SDK $actualText does not exactly match pinned $pinnedText."
    }
    Write-Host "Using pinned .NET SDK $actualText (global.json $pinnedText, rollForward=$rollForward)."
    return $actualText
}

function Get-MuesliGitHead {
    param([string]$Root)
    $head = (& git -C $Root rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) { return "" }
    return ([string]$head).Trim()
}

function Get-MuesliGitPorcelain {
    param([string]$Root)
    $status = (& git -C $Root status --porcelain=v1)
    if ($LASTEXITCODE -ne 0) {
        throw "git status failed in '$Root'. Release packaging requires a git work tree."
    }
    return @($status | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Assert-MuesliCleanReleaseInputs {
    param(
        [string]$Root,
        [switch]$AllowDirty,
        [string]$OverridePath = ""
    )

    $porcelain = @(Get-MuesliGitPorcelain -Root $Root)
    $head = Get-MuesliGitHead -Root $Root
    if ($porcelain.Count -eq 0) {
        Write-Host "Release inputs are clean at $head."
        return [pscustomobject]@{
            Clean = $true
            AllowDirty = [bool]$AllowDirty
            Head = $head
            Porcelain = @()
            OverridePath = $null
        }
    }

    $rendered = $porcelain -join "`n"
    if (-not $AllowDirty) {
        throw @"
Refusing to build a release package from a dirty work tree.
Commit or stash these paths, or pass -AllowDirty to record an override:

$rendered
"@
    }

    if ([string]::IsNullOrWhiteSpace($OverridePath)) {
        $OverridePath = Join-Path $Root "artifacts\dirty-release-override.json"
    }
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($OverridePath)) | Out-Null
    $record = [ordered]@{
        schemaVersion = 1
        allowDirty = $true
        recordedAtUtc = [DateTime]::UtcNow.ToString("o")
        head = $head
        porcelain = @($porcelain)
        note = "Unsigned rehearsal/package proceeded with uncommitted inputs. This override is recorded and is not a clean release."
    }
    Write-Utf8NoBomFile -Path $OverridePath -Content (($record | ConvertTo-Json -Depth 6) + "`n")
    Write-Warning "AllowDirty override recorded at $OverridePath"
    return [pscustomobject]@{
        Clean = $false
        AllowDirty = $true
        Head = $head
        Porcelain = @($porcelain)
        OverridePath = $OverridePath
    }
}

function Assert-MuesliModelSourcesPresent {
    param([string]$Root)

    $appRoot = Join-Path $Root "windows-native\Muesli.Windows"
    $required = @(
        (Join-Path $appRoot "Models\DictationItem.cs"),
        (Join-Path $appRoot "Models\MeetingItem.cs"),
        (Join-Path $appRoot "Features\Models\ModelsView.xaml")
    )
    $missing = @($required | Where-Object { -not (Test-Path -LiteralPath $_) })
    if ($missing.Count -gt 0) {
        throw "Required Windows model sources are missing from the checkout: $($missing -join ', '). L40 must track these files; release packaging never copies sources from another worktree."
    }
    return "tracked"
}

function Get-MuesliDeterministicPublishArguments {
    return @(
        "--self-contained", "true",
        "-p:PublishSingleFile=false",
        "-p:PublishReadyToRun=true",
        "-p:Deterministic=true",
        "-p:ContinuousIntegrationBuild=true",
        "-p:DebugType=None",
        "-p:DebugSymbols=false"
    )
}

function Write-Utf8NoBomFile {
    param(
        [string]$Path,
        [string]$Content
    )
    $directory = [IO.Path]::GetDirectoryName($Path)
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [IO.File]::WriteAllText($Path, $Content, $utf8)
}

function Get-Sha256HexFromBytes {
    param([byte[]]$Bytes)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($Bytes)
        return ([BitConverter]::ToString($hash).Replace("-", "").ToLowerInvariant())
    } finally {
        $sha.Dispose()
    }
}

function Get-Sha256HexFromFile {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-StablePackagedNativeInventory {
    param(
        [string]$SourcePath,
        [string]$DestinationPath
    )

    $raw = Get-Content -LiteralPath $SourcePath -Raw | ConvertFrom-Json
    $stable = [ordered]@{
        schemaVersion = $raw.schemaVersion
        catalogPath = $raw.catalogPath
        publicPackage = $raw.publicPackage
        nativeFileCount = $raw.nativeFileCount
        files = $raw.files
        failures = $raw.failures
        passed = $raw.passed
        cudaProviderIncluded = $false
        cpuProviderIncluded = $true
    }
    Write-Utf8NoBomFile -Path $DestinationPath -Content (($stable | ConvertTo-Json -Depth 8) + "`n")
}

function Get-MuesliZipContentInventory {
    param([string]$ZipPath)

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $files = [System.Collections.Generic.List[object]]::new()
        foreach ($entry in $archive.Entries) {
            $path = $entry.FullName.Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($path) -or $path.EndsWith('/')) {
                continue
            }
            $stream = $entry.Open()
            try {
                $buffer = New-Object System.IO.MemoryStream
                $stream.CopyTo($buffer)
                $bytes = $buffer.ToArray()
            } finally {
                $stream.Dispose()
            }

            $hashBytes = $bytes
            if ([IO.Path]::GetFileName($path) -eq "native-runtime-inventory.json") {
                try {
                    $text = [Text.Encoding]::UTF8.GetString($bytes)
                    $obj = $text | ConvertFrom-Json
                    $stable = [ordered]@{
                        schemaVersion = $obj.schemaVersion
                        catalogPath = $obj.catalogPath
                        publicPackage = $obj.publicPackage
                        nativeFileCount = $obj.nativeFileCount
                        files = $obj.files
                        failures = $obj.failures
                        passed = $obj.passed
                    }
                    $hashBytes = [Text.Encoding]::UTF8.GetBytes((($stable | ConvertTo-Json -Depth 8) + "`n"))
                } catch {
                    $hashBytes = $bytes
                }
            }

            $files.Add([ordered]@{
                path = $path
                bytes = $bytes.Length
                sha256 = Get-Sha256HexFromBytes -Bytes $hashBytes
            })
        }

        $ordered = @($files | Sort-Object { $_.path })
        $canonical = ($ordered | ForEach-Object { "$($_.path)|$($_.bytes)|$($_.sha256)" }) -join "`n"
        $digest = Get-Sha256HexFromBytes -Bytes ([Text.Encoding]::UTF8.GetBytes($canonical + "`n"))
        return [ordered]@{
            schemaVersion = 1
            zipPath = [IO.Path]::GetFileName($ZipPath)
            entryCount = $ordered.Count
            ignoresZipEntryTimestamps = $true
            timestampNote = "Compress-Archive stores per-entry LastWriteTime, so ZIP file hashes can differ across otherwise identical builds. Compare contentDigest, which hashes entry names, sizes, and file bytes and ignores zip-entry timestamps."
            contentDigest = $digest
            files = $ordered
            publicPackage = [ordered]@{
                cpuProviderIncluded = $true
                cudaProviderIncluded = $false
            }
        }
    } finally {
        $archive.Dispose()
    }
}

function Compare-MuesliContentInventories {
    param(
        [Parameter(Mandatory = $true)]$Left,
        [Parameter(Mandatory = $true)]$Right
    )

    $matched = $Left.contentDigest -eq $Right.contentDigest
    $differences = [System.Collections.Generic.List[string]]::new()
    if (-not $matched) {
        $leftMap = @{}
        foreach ($file in @($Left.files)) { $leftMap[$file.path] = $file }
        $rightMap = @{}
        foreach ($file in @($Right.files)) { $rightMap[$file.path] = $file }
        $paths = @($leftMap.Keys + $rightMap.Keys | Select-Object -Unique | Sort-Object)
        foreach ($path in $paths) {
            $l = $leftMap[$path]
            $r = $rightMap[$path]
            if ($null -eq $l) {
                $differences.Add("only in right: $path")
            } elseif ($null -eq $r) {
                $differences.Add("only in left: $path")
            } elseif ($l.sha256 -ne $r.sha256 -or $l.bytes -ne $r.bytes) {
                $differences.Add("changed: $path left=$($l.sha256) ($($l.bytes)) right=$($r.sha256) ($($r.bytes))")
            }
        }
    }

    return [ordered]@{
        matched = $matched
        leftDigest = $Left.contentDigest
        rightDigest = $Right.contentDigest
        differenceCount = $differences.Count
        differences = @($differences)
        zipTimestampNote = $Left.timestampNote
    }
}
