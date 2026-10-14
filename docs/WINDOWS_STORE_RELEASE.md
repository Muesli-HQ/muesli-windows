# Microsoft Store package build

The selected release path is a Microsoft-hosted MSIX for **Muesli: Voice & Meeting Notes**.
The app is reserved in Partner Center; it is not published. Microsoft signs the MSIX after
certification, so the build requires no paid signing service, certificate, PFX, or signing secrets.

## Reserved identity

Public Store metadata is checked into `windows-native/store-identity.json`:

| Field | Partner Center value |
|---|---|
| Store ID | `9NL9X2427H2F` |
| Package identity name | `MuesliHQ.MuesliVoiceMeetingNotes` |
| Publisher | `CN=50620BEA-CDFB-42FB-B7EB-F76699F9D797` |
| Publisher display name | `Muesli HQ` |
| Reserved display name | `Muesli: Voice & Meeting Notes` |
| Package family name | `MuesliHQ.MuesliVoiceMeetingNotes_jjkgdeby5rbtr` |

These values come from [Product identity in Partner Center](https://partner.microsoft.com/en-US/dashboard/products/9NL9X2427H2F/identity).
The packager supplies a temporary Store manifest to MSBuild before resource compilation, including
both package and application display names. The resulting `resources.pri` therefore uses the Store
resource-map identity. It does not modify the checked-in development manifest or register a Store package
on the developer's computer. Local development continues through `scripts/run-windows.ps1`.

## Build in GitHub Actions

Workflow: **Windows Store package**, `.github/workflows/windows-store.yml`.

It runs on PRs, pushes to main or `codex/windows-store-release`, and manual dispatch once the
workflow is available on the default branch. It checks out the exact event commit, uses pinned
actions and the .NET SDK in `global.json`, and has read-only repository permissions.

No GitHub environment or signing credentials are needed. The current shared-core lock ships the
managed fallback; the optional `MUESLI_SHARED_REPO_TOKEN` is required only if a future lock needs
the pinned private Swift repository and the built-in GitHub token cannot read it.

The job:

1. Validates the shared-core lock and installs any required pinned Swift dependencies.
2. Runs cross-platform Store manifest and test-result gate tests.
3. Runs the Release Windows product tests and records the TRX, including skipped tests.
4. Builds the unsigned x64 MSIX and normalizes its payload. Main app and indicator carry their
   .NET runtimes; Windows App SDK and required app-local C++ runtime files are checked explicitly.
5. Checks the exact Store identity, version, architecture and display names, and dumps the compiled
   PRI with MakePri to verify its resource-map name matches the manifest. It also checks native-runtime
   inventory, notices/licenses, and absence of foreign-RID/CUDA/debug artifacts.
6. Uploads the verified package and its SHA-256/evidence as `windows-store-package-<commit SHA>`.
   Failed runs upload diagnostics separately and never publish a verified package artifact.

This workflow builds an uploadable artifact. It does not claim Windows UI qualification, run the
Windows App Certification Kit, submit to Partner Center, or publish the app. Those are later gates.

## Download the uploadable package

Open the successful workflow run, download `windows-store-package-<commit SHA>`, and extract it.
The archive contains one canonical package named:

`Muesli.Windows.WinUI_<version>_x64.msix`

Upload **that MSIX**, not GitHub's enclosing artifact ZIP, to Partner Center's Packages section.
MSIX is accepted directly; this pipeline does not generate or require an `.msixupload` wrapper.
The filename keeps the build project's name; the Store identity inside it is checked separately.

Evidence included with the artifact:

- `store-submission-report.json`: Store ID/identity, source commit, version, final package SHA-256,
  test counters and explicit outstanding certification/interactive gates.
- `release-hashes.json` and `package-content-inventory.json`: final hashes and payload inventory.
- `msix-smoke-report.json`, `store-pri-dump.xml`, `native-runtime-inventory.json`, and `muesli-win32-manifest.xml`.
- `test-results/windows-tests.trx`: actual Windows test outcomes; skipped tests remain visible.

GitHub may trim the shared `artifacts/` prefix in the archive; locate files by name. Verify the MSIX
hash against `store-submission-report.json` before uploading. An unsigned submission artifact is
not an installer for public website downloads; Store certification provides distribution signing.

## Build locally on Windows

From a clean, committed checkout with the pinned .NET SDK and Windows SDK/C++ Build Tools:

```powershell
.\scripts\build-store-release.ps1
```

Output: `artifacts/store/Muesli.Windows.WinUI_<version>_x64.msix` and the reports above.
The wrapper always runs product tests, verifies their TRX and payload checks, and rejects a dirty
checkout. For fast credential-free manifest/gate tests on Windows or macOS PowerShell:

```powershell
.\scripts\tests\test-store-packaging.ps1
```

The lower-level `package-winui-msix.ps1 -Store` only packages; use the wrapper/workflow for release
evidence. Plain `package-winui-msix.ps1` and the general Windows CI retain development identity.
Keep `MuesliVersion` and the development manifest version in sync. Store versions use four numeric
components with the final component zero; increase the product version for later submissions.

## After the build stage

Before certification, qualify packaged behavior on Windows and run the Windows App Certification
Kit. The first submission also needs pricing/markets, properties, age ratings, real screenshots,
privacy/support URLs, and a Store listing. Explain `runFullTrust` (native desktop integration,
global dictation hotkeys, WASAPI audio, indicator companion) and `unvirtualizedResources` (preserving
local profile/history across upgrades/uninstall) in the restricted-capability submission fields.
Approval of those capabilities is Microsoft's decision, not a claim made by this build.

Keep publication manual during initial certification. Do not publish or change the website button
until the Store-delivered build is qualified. No Store API credentials are needed for manual upload.

## References

- [Microsoft Store MSIX package requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/app-package-requirements?pivots=store-installer-msix)
- [Create an MSIX submission](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/create-app-submission?pivots=store-installer-msix)
- [Certification process](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-certification-process)
