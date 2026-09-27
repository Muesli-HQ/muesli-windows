function Get-MuesliReleaseProperties {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $propsPath = Join-Path $Root "Directory.Build.props"
    if (-not (Test-Path -LiteralPath $propsPath)) {
        throw "Shared release properties were not found at '$propsPath'."
    }

    [xml]$props = Get-Content -LiteralPath $propsPath -Raw
    $propertyGroup = @($props.Project.PropertyGroup) |
        Where-Object { $_.MuesliVersion -and $_.MuesliReleaseChannel } |
        Select-Object -First 1
    if ($null -eq $propertyGroup) {
        throw "Directory.Build.props does not define the authoritative Muesli release properties."
    }

    $version = ([string]$propertyGroup.MuesliVersion).Trim()
    $channel = ([string]$propertyGroup.MuesliReleaseChannel).Trim()
    $minimumWindowsVersion = ([string]$propertyGroup.MuesliMinimumWindowsVersion).Trim()
    $metadataFileName = ([string]$propertyGroup.MuesliReleaseMetadataFileName).Trim()
    $supportedEnvironments = @(([string]$propertyGroup.MuesliSupportedEnvironments) -split ';' |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ })

    $parsedVersion = [Version]::new()
    if (-not [Version]::TryParse($version, [ref]$parsedVersion) -or $parsedVersion.Revision -ge 0) {
        throw "MuesliVersion '$version' must be a three-part numeric release version."
    }
    if ([string]::IsNullOrWhiteSpace($channel) -or $supportedEnvironments.Count -eq 0) {
        throw "Muesli release channel and supported environments must not be empty."
    }
    if ([string]::IsNullOrWhiteSpace($metadataFileName)) {
        throw "MuesliReleaseMetadataFileName must not be empty."
    }

    [pscustomobject]@{
        Version = $version
        Channel = $channel
        MinimumWindowsVersion = $minimumWindowsVersion
        SupportedEnvironments = $supportedEnvironments
        MetadataFileName = $metadataFileName
    }
}
