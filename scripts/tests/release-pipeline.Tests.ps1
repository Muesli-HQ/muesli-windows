$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptRoot

Describe "Muesli MSIX release signing and update contracts" {
    It "signs the MSIX before archiving the update manifest and refuses unsigned publish reuse" {
        $orchestrator = Get-Content (Join-Path $scriptRoot "build-signed-release.ps1") -Raw
        $orchestrator.IndexOf("sign-windows-release.ps1") | Should BeGreaterThan -1
        $orchestrator.IndexOf("Assert-MuesliSignedMsix") | Should BeGreaterThan -1
        $orchestrator.IndexOf("package-winui-msix.ps1") | Should BeGreaterThan -1
        $orchestrator.IndexOf("Assert-MuesliProductionPublisher") | Should BeGreaterThan -1
        $installer = Get-Content (Join-Path $scriptRoot "build-installer.ps1") -Raw
        $installer | Should Match "package-winui-msix.ps1"
        $installer | Should Not Match "Inno Setup"
        $sign = Get-Content (Join-Path $scriptRoot "sign-windows-release.ps1") -Raw
        $sign | Should Match "Assert-MuesliSignedMsix"
        $sign | Should Match "requires a -TimestampUrl"
    }

    It "records fixture MSIX signatures without changing target bytes" {
        $scratch = Join-Path ([IO.Path]::GetTempPath()) ("muesli-msix-signing-" + [guid]::NewGuid().ToString("N"))
        $signatures = Join-Path $scratch "signatures"
        New-Item -ItemType Directory -Force -Path $scratch | Out-Null
        try {
            $msix = Join-Path $scratch "Muesli.Windows.WinUI_0.3.0_x64.msix"
            Add-Type -AssemblyName System.IO.Compression
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $archive = [System.IO.Compression.ZipFile]::Open($msix, [System.IO.Compression.ZipArchiveMode]::Create)
            try {
                $entry = $archive.CreateEntry("AppxManifest.xml")
                $writer = [System.IO.StreamWriter]::new($entry.Open())
                $writer.Write('<?xml version="1.0" encoding="utf-8"?><Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity Name="Muesli.Windows" Publisher="CN=FixturePublisher" Version="0.3.0.0" /></Package>')
                $writer.Dispose()
            } finally { $archive.Dispose() }

            $before = (Get-FileHash -LiteralPath $msix -Algorithm SHA256).Hash
            & (Join-Path $scriptRoot "sign-windows-release.ps1") -MsixPath $msix -DryRun -SignatureOutputDirectory $signatures
            $after = (Get-FileHash -LiteralPath $msix -Algorithm SHA256).Hash
            $before | Should Be $after
            $record = Get-Content (Join-Path $signatures "Muesli.Windows.WinUI.msix.signature.json") -Raw | ConvertFrom-Json
            $record.status | Should Be "Simulated"
            $record.authenticode | Should Be $false
            $record.timestamped | Should Be $false
        } finally {
            Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It "verifies the update-channel manifest contract in fixture mode" {
        $scratch = Join-Path ([IO.Path]::GetTempPath()) ("muesli-update-" + [guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Force -Path $scratch | Out-Null
        try {
            $zip = Join-Path $scratch "muesli-windows-0.3.0-win-x64.zip"
            [IO.File]::WriteAllText($zip, "fixture package")
            $inventory = Join-Path $scratch "package-content-inventory.json"
            [IO.File]::WriteAllText($inventory, '{"contentDigest":"fixture-digest"}')
            $manifest = Join-Path $scratch "update-channel-v1.json"
            & (Join-Path $scriptRoot "write-update-manifest.ps1") -PortablePackagePath $zip -ContentInventoryPath $inventory -OutputPath $manifest -SigningMode fixture
            $detached = Get-Content "$manifest.signature.json" -Raw | ConvertFrom-Json
            $publicKeyBytes = [Convert]::FromBase64String([string]$detached.publicKeySpkiBase64)
            $expectedKeyHash = ([BitConverter]::ToString(([Security.Cryptography.SHA256]::Create().ComputeHash($publicKeyBytes))).Replace('-', '')).ToLowerInvariant()
            & (Join-Path $scriptRoot "verify-update-manifest.ps1") -ManifestPath $manifest -ExpectedSigningMode fixture -ExpectedPublisherPublicKeySha256 $expectedKeyHash
            $parsed = Get-Content $manifest -Raw | ConvertFrom-Json
            $parsed.contract | Should Be "muesli.windows.update-channel"
            $parsed.signing.status | Should Be "FixtureOnly"
        } finally {
            Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It "rejects a signed manifest without an independently supplied publisher pin" {
        $scratch = Join-Path ([IO.Path]::GetTempPath()) ("muesli-update-pin-" + [guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Force -Path $scratch | Out-Null
        try {
            $zip = Join-Path $scratch "package.zip"
            [IO.File]::WriteAllText($zip, "fixture package")
            $manifest = Join-Path $scratch "update.json"
            & (Join-Path $scriptRoot "write-update-manifest.ps1") -PortablePackagePath $zip -OutputPath $manifest -SigningMode fixture
            $missingPinError = $false
            try { & (Join-Path $scriptRoot "verify-update-manifest.ps1") -ManifestPath $manifest -ExpectedSigningMode fixture } catch { $missingPinError = $true }
            $missingPinError | Should Be $true
            $wrongPinError = $false
            try { & (Join-Path $scriptRoot "verify-update-manifest.ps1") -ManifestPath $manifest -ExpectedSigningMode fixture -ExpectedPublisherPublicKeySha256 (('0' * 64) -join '') } catch { $wrongPinError = $true }
            $wrongPinError | Should Be $true
        } finally {
            Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It "does not allow automatic production certificate selection" {
        $sign = Get-Content (Join-Path $scriptRoot "sign-windows-release.ps1") -Raw
        $sign | Should Not Match '"/a"'
        $sign | Should Match "CertificateThumbprint"
        $sign | Should Match "automatic certificate selection is forbidden"
        $sign | Should Match "must exactly match the MSIX manifest publisher"
    }

    It "compares package directories independent of checkout path" {
        $scratch = Join-Path ([IO.Path]::GetTempPath()) ("muesli-inventory-" + [guid]::NewGuid().ToString("N"))
        $left = Join-Path $scratch "checkout-a\publish"
        $right = Join-Path $scratch "checkout-b\publish"
        New-Item -ItemType Directory -Force -Path $left, $right | Out-Null
        try {
            [IO.File]::WriteAllText((Join-Path $left "Muesli.Windows.WinUI.exe"), "fixture binary")
            [IO.File]::WriteAllText((Join-Path $left "README.txt"), "same content")
            Copy-Item -LiteralPath (Join-Path $left "Muesli.Windows.WinUI.exe") -Destination (Join-Path $right "Muesli.Windows.WinUI.exe")
            Copy-Item -LiteralPath (Join-Path $left "README.txt") -Destination (Join-Path $right "README.txt")
            $output = Join-Path $scratch "comparison.json"
            & (Join-Path $scriptRoot "compare-release-content-inventories.ps1") -LeftPath $left -RightPath $right -OutputPath $output
            $comparison = Get-Content $output -Raw | ConvertFrom-Json
            $comparison.matched | Should Be $true
            $comparison.differenceCount | Should Be 0
        } finally {
            Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
