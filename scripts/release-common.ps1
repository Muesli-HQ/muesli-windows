# Shared helpers for Windows release packaging and signing orchestration (L04-L06).
# Production signing is performed only by sign-windows-release.ps1.
# Do not claim CUDA is included in the public package.

function Get-MuesliRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$TargetPath
    )

    # Windows PowerShell 5.1 runs on .NET Framework, where Path.GetRelativePath does not exist.
    # Uri.MakeRelativeUri keeps the release scripts compatible with both powershell.exe and pwsh.
    $baseFull = [IO.Path]::GetFullPath($BasePath).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $targetFull = [IO.Path]::GetFullPath($TargetPath)
    $baseUri = [Uri]::new($baseFull)
    $targetUri = [Uri]::new($targetFull)
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace('/', [IO.Path]::DirectorySeparatorChar)
}

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

function Read-MuesliSharedCoreLock {
    param([string]$Root)
    $lockPath = Join-Path $Root "windows-native\shared-core.lock.json"
    if (-not (Test-Path -LiteralPath $lockPath)) {
        throw "Shared-core lock file was not found at '$lockPath'."
    }
    return Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
}

# Release packaging must build the shared Swift bridge from the immutable revision recorded in
# windows-native/shared-core.lock.json. Local development may instead resolve a working tree via
# MUESLI_SHARED_CORE_PACKAGE, but that unpinned path is never release evidence and warns loudly.
function Assert-MuesliSharedCoreRevision {
    param(
        [string]$Root,
        [switch]$AllowUnpinnedDevOverride
    )

    $lock = Read-MuesliSharedCoreLock -Root $Root
    if ($lock.repository -ne "Muesli-HQ/muesli") {
        throw "shared-core.lock.json points at unexpected repository '$($lock.repository)'."
    }
    # Absent means "required" so an older lock can never silently downgrade to the fallback.
    $bridgeRequired = if ($null -eq $lock.PSObject.Properties['bridgeRequired']) {
        $true
    } else {
        [bool]$lock.bridgeRequired
    }
    $revision = [string]$lock.revision
    if (-not $bridgeRequired -and -not [string]::IsNullOrWhiteSpace($revision)) {
        throw "shared-core.lock.json sets bridgeRequired=false but still pins revision '$revision'; choose a single shipping mode."
    }
    if (-not [string]::IsNullOrWhiteSpace($revision)) {
        if ($revision -notmatch '^[0-9a-fA-F]{40}$') {
            throw "shared-core.lock.json revision '$revision' is not a full 40-character commit SHA."
        }
        Write-Host "Shared core pinned at revision $revision."
        return [pscustomobject]@{ Revision = $revision.ToLowerInvariant(); Pinned = $true; DevOverride = ""; Mode = "Pinned"; BridgeRequired = $true }
    }

    if (-not $bridgeRequired) {
        # Deliberate shipping decision: no approved upstream ABI exists, so the release ships the
        # parity-tested managed text processor instead of the optional Swift bridge.
        Write-Host "Shared core not pinned and bridgeRequired=false: release ships the managed-fallback text processor."
        return [pscustomobject]@{ Revision = ""; Pinned = $false; DevOverride = ""; Mode = "ManagedFallback"; BridgeRequired = $false }
    }

    $package = $env:MUESLI_SHARED_CORE_PACKAGE
    if ($AllowUnpinnedDevOverride -and -not [string]::IsNullOrWhiteSpace($package)) {
        Write-Warning @"
windows-native/shared-core.lock.json.revision is EMPTY. This build resolves the shared Swift core
from MUESLI_SHARED_CORE_PACKAGE ('$package') and is NOT reproducible release evidence.
Commit the shared Swift changes in the shared repository and set revision to the committed SHA
before producing a release. See docs/SHARED_CORE_PINNING.md.
"@
        return [pscustomobject]@{ Revision = ""; Pinned = $false; DevOverride = $package; Mode = "DevOverride"; BridgeRequired = $true }
    }

    throw @"
windows-native/shared-core.lock.json has an empty revision, so this release build has no immutable
shared-core pin. Commit the shared Swift changes, copy the committed SHA into revision, and rerun.
See docs/SHARED_CORE_PINNING.md. Local development may set MUESLI_SHARED_CORE_PACKAGE with the
unpinned override explicitly allowed.
"@
}

