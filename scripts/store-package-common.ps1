# Store identity is public metadata from Partner Center, not a certificate or credential.
function Read-MuesliStoreIdentity {
    param([Parameter(Mandatory)][string]$Path)
    $identity = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($identity.schemaVersion -ne 1) { throw 'Unsupported Store identity schema.' }
    foreach ($name in @('storeId', 'packageName', 'publisher', 'publisherDisplayName', 'displayName', 'packageFamilyName')) {
        if ([string]::IsNullOrWhiteSpace([string]$identity.$name)) { throw "Missing Store identity field: $name" }
    }
    if ($identity.packageName -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]{2,49}$' -or
        $identity.publisher -notmatch '^CN=[0-9A-Fa-f-]{36}$' -or
        $identity.storeId -notmatch '^[A-Z0-9]{12}$' -or
        $identity.packageFamilyName -notmatch ('^' + [regex]::Escape($identity.packageName) + '_[a-z0-9]{13}$')) {
        throw 'Invalid Partner Center Store identity.'
    }
    return $identity
}

function Set-MuesliStoreManifestIdentity {
    param([Parameter(Mandatory)][string]$ManifestPath, [Parameter(Mandatory)]$StoreIdentity)
    [xml]$manifest = Get-Content -LiteralPath $ManifestPath -Raw
    $manifest.Package.Identity.Name = $StoreIdentity.packageName
    $manifest.Package.Identity.Publisher = $StoreIdentity.publisher
    $manifest.Package.Properties.DisplayName = $StoreIdentity.displayName
    $manifest.Package.Properties.PublisherDisplayName = $StoreIdentity.publisherDisplayName
    foreach ($app in @($manifest.Package.Applications.Application)) {
        $app.VisualElements.DisplayName = $StoreIdentity.displayName
    }
    $settings = [Xml.XmlWriterSettings]@{ Encoding = [Text.UTF8Encoding]::new($false); Indent = $true }
    $writer = [Xml.XmlWriter]::Create($ManifestPath, $settings)
    try { $manifest.Save($writer) } finally { $writer.Dispose() }
}

function Assert-MuesliStoreManifestIdentity {
    param([Parameter(Mandatory)][xml]$Manifest, [Parameter(Mandatory)]$StoreIdentity,
        [Parameter(Mandatory)][string]$ExpectedVersion)
    $checks = @{
        Name = @([string]$Manifest.Package.Identity.Name, [string]$StoreIdentity.packageName)
        Publisher = @([string]$Manifest.Package.Identity.Publisher, [string]$StoreIdentity.publisher)
        DisplayName = @([string]$Manifest.Package.Properties.DisplayName, [string]$StoreIdentity.displayName)
        PublisherDisplayName = @([string]$Manifest.Package.Properties.PublisherDisplayName, [string]$StoreIdentity.publisherDisplayName)
        Version = @([string]$Manifest.Package.Identity.Version, $ExpectedVersion)
        Architecture = @([string]$Manifest.Package.Identity.ProcessorArchitecture, 'x64')
    }
    foreach ($field in $checks.Keys) {
        if ($checks[$field][0] -cne $checks[$field][1]) { throw "Store package $field mismatch: '$($checks[$field][0])' != '$($checks[$field][1])'." }
    }
    foreach ($app in @($Manifest.Package.Applications.Application)) {
        if ([string]$app.VisualElements.DisplayName -cne [string]$StoreIdentity.displayName) { throw 'Store application display name mismatch.' }
    }
    $version = [version]$ExpectedVersion
    if ($version.Revision -ne 0) { throw 'Store package version must reserve its final component as zero.' }
}

function Read-MuesliPassingWindowsTestResults {
    param([Parameter(Mandatory)][string]$Path)
    [xml]$trx = Get-Content -LiteralPath $Path -Raw
    $summary = $trx.TestRun.ResultSummary
    $counters = $summary.Counters
    if ([string]$summary.outcome -notin @('Completed', 'Passed') -or [int]$counters.executed -le 0 -or
        [int]$counters.failed -ne 0 -or [int]$counters.passed -ne [int]$counters.executed) {
        throw 'Store package requires a completed, passing Windows test run.'
    }
    foreach ($name in @('error', 'timeout', 'aborted')) {
        if ([int]$counters.GetAttribute($name) -ne 0) { throw "Windows test run has $name results." }
    }
    # VSTest can write notExecuted=0 even when xUnit records skipped UnitTestResult entries.
    # Read the actual outcomes and total/executed difference so qualification skips stay visible.
    $skippedResults = @($trx.SelectNodes("/*[local-name()='TestRun']/*[local-name()='Results']/*[local-name()='UnitTestResult'][@outcome='NotExecuted']")).Count
    $notExecuted = [Math]::Max([int]$counters.notExecuted,
        [Math]::Max($skippedResults, [Math]::Max(0, [int]$counters.GetAttribute('total') - [int]$counters.executed)))
    return [ordered]@{
        passed = [int]$counters.passed
        failed = [int]$counters.failed
        notExecuted = $notExecuted
    }
}

function Get-MuesliMakePriPath {
    # Prefer the SDK restored for this project, then the installed Windows SDK.
    $roots = @(
        (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools'),
        'C:\Program Files (x86)\Windows Kits\10\bin'
    )
    foreach ($root in $roots) {
        $tool = Get-ChildItem -LiteralPath $root -Recurse -File -Filter 'makepri.exe' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '[\\/]x64[\\/]' } |
            Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
        if ($tool) { return $tool.FullName }
    }
    throw 'MakePri.exe was not found; Store resource-map identity cannot be verified.'
}

function Assert-MuesliPriResourceMap {
    param([Parameter(Mandatory)][xml]$Dump, [Parameter(Mandatory)][string]$ExpectedName)
    $names = @($Dump.SelectNodes("//*[local-name()='ResourceMap']") | ForEach-Object { $_.GetAttribute('name') })
    if ($names -cnotcontains $ExpectedName) {
        throw "PRI resource-map identity mismatch: expected '$ExpectedName', found '$($names -join ', ')'."
    }
}
