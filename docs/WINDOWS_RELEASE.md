# Releasing Muesli for Windows

One command releases Muesli for Windows:

```powershell
.\scripts\release-windows.ps1 -Version 0.1.0
```

It checks the repository, has GitHub Actions build and verify the package, verifies what GitHub
then serves, and publishes. Nothing is built or signed on your machine.

## What a release is today

| Channel | What ships | Who it is for |
|---|---|---|
| GitHub Releases | The **unsigned developer build**, marked as a pre-release | Developers and testers with Developer Mode on |
| Microsoft Store | The Store package, signed by Microsoft after certification | Everyone else |

There is no code-signing certificate, so the GitHub build cannot be installed by double-click.
Each release carries `Install-MuesliDeveloperBuild.ps1`, which unpacks and registers the package
for the current user. The package keeps the development identity (`Muesli.Windows`,
`CN=AppPublisher`) and sends no usage analytics.

When a certificate exists, `scripts/build-signed-release.ps1` already packages, signs and verifies
a production MSIX; the release workflow gains a signing step and the GitHub build stops being a
pre-release. Until then the release script refuses a package that carries a production publisher.

## Prerequisites

- Windows PowerShell 5.1 or later, `git`, and the GitHub CLI (`gh auth status` must pass) with
  write access to the repository.
- The version is set and merged (see below), and `docs/release-notes/<version>.md` is written.
  Notes are hand-written, in the same style as the macOS app: a `##` heading, bold-lead bullets,
  then `### A few details`. The install steps and checksums are appended automatically.
- A clean checkout of `main`, level with `origin/main`, whose CI (`ci-gate`) passed.

## Setting the version

The version lives in `Directory.Build.props` (`MuesliVersion`) and the manifest. `main` is
protected, so the bump goes through a pull request:

```powershell
.\scripts\release-windows.ps1 -Version 0.1.1 -SetVersion
```

Write `docs/release-notes/0.1.1.md`, commit both on a branch, and merge the pull request. Tags
are `v<version>`.

## Rehearsing

```powershell
.\scripts\release-windows.ps1 -Version 0.1.0 -DryRun
```

A dry run does everything except create a tag or a release: preconditions, the full build and
gates on GitHub Actions, then a download and verification of the result into
`artifacts\release-dry-run\<version>`. It works from any pushed branch. A pull request that
touches the release tooling also runs the workflow as a dry run.

## What each stage does

1. **Preconditions** (local). Version set consistently, release notes present, clean tree on
   `main` level with `origin/main`, CI passed on the commit, no tag or release for the version.
   A manifest with a development-slot identity is refused.
2. **Build and verify** (`.github/workflows/windows-release.yml`). Runs the unit tests, builds
   the Release x64 MSIX through `rehearse-windows-release.ps1`, runs the smoke checks and
   `qualify-windows-release.ps1`, then `stage-release-assets.ps1` checks the package identity
   and writes the assets with `SHA256SUMS.txt`. A second job creates a **draft** pre-release
   with those assets. No tag exists yet.
3. **Verify** (local). Downloads the draft's assets from GitHub, checks every file against
   `SHA256SUMS.txt`, and re-checks the package identity and version.
4. **Publish** (local). Publishes the draft, which creates the tag at the built commit, and
   prints the release URL, tag and asset checksums.

Every stage stops the release on failure, with a message saying what to fix. Only stage 4 makes
anything public.

## Microsoft Store

```powershell
.\scripts\release-windows.ps1 -Version 0.1.0 -StoreSubmission draft
```

`draft` uploads the Store package to a draft submission in Partner Center for you to submit by
hand; `submit` also submits it for certification. The workflow builds the Store package with the
existing Store workflow and hands it over with the Microsoft Store Developer CLI.

This path needs, once:

- The first submission made by hand in Partner Center (listing, screenshots, age rating, pricing,
  and the justification for `runFullTrust` and `unvirtualizedResources`). The API only updates a
  product that already has one.
- A Microsoft Entra app registration linked to the Partner Center account, with these repository
  secrets: `PARTNER_CENTER_TENANT_ID`, `PARTNER_CENTER_SELLER_ID`, `PARTNER_CENTER_CLIENT_ID`,
  `PARTNER_CENTER_CLIENT_SECRET`.

The Store jobs have not been run yet: no secrets or first submission existed when they were
written. Use `draft` the first time. Certification takes hours to days and is followed in
Partner Center, not by this script. See [the Store package guide](WINDOWS_STORE_RELEASE.md).

## Recovering from a failed release

| Where it stopped | State | What to do |
|---|---|---|
| Preconditions | Nothing happened | Fix what the message names and run the command again |
| Workflow failed | No draft, no tag | Open the run it links, fix and merge, run again |
| Draft created, verification failed | Draft exists, not public | Delete the draft on the Releases page, then run again |
| Draft created, script interrupted | Draft exists, not public | Run the same command again: it finds the draft for this commit, verifies and publishes it |
| Published | Public release and tag | Do not move the tag. Fix forward with the next version |

A draft for the version that the release did not create (different commit or different assets)
is never touched; the script stops and asks you to delete it or pick another version.
