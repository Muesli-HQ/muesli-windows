<#
.SYNOPSIS
    Releases Muesli for Windows: one command that checks, builds, verifies and publishes.

.DESCRIPTION
    The release is built on GitHub Actions (.github/workflows/windows-release.yml), never on this
    machine. This script is the single entry point around it:

      1. Preconditions: clean tree, on main and level with origin/main, the version in
         Directory.Build.props and Package.appxmanifest equals -Version, release notes exist,
         CI passed on this commit, and no tag or release for the version exists yet.
      2. Starts the release workflow for this commit and waits for it. The workflow runs the
         tests, builds the Release x64 MSIX, runs the package gates, stages the assets with
         checksums and creates a DRAFT GitHub release.
      3. Downloads the draft's assets, verifies them against SHA256SUMS.txt and re-checks the
         package identity.
      4. Publishes the draft (which creates the tag) and prints what was published.

    Any failed step stops before the release is public. A draft that already exists for the
    version, made by an earlier run of this script, is verified and published instead of rebuilt,
    so rerunning the same command recovers a release that stopped after step 2.

    What this channel ships today is the UNSIGNED developer build, published as a pre-release.
    There is no code-signing certificate; a signed build reaches ordinary users through the
    Microsoft Store (-StoreSubmission). See docs/WINDOWS_RELEASE.md.

    Runs under Windows PowerShell 5.1. Needs git and an authenticated GitHub CLI (gh).

.PARAMETER Version
    Three-part version, for example 0.3.0. The tag is v<Version>.

.PARAMETER DryRun
    Runs the preconditions and the whole build and verification on GitHub Actions, downloads the
    result to artifacts\release-dry-run\<Version> and verifies it. Creates no tag and no release.
    Allowed from any pushed branch.

.PARAMETER SetVersion
    Writes -Version into Directory.Build.props and Package.appxmanifest and stops. Commit that
    through a pull request; main is protected, so this script never pushes to it.

.PARAMETER StoreSubmission
    none (default), draft (upload the Store package to a Partner Center draft submission) or
    submit (upload and submit for certification). Needs the Partner Center secrets.

.PARAMETER Yes
    Do not ask for confirmation before starting the workflow or publishing.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$DryRun,
    [switch]$SetVersion,
    [ValidateSet('none', 'draft', 'submit')][string]$StoreSubmission = 'none',
    [switch]$Yes
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location -LiteralPath $root
. (Join-Path $PSScriptRoot "read-release-properties.ps1")
. (Join-Path $PSScriptRoot "release-common.ps1")

$workflowFile = "windows-release.yml"
$Version = $Version.Trim().TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version '$Version' must be three numbers, for example 0.3.0."
}
$tag = "v$Version"
$names = Get-MuesliReleaseAssetNames -Version $Version

function Write-Step { param([string]$Text) Write-Host ""; Write-Host "== $Text" -ForegroundColor Cyan }

# Runs a native tool and returns its standard output. stderr is left on the console: redirecting
# it in Windows PowerShell 5.1 turns every progress line into an error record.
function Invoke-Tool {
    param([Parameter(Mandatory = $true)][string]$Tool, [string[]]$Arguments = @(), [string]$Failure = "")
    $output = & $Tool @Arguments
    if ($LASTEXITCODE -ne 0) {
        $what = if ($Failure) { $Failure } else { "'$Tool $($Arguments -join ' ')' failed" }
        throw "$what (exit code $LASTEXITCODE)."
    }
    return $output
}

function Invoke-GhJson {
    param([string[]]$Arguments, [string]$Failure = "")
    $text = (@(Invoke-Tool -Tool gh -Arguments $Arguments -Failure $Failure) -join "`n")
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    return ($text | ConvertFrom-Json)
}

function Confirm-Step {
    param([string]$Question)
    if ($Yes) { return }
    $answer = Read-Host "$Question [y/N]"
    if ($answer -notmatch '^(y|yes)$') { throw "Stopped at your request. Nothing further was done." }
}

