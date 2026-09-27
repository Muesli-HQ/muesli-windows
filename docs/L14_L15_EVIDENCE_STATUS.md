# L14 / L15 evidence status

**Phase 2 is not qualified.** Ledger rows DIC-01, DIC-02, DIC-03, HOT-01, HOT-02,
TXT-01, TXT-02, and API-03 stay **Implemented with verification debt**. This
slice is evidence *setup*: schema-2 template, rehearsal tooling, and honest
failure paths. It is not a pass report.

| Field | Value |
|---|---|
| Branch | `agent-b-l14-l15-dictation-evidence` |
| Base | `efa961c` (`codex/wave0-launch-foundation`) |
| Status | Qualification pending / Blocked on human |
| Four apps passed | **None** (Notepad, Chrome, Office, Other all unrun) |
| Ledger update | **Not warranted** |
| Live Muesli paste run | Not performed (no app-code change; no fresh human traces) |

## Capability IDs

- L14: DIC-01, TXT-01, TXT-02
- L15: DIC-02, DIC-03, HOT-01, HOT-02, API-03

## Artifacts

| Path | Role |
|---|---|
| `docs/L14_L15_EVIDENCE_STATUS.md` | This status (not a pass) |
| `qualification/dictation-corpus/windows-dictation-human-qualification.template.json` | Schema-2 placeholder slots |
| `qualification/dictation-corpus/README.md` | Human corpus fill instructions |
| `qualification/dictation-targets/README.md` | Four-app and manual-matrix protocol |
| `scripts/rehearse-dictation-qualification.ps1` | Dry-run that must report `qualified=false` |
| `windows-native/Muesli.Windows.Tests/DictationCorpusManifestTests.cs` | Schema-2 / rejection coverage |

No WAV files were checked in. No `reviewedBy` / `reviewedAt` values in the
template are filled. Do not copy model output into references.

## Commands run this slice

Template validation (expected failure):

```powershell
.\scripts\test-transcription-corpus.ps1 `
  -ManifestPath .\qualification\dictation-corpus\windows-dictation-human-qualification.template.json `
  -ValidateOnly
```

Result: exit 1. Every case failed with `placeholder case is not human-reviewed
evidence`. No `validationPassed: true`.

Rehearsal:

```powershell
.\scripts\rehearse-dictation-qualification.ps1
```

Result: `qualified=false`, `fourAppPassed=[]`. Steps that failed as required:

- template-validate-only → placeholder rejection
- empty-reference → `reference transcript is empty`
- model-only-reference → `model-only references are rejected`
- placeholder-trace-id → TraceId must be 12-character hex
- missing-fresh-trace → no fabricated trace accepted
- suite-missing-reports → missing report path rejected

Unit tests (after locally copying gitignored `Models/` sources so the WPF
project could compile; see bug L14-L15-B1):

```powershell
dotnet test windows-native\Muesli.Windows.Tests\Muesli.Windows.Tests.csproj `
  --filter FullyQualifiedName~DictationCorpusManifestTests
```

Result: 6 passed, 0 failed.

Live `qualify-dictation-corpus.ps1` and `qualify-dictation-target.ps1` against
a real dictation were **not** run. That would require human WAVs and four
fresh paste traces. Placeholder TraceIds are rejected.

## Exact commands a human must run next

1. Record WAVs into `qualification/dictation-corpus/audio/` for every template
   slot. Listen to each complete take.
2. Write `qualification/dictation-corpus/references/<id>.txt` by hand for every
   `transcript` case. Keep `silence` and `background-noise` as `no-speech`
   without a text reference. Dictionary cases need a name, a multi-word phrase,
   and a replacement already present in the Windows dictionary.
3. Accent: only languages/accents advertised by the selected model. For
   `parakeet-v3` that is "25 European languages · automatic".
4. Copy the template to
   `qualification/dictation-corpus/windows-dictation-human-qualification.json`,
   remove every `"placeholder": true`, set real `reviewedBy` / `reviewedAt` /
   `referenceProvenance`.
5. Validate:

```powershell
.\scripts\test-transcription-corpus.ps1 `
  -ManifestPath .\qualification\dictation-corpus\windows-dictation-human-qualification.json `
  -ValidateOnly
```

6. CPU + CUDA corpus (CUDA missing is a failed CUDA gate, not a CPU pass):

```powershell
.\scripts\qualify-dictation-corpus.ps1 `
  -ManifestPath .\qualification\dictation-corpus\windows-dictation-human-qualification.json `
  -ModelId parakeet-v3 `
  -Runs 3
```