function Assert-MuesliModelSourcesPresent {
    param([string]$Root)

    # Active shipping paths after the WinUI/Core migration. The retired WPF tree no longer owns the
    # model sources or the models view.
    $required = @(
        (Join-Path $Root "windows-native\Muesli.Windows.Core\Models\MeetingItem.cs"),
        (Join-Path $Root "windows-native\Muesli.Windows.WinUI\Pages\ModelsPage.xaml")
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

function Normalize-MuesliThumbprint {
    param([Parameter(Mandatory = $true)][string]$Thumbprint)
    return ($Thumbprint -replace '\s', '').ToUpperInvariant()
}

function Normalize-MuesliSha256 {
    param([Parameter(Mandatory = $true)][string]$Hash)
    $normalized = ($Hash -replace '\s', '').ToLowerInvariant()
    if ($normalized -notmatch '^[0-9a-f]{64}$') {
        throw "SHA-256 pin must be a 64-character hexadecimal hash; got '$Hash'."
    }
    return $normalized
}

function Get-MuesliCertificateByThumbprint {
    param([Parameter(Mandatory = $true)][string]$Thumbprint)

    $normalized = Normalize-MuesliThumbprint -Thumbprint $Thumbprint
    if ($normalized -notmatch '^[0-9A-F]{40}$') {
        throw "Certificate thumbprint must be a 40-character SHA-1 thumbprint; got '$Thumbprint'."
    }

    foreach ($storePath in @('Cert:\CurrentUser\My', 'Cert:\LocalMachine\My')) {
        $certificate = Get-ChildItem -Path $storePath -ErrorAction SilentlyContinue |
            Where-Object { (Normalize-MuesliThumbprint -Thumbprint ([string]$_.Thumbprint)) -eq $normalized } |
            Select-Object -First 1
        if ($null -ne $certificate) {
            return $certificate
        }
    }
    throw "Certificate with thumbprint '$normalized' was not found in the CurrentUser or LocalMachine personal store."
}

# A public release must never ship the development publisher placeholder. Production packaging
# resolves an approved publisher/certificate subject; anything else fails closed here.
function Assert-MuesliProductionPublisher {
    param(
        [string]$Publisher = "",
        [string]$DevPlaceholder = "CN=AppPublisher"
    )

    if ([string]::IsNullOrWhiteSpace($Publisher)) {
        throw "A public release must declare a production publisher. The development placeholder '$DevPlaceholder' cannot ship."
    }
    if ([string]::Equals($Publisher.Trim(), $DevPlaceholder, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to produce a public release with the development publisher '$DevPlaceholder'. Supply an approved production publisher and certificate."
    }
}

function Assert-MuesliCodeSigningCertificate {
    param(
        [Parameter(Mandatory = $true)]$Certificate,
        [string]$ExpectedSubject = ""
    )

    if ($null -eq $Certificate) { throw "A signing certificate is required." }
    if (-not $Certificate.HasPrivateKey) { throw "Signing certificate '$($Certificate.Thumbprint)' has no private key." }
    $now = [DateTime]::UtcNow
    if ($Certificate.NotBefore.ToUniversalTime() -gt $now -or $Certificate.NotAfter.ToUniversalTime() -lt $now) {
        throw "Signing certificate '$($Certificate.Thumbprint)' is outside its validity period."
    }
    if ([string]::IsNullOrWhiteSpace([string]$Certificate.Subject)) {
        throw "Signing certificate '$($Certificate.Thumbprint)' has no publisher subject."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSubject) -and
        -not [string]::Equals([string]$Certificate.Subject, $ExpectedSubject, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Signing certificate subject '$($Certificate.Subject)' did not match expected publisher '$ExpectedSubject'."
    }

    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    $hasCodeSigningEku = $false
    foreach ($extension in @($Certificate.Extensions)) {
        if ([string]$extension.Oid.Value -ne '2.5.29.37') { continue }
        try {
            $eku = [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$extension
            $hasCodeSigningEku = @($eku.EnhancedKeyUsages | Where-Object { [string]$_.Value -eq $codeSigningOid }).Count -gt 0
        } catch {
            $hasCodeSigningEku = ([string]$extension.Format($false) -match 'Code Signing|1\.3\.6\.1\.5\.5\.7\.3\.3')
        }
    }
    if (-not $hasCodeSigningEku) {
        throw "Signing certificate '$($Certificate.Thumbprint)' does not contain the Code Signing EKU ($codeSigningOid)."
    }
    return $Certificate
}

function Get-MuesliPublicKeySha256 {
    param([Parameter(Mandatory = $true)][byte[]]$SubjectPublicKeyInfo)
    return Get-Sha256HexFromBytes -Bytes $SubjectPublicKeyInfo
}

function Get-MuesliMsixManifestIdentity {
    param([Parameter(Mandatory = $true)][string]$MsixPath)
    if (-not (Test-Path -LiteralPath $MsixPath -PathType Leaf)) {
        throw "MSIX not found: $MsixPath"
    }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($MsixPath)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq "AppxManifest.xml" } | Select-Object -First 1
        if ($null -eq $entry) { throw "The MSIX has no AppxManifest.xml: $MsixPath" }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally {
        $archive.Dispose()
    }
    return [pscustomobject]@{
        Name = [string]$manifest.Package.Identity.Name
        Publisher = [string]$manifest.Package.Identity.Publisher
        Version = [string]$manifest.Package.Identity.Version
    }
}

# Locates the newest unsigned/signed WinUI MSIX under a directory (default artifacts\msix).
function Get-MuesliLatestMsix {
    param([string]$SearchRoot)
    if ([string]::IsNullOrWhiteSpace($SearchRoot)) {
        $SearchRoot = Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\msix"
    }
    if (-not (Test-Path -LiteralPath $SearchRoot)) { return $null }
    return Get-ChildItem -Path $SearchRoot -Recurse -File -Filter 'Muesli.Windows.WinUI_*.msix' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
}

# A public release must not ship the development publisher placeholder, and the manifest publisher
# must match the approved production publisher when one is supplied.
function Assert-MuesliMsixPublisher {
    param(
        [Parameter(Mandatory = $true)][string]$MsixPath,
        [string]$ExpectedPublisher = "",
        [string]$DevPlaceholder = "CN=AppPublisher"
    )
    $identity = Get-MuesliMsixManifestIdentity -MsixPath $MsixPath
    if ([string]::IsNullOrWhiteSpace($identity.Publisher)) {
        throw "MSIX '$MsixPath' has an empty publisher."
    }
    if ([string]::Equals($identity.Publisher.Trim(), $DevPlaceholder, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing a public release whose MSIX still uses the development publisher '$DevPlaceholder'."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedPublisher) -and
        -not [string]::Equals($identity.Publisher.Trim(), $ExpectedPublisher.Trim(), [StringComparison]::OrdinalIgnoreCase)) {
        throw "MSIX publisher '$($identity.Publisher)' does not match expected publisher '$ExpectedPublisher'."
    }
    return $identity
}

# Fail-closed MSIX signature gate. Requires a valid Authenticode signature whose signer subject
# matches the manifest publisher, and (unless -SkipTimestamp) a trusted timestamp.
function Assert-MuesliSignedMsix {
    param(
        [Parameter(Mandatory = $true)][string]$MsixPath,
        [switch]$SkipTimestamp,
        [string]$ExpectedPublisher = ""
    )
    if (-not (Test-Path -LiteralPath $MsixPath -PathType Leaf)) {
        throw "MSIX not found: $MsixPath"
    }
    $identity = Get-MuesliMsixManifestIdentity -MsixPath $MsixPath
    $signature = Get-AuthenticodeSignature -LiteralPath $MsixPath
    if ($null -eq $signature -or [string]$signature.Status -ne "Valid") {
        $status = if ($null -eq $signature) { "Unavailable" } else { [string]$signature.Status }
        throw "MSIX '$MsixPath' is not validly signed (Authenticode status '$status'). Public release requires a signed MSIX."
    }
    if ($null -eq $signature.SignerCertificate) {
        throw "MSIX '$MsixPath' has no signer certificate."
    }
    if (-not [string]::Equals([string]$signature.SignerCertificate.Subject, $identity.Publisher, [StringComparison]::OrdinalIgnoreCase)) {
        throw "MSIX signer subject '$($signature.SignerCertificate.Subject)' does not match manifest publisher '$($identity.Publisher)'."
    }
    if (-not $SkipTimestamp -and $null -eq $signature.TimeStamperCertificate) {
        throw "MSIX '$MsixPath' has no trusted timestamp. Public release requires timestamped signing."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedPublisher) -and
        -not [string]::Equals($identity.Publisher.Trim(), $ExpectedPublisher.Trim(), [StringComparison]::OrdinalIgnoreCase)) {
        throw "MSIX publisher '$($identity.Publisher)' does not match expected publisher '$ExpectedPublisher'."
    }
    return [ordered]@{
        status = [string]$signature.Status
        signerSubject = [string]$signature.SignerCertificate.Subject
        signerThumbprint = (Normalize-MuesliThumbprint -Thumbprint ([string]$signature.SignerCertificate.Thumbprint))
        timestamped = $null -ne $signature.TimeStamperCertificate
        publisher = [string]$identity.Publisher
        sha256 = Get-Sha256HexFromFile -Path $MsixPath
    }
}

# Legal/product release gates that are not code-correctness checks. This helper records the
# decision the release owner still owes; it does not make the decision. A gate here never counts
# as passed, so a rehearsal that includes it cannot claim the affected feature is launch-ready.
function Get-MuesliReleaseLegalProductGates {
    param([string]$Root)

    $publisher = ""
    $manifestPath = Join-Path $Root "windows-native\Muesli.Windows.WinUI\Package.appxmanifest"
    if (Test-Path -LiteralPath $manifestPath) {
        try {
            [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
            $publisher = [string]$manifest.Package.Identity.Publisher
        } catch {
            $publisher = "<unreadable>"
        }
    }

    return @(
        [ordered]@{
            id = "EXP-01"
            area = "PDF export (QuestPDF Community eligibility)"
            status = "BlockedUntilReleaseOwnerDecision"
            detail = "QuestPDF is selected as LicenseType.Community but no eligibility conclusion is recorded. PDF is not launch-ready."
            launchClaim = "PDF export must not be described as launch-ready."
            disableSwitch = "MeetingDocumentWriter.PdfExportApproved (default false) / MUESLI_PDF_EXPORT_APPROVED=1 for development only."
        },
        [ordered]@{
            id = "SIGN-02"
            area = "Production signing certificate"
            status = "ExternallyBlocked"
            detail = "No approved production code-signing certificate; the rehearsal is unsigned by design."
            launchClaim = "No Authenticode/MSIX signature is claimed."
            disableSwitch = ""
        },
        [ordered]@{
            id = "IDENT-01"
            area = "Release publisher identity"
            status = if ($publisher -match 'CN=AppPublisher') { "PlaceholderPresent" } else { "NeedsReview" }
            detail = "Package.appxmanifest Identity Publisher is '$publisher'; a public release must replace the development placeholder."
            launchClaim = "The package does not yet carry the production publisher identity."
            disableSwitch = ""
        },
        [ordered]@{
            id = "UPD-01"
            area = "Update channel"
            status = "ManualUpdatesForV1"
            detail = "Release-side manifest signing/verification and an app-side check/verify/download/rollback workflow exist; automatic installation stays disabled until the production Authenticode certificate exists (D5), so v1 updates install manually."
            launchClaim = "v1 updates are manual: the app can check and verify a signed manifest, but does not auto-install."
            disableSwitch = ""
        },
        [ordered]@{
            id = "SUP-01"
            area = "Privacy/support contact"
            status = "NeedsContact"
            detail = "A privacy policy ships, but no support contact or in-app privacy/support link is verified."
            launchClaim = "Support contact is not yet published."
            disableSwitch = ""
        },
        [ordered]@{
            id = "AUD-03"
            area = "Audio interference policy"
            status = "DecisionPending"
            detail = "The pause-vs-duck-vs-none policy for other media during capture is undecided and unimplemented."
            launchClaim = "No audio-interference behavior is claimed."
            disableSwitch = ""
        }
    )
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
            # AppxBlockMap.xml/AppxSignature.p7x are derived container metadata regenerated by
            # makeappx/signing. Their bytes are nondeterministic across otherwise identical builds;
            # every payload entry they describe is still hashed above, so excluding them cannot hide
            # a content change.
            if ([IO.Path]::GetFileName($path) -in @('AppxBlockMap.xml', 'AppxSignature.p7x')) {
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
            ignoredContainerMetadata = @('AppxBlockMap.xml', 'AppxSignature.p7x')
            timestampNote = "Compress-Archive stores per-entry LastWriteTime, so ZIP file hashes can differ across otherwise identical builds. Compare contentDigest, which hashes entry names, sizes, and file bytes and ignores zip-entry timestamps and derived Appx container metadata."
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

function Get-MuesliDirectoryContentInventory {
    param([Parameter(Mandatory = $true)][string]$DirectoryPath)

    $resolved = (Resolve-Path -LiteralPath $DirectoryPath).Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "Package directory not found: $DirectoryPath"
    }
    $files = [System.Collections.Generic.List[object]]::new()
    foreach ($file in @(Get-ChildItem -LiteralPath $resolved -Recurse -File -Force | Sort-Object FullName)) {
        $relative = (Get-MuesliRelativePath -BasePath $resolved -TargetPath $file.FullName).Replace('\', '/')
        $hashBytes = [IO.File]::ReadAllBytes($file.FullName)
        if ([IO.Path]::GetFileName($relative) -eq "native-runtime-inventory.json") {
            try {
                $obj = [Text.Encoding]::UTF8.GetString($hashBytes) | ConvertFrom-Json
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
                # A malformed inventory is part of the content and will fail its own gate.
            }
        }
        $files.Add([ordered]@{
            path = $relative
            bytes = $file.Length
            sha256 = Get-Sha256HexFromBytes -Bytes $hashBytes
        })
    }
    $ordered = @($files | Sort-Object { $_.path })
    $canonical = ($ordered | ForEach-Object { "$($_.path)|$($_.bytes)|$($_.sha256)" }) -join "`n"
    $digest = Get-Sha256HexFromBytes -Bytes ([Text.Encoding]::UTF8.GetBytes($canonical + "`n"))
    return [ordered]@{
        schemaVersion = 1
        sourceType = "directory"
        entryCount = $ordered.Count
        ignoresSourcePath = $true
        contentDigest = $digest
        files = $ordered
        publicPackage = [ordered]@{
            cpuProviderIncluded = $true
            cudaProviderIncluded = $false
        }
    }
}