# Replaces one value in a tracked file, keeping its encoding (the manifest has a UTF-8 BOM) and
# line endings exactly as they are.
function Set-FileText {
    param([string]$Path, [string]$Pattern, [string]$Replacement)
    $bytes = [IO.File]::ReadAllBytes($Path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $encoding = New-Object System.Text.UTF8Encoding $hasBom
    $text = if ($hasBom) { $encoding.GetString($bytes, 3, $bytes.Length - 3) } else { $encoding.GetString($bytes) }
    $regex = New-Object System.Text.RegularExpressions.Regex $Pattern
    if ($regex.Matches($text).Count -ne 1) { throw "Expected exactly one match for '$Pattern' in $Path." }
    [IO.File]::WriteAllText($Path, $regex.Replace($text, $Replacement), $encoding)
}

$propsPath = Join-Path $root "Directory.Build.props"
$manifestPath = Join-Path $root "windows-native\Muesli.Windows.WinUI\Package.appxmanifest"
$notesPath = Get-MuesliReleaseNotesPath -Root $root -Version $Version

if ($SetVersion) {
    Set-FileText -Path $propsPath -Pattern '(?<=<MuesliVersion>)[^<]*(?=</MuesliVersion>)' -Replacement $Version
    Set-FileText -Path $manifestPath -Pattern '(?<=<Identity\b[^>]*?\bVersion=")[^"]*(?=")' -Replacement "$Version.0"
    Write-Host "Set the version to $Version in Directory.Build.props and Package.appxmanifest."
    if (-not (Test-Path -LiteralPath $notesPath)) {
        Write-Host "Next: write the release notes at docs\release-notes\$Version.md."
    }
    Write-Host "Then commit both on a branch, merge the pull request, and run:"
    Write-Host "  .\scripts\release-windows.ps1 -Version $Version"
    return
}

# ---------------------------------------------------------------- 1. Preconditions
Write-Step "Preconditions for $tag$(if ($DryRun) { ' (dry run)' })"

foreach ($tool in @('git', 'gh')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "'$tool' was not found on PATH." }
}
Invoke-Tool -Tool gh -Arguments @('auth', 'status') -Failure "GitHub CLI is not signed in; run 'gh auth login'" | Out-Null