7. Launch Muesli **visibly** with the dashboard open. Set paste behavior to
   `active-app`. Dictate into Notepad, Chrome, one Office app, and one other
   editor. Visually compare the entire pasted text with what was spoken. Copy
   each fresh 12-character hex `trace=` id from `%APPDATA%\muesli\logs\muesli-*.log`.
   Do not put transcript text or window titles in logs or report arguments.

```powershell
.\scripts\qualify-dictation-target.ps1 `
  -TargetKind Notepad `
  -TargetProcess notepad `
  -TraceId <fresh-12-char-hex-trace-id> `
  -RequiredModelId parakeet-v3 `
  -MaxReleaseToPasteMs 3000 `
  -ReviewedBy <human-reviewer> `
  -ReviewedAt (Get-Date) `
  -TextVerified `
  -OutputPath .\artifacts\benchmarks\dictation-targets\notepad.json
```

Repeat for Chrome, Office, and Other, then:

```powershell
.\scripts\qualify-dictation-target-suite.ps1 `
  -ReportPaths `
    .\artifacts\benchmarks\dictation-targets\notepad.json, `
    .\artifacts\benchmarks\dictation-targets\chrome.json, `
    .\artifacts\benchmarks\dictation-targets\office.json, `
    .\artifacts\benchmarks\dictation-targets\other.json `
  -OutputPath .\artifacts\benchmarks\dictation-targets\summary.json
```

## L15 manual matrix (not executed)

Document pass/fail with reviewer identity only. Do not paste transcript text
into notes. Owner: later dictation implementation / Agent E for production
fixes in locked files.

| Exercise | Capability | This slice |
|---|---|---|
| Dashboard copy | DIC-03 | Not executed |
| Dashboard delete | DIC-03 | Not executed |
| Date filter (all / 2 days / week / 2 weeks / month / 3 months) | DIC-03 | Not executed |
| Search | DIC-03 | Not executed |
| Alternate keyboard layouts | HOT-01 | Not executed |
| Custom modifiers (`Ctrl+Shift+Space`, `Ctrl+Alt+D`, typed custom) | HOT-01 | Not executed |
| F-key conflicts (F8–F12 vs OS/app shortcuts) | HOT-01 | Not executed |
| Elevation / UIPI (elevated target, unelevated Muesli) | API-03 / HOT-01 | Not executed |
| Target closure during transcription | DIC-02 | Not executed |
| Clipboard-only mode | DIC-02 | Not executed (cannot pass the four-app active-app gate) |
| Clipboard restore after successful paste | DIC-02 | Not executed |
| Escape cancel during capture | HOT-01 | Not executed |
| Escape cancel during transcription | HOT-01 | Not executed |
| Hands-free double tap | HOT-02 | Not executed |
| Original target regains focus | DIC-02 | Not executed |
| Failed paste leaves recoverable text | DIC-02 | Not executed |

## Product bugs (separate from evidence)

These are not greened over. Locked production files were not edited.

### L14-L15-B1 — `models/` gitignore hides WPF model sources

- **Capability:** TEST-01 / build hygiene (blocks L14 unit-test compile on a clean tree)
- **Blocks qualification scripts?** No
- **Blocks xunit on a clean worktree?** Yes
- **Repro:** `git worktree add` from `efa961c`, then `dotnet build windows-native\Muesli.Windows\Muesli.Windows.csproj`
- **Expected:** compile
- **Actual:** 42 errors (`DictationItem`, `MeetingItem`, `ModelsView`, `LiveModelChoice` missing)
- **Cause:** `.gitignore` pattern `models/` matches `windows-native/Muesli.Windows/Models/` and `Features/Models/`. Those sources exist on the developer machine but are untracked.
- **Owner:** Integration / L00. Narrow the ignore (for example a repo-root `models/` cache only) and force-add the WPF files. Do not treat a local copy as tracked evidence.

### L14-L15-B2 — history Copy toast includes transcript text

- **Capability:** DIC-03 / PRIV-01
- **Blocks four-app paste qualification?** No (UI toast, not a log/report argument)
- **Repro:** Dictations page → Copy on a row
- **Expected:** toast confirms copy without putting transcript text into diagnostics
- **Actual:** `CopyDictation_Click` calls `Show("Copied", item.Text, ...)`. Row-click copy uses a generic body and is safer.
- **Owner:** Agent E / later dictation slice. File is `FeatureRuntime.Dictations.cs` (locked this slice).

## What this slice did not do

- Did not mark Phase 2 qualified
- Did not promote ledger or parity rows to Complete and verified
- Did not check in dummy WAVs or fake reviewer identities
- Did not start L16 device recovery or L17 text-normalization
- Did not run L10 seven-model CPU qualification
- Did not claim CUDA qualification