$release = Get-MuesliReleaseProperties -Root $root
if ($release.Version -ne $Version) {
    throw "Directory.Build.props has MuesliVersion '$($release.Version)', not '$Version'. Run '.\scripts\release-windows.ps1 -Version $Version -SetVersion', merge that change, then release."
}
[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
$manifestName = [string]$manifest.Package.Identity.Name
$manifestVersion = [string]$manifest.Package.Identity.Version
if ($manifestVersion -ne "$Version.0") {
    throw "Package.appxmanifest has version '$manifestVersion', not '$Version.0'. Run -SetVersion and merge that change."
}
if ($manifestName -like 'MuesliDev*' -or $manifestName -cne 'Muesli.Windows') {
    throw "Package.appxmanifest identity is '$manifestName'. A release ships 'Muesli.Windows'; a development-slot identity must never be committed."
}
Assert-MuesliReleaseNotes -Path $notesPath
Write-Host "Version $Version is set consistently; release notes found."

$tree = Assert-MuesliCleanReleaseInputs -Root $root
$head = [string]$tree.Head
$branch = [string](Invoke-Tool -Tool git -Arguments @('rev-parse', '--abbrev-ref', 'HEAD'))
if ($branch -eq 'HEAD') { throw "HEAD is detached. Check out main (or a branch, for -DryRun)." }
if (-not $DryRun -and $branch -ne 'main') {
    throw "Releases are cut from main; you are on '$branch'. Use -DryRun to rehearse from a branch."
}
Invoke-Tool -Tool git -Arguments @('fetch', '--quiet', 'origin', $branch) -Failure "Could not fetch origin/$branch; push the branch first" | Out-Null
$remoteHead = [string](Invoke-Tool -Tool git -Arguments @('rev-parse', "origin/$branch"))
if ($remoteHead -ne $head) {
    throw "Local $branch ($($head.Substring(0, 9))) is not level with origin/$branch ($($remoteHead.Substring(0, 9))). Pull or push so they match."
}
Write-Host "Clean tree on $branch at $($head.Substring(0, 9)), level with origin."

$repo = [string](Invoke-GhJson -Arguments @('repo', 'view', '--json', 'nameWithOwner')).nameWithOwner
$resumeDraft = $null
if (-not $DryRun) {
    $allReleases = @(Invoke-GhJson -Arguments @('api', "repos/$repo/releases?per_page=100") -Failure "Could not list releases")
    $sameTag = @($allReleases | Where-Object { $_.tag_name -eq $tag })
    $published = @($sameTag | Where-Object { -not $_.draft })
    if ($published.Count -gt 0) { throw "$tag is already published: $($published[0].html_url). Choose the next version." }
    if ($sameTag.Count -gt 1) {
        throw "There are $($sameTag.Count) draft releases for $tag. Delete the ones that are not wanted on https://github.com/$repo/releases and run this again."
    }
    if ($sameTag.Count -eq 1) {
        $draft = $sameTag[0]
        $assetNames = @($draft.assets | ForEach-Object { $_.name })
        $ours = ($assetNames -contains $names.Msix) -and ($assetNames -contains $names.Checksums) -and ($draft.target_commitish -eq $head)
        if (-not $ours) {
            throw "A draft release for $tag already exists that this release did not create (target '$($draft.target_commitish)', assets: $($assetNames -join ', ')). Delete it on https://github.com/$repo/releases, or choose another version."
        }
        $resumeDraft = $draft
        Write-Host "Found the draft release for $tag from an earlier run at this commit; it will be verified and published."
    }
    $tagRefs = @(Invoke-GhJson -Arguments @('api', "repos/$repo/git/matching-refs/tags/$tag") -Failure "Could not look up tags")
    if (@($tagRefs | Where-Object { $_.ref -eq "refs/tags/$tag" }).Count -gt 0) {
        throw "Tag $tag already exists on $repo. A release creates its own tag; delete the stray tag or choose the next version."
    }
}

$checks = Invoke-GhJson -Arguments @('api', "repos/$repo/commits/$head/check-runs?per_page=100") -Failure "Could not read CI results"
$gate = @($checks.check_runs | Where-Object { $_.name -eq 'ci-gate' } | Sort-Object { $_.started_at } -Descending | Select-Object -First 1)
$gateState = if ($gate.Count -eq 0) { 'has not run' } elseif ($gate[0].status -ne 'completed') { 'is still running' } else { [string]$gate[0].conclusion }
if ($gateState -ne 'success') {
    $message = "CI (ci-gate) $gateState on $($head.Substring(0, 9))."
    if ($DryRun) { Write-Warning "$message The dry run continues; a real release would stop here." }
    else { throw "$message A release needs a passing CI run on the exact commit." }
} else {
    Write-Host "CI passed on this commit."
}

# ---------------------------------------------------------------- 2. Build on GitHub Actions
$runUrl = ""
$runId = $null
if ($null -eq $resumeDraft) {
    Write-Step "Build and verify on GitHub Actions"
    if ($DryRun) {
        Write-Host "Dry run: the workflow builds and verifies the package and creates no tag or release."
    } else {
        Write-Host "The workflow will create a DRAFT pre-release $tag on $repo with the unsigned developer build."
        if ($StoreSubmission -ne 'none') { Write-Host "Microsoft Store: '$StoreSubmission' the Store package to Partner Center." }
        Confirm-Step -Question "Start the release build for $tag from $($head.Substring(0, 9))?"
    }

    $listArguments = @('run', 'list', '--workflow', $workflowFile, '--event', 'workflow_dispatch', '--limit', '20', '--json', 'databaseId,headSha,url')
    $before = @(Invoke-GhJson -Arguments $listArguments -Failure "Could not list workflow runs. Is $workflowFile on the default branch yet?" | ForEach-Object { $_.databaseId })
    $storeChoice = if ($DryRun) { 'none' } else { $StoreSubmission }
    Invoke-Tool -Tool gh -Arguments @('workflow', 'run', $workflowFile, '--ref', $branch,
        '-f', "version=$Version", '-f', "dry_run=$(([bool]$DryRun).ToString().ToLowerInvariant())", '-f', "store_submission=$storeChoice") `
        -Failure "Could not start the release workflow" | Out-Null

    for ($attempt = 0; $attempt -lt 20 -and $null -eq $runId; $attempt++) {
        Start-Sleep -Seconds 3
        $candidate = @(Invoke-GhJson -Arguments $listArguments | Where-Object { $before -notcontains $_.databaseId -and $_.headSha -eq $head } | Select-Object -First 1)
        if ($candidate.Count -eq 1) { $runId = $candidate[0].databaseId; $runUrl = [string]$candidate[0].url }
    }
    if ($null -eq $runId) { throw "The workflow was started but its run did not appear. Look at https://github.com/$repo/actions and rerun this command once it finishes." }
    Write-Host "Run: $runUrl"
    Write-Host "Waiting for it to finish (tests, package build and gates take a while)..."
    & gh run watch $runId --exit-status --interval 30 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "The release workflow failed: $runUrl`nNothing was published. Fix the failure it reports, merge the fix, and run this command again."
    }
    Write-Host "Workflow succeeded."
}

# ---------------------------------------------------------------- 3. Verify what was built
Write-Step "Verify the built assets"
$downloadDirectory = Join-Path $root ("artifacts\" + $(if ($DryRun) { "release-dry-run" } else { "release-verify" }) + "\$Version")
if (Test-Path -LiteralPath $downloadDirectory) { Remove-Item -LiteralPath $downloadDirectory -Recurse -Force }
New-Item -ItemType Directory -Force -Path $downloadDirectory | Out-Null

if ($DryRun) {
    Invoke-Tool -Tool gh -Arguments @('run', 'download', "$runId", '--name', "windows-release-$Version", '--dir', $downloadDirectory) -Failure "Could not download the workflow artifact" | Out-Null
} else {
    # Download what GitHub actually serves for the draft, not a local copy.
    Invoke-Tool -Tool gh -Arguments @('release', 'download', $tag, '--dir', $downloadDirectory, '--clobber') -Failure "Could not download the draft release assets for $tag" | Out-Null
}
$verified = @(Assert-MuesliReleaseChecksums -Directory $downloadDirectory -ChecksumsFileName $names.Checksums -RequiredFiles @($names.Msix, $names.Installer))
$identity = Assert-MuesliUnsignedReleasePackage -MsixPath (Join-Path $downloadDirectory $names.Msix) -Version $Version
Write-Host "Checksums match and the package is $($identity.Name) $($identity.Version) ($($identity.Publisher))."

if ($DryRun) {
    Write-Step "Dry run complete"
    Write-Host "Nothing was tagged or published. Verified assets: $downloadDirectory"
    foreach ($file in $verified) { Write-Host ("  {0}  {1}  ({2:N1} MB)" -f $file.Sha256, $file.Name, ($file.Bytes / 1MB)) }
    if ($runUrl) { Write-Host "Workflow run: $runUrl" }
    return
}

# ---------------------------------------------------------------- 4. Publish
Write-Step "Publish $tag"
$draftNow = @(@(Invoke-GhJson -Arguments @('api', "repos/$repo/releases?per_page=100")) | Where-Object { $_.tag_name -eq $tag })
if ($draftNow.Count -ne 1 -or -not $draftNow[0].draft) {
    throw "Expected exactly one draft release for $tag, found $($draftNow.Count). Nothing was published; check https://github.com/$repo/releases."
}
if ($draftNow[0].target_commitish -ne $head) {
    throw "The draft release for $tag targets '$($draftNow[0].target_commitish)', not this commit $head. Nothing was published."
}
Confirm-Step -Question "Publish the verified draft as pre-release $tag? This makes it public and creates the tag."
Invoke-Tool -Tool gh -Arguments @('release', 'edit', $tag, '--draft=false', '--prerelease') -Failure "Could not publish the draft. It is still a draft; rerun this command to try again" | Out-Null

$final = Invoke-GhJson -Arguments @('release', 'view', $tag, '--json', 'url,isDraft,isPrerelease,tagName,targetCommitish')
if ($final.isDraft) { throw "GitHub still reports $tag as a draft. Rerun this command." }

Write-Step "Published"
Write-Host "Release:  $($final.url)"
Write-Host "Tag:      $($final.tagName) at $head"
Write-Host "Kind:     unsigned developer build, marked as a pre-release"
Write-Host "Assets:"
foreach ($file in $verified) { Write-Host ("  {0}  {1}  ({2:N1} MB)" -f $file.Sha256, $file.Name, ($file.Bytes / 1MB)) }
if ($runUrl) { Write-Host "Workflow: $runUrl" }
if ($StoreSubmission -ne 'none') {
    Write-Host "Microsoft Store: the workflow handled the '$StoreSubmission' step; follow certification in Partner Center."
}
