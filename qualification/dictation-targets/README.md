# L15 four-app paste protocol (operator kit)

This folder is the Windows Phase 2 paste-target kit. It is **not** passing
evidence. Each report must come from a fresh real dictation whose 12-character
hex `trace=` id is copied from `%APPDATA%\muesli\logs\muesli-*.log`. Do not
fabricate TraceIds. Do not put expected or observed transcript text, or window
titles, in logs or report arguments.

Authoritative protocol: `docs/PHASE2_DICTATION_QUALIFICATION.md`.
Current status: `docs/L14_L15_EVIDENCE_STATUS.md`.

## Before any live paste run

1. Launch Muesli visibly (not hidden) with the dashboard open.
2. Confirm dictation model identity is `parakeet-v3`.
3. Set paste behavior to `active-app` (clipboard-only cannot pass this gate).
4. Focus the target app, dictate, visually compare the entire pasted text with
   what was spoken, then run the qualifier within two hours.

Suggested targets:

| TargetKind | TargetProcess | Notes |
|---|---|---|
| Notepad | `notepad` | Classic Notepad. |
| Chrome | `chrome` | Focus a text field first. |
| Office | `winword`, `excel`, `powerpnt`, `outlook`, or `onenote` | Any one Office process. |
| Other | for example `notepad++`, `Code`, `win32calc` | Must not be notepad/chrome/Office. |

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

The gate requires active-app delivery, original-app foreground restoration, an
explicit native engine/model identity, a non-empty result, human paste
verification within two hours, durable history, and release-to-paste ≤ 3000 ms.

## Manual matrix (document results; do not fake passes)

Exercise and record pass/fail plus reviewer identity. Do not paste transcript
text into notes.

- Alternate keyboard layouts
- Custom modifiers (`Ctrl+Shift+Space`, `Ctrl+Alt+D`, and a typed custom gesture)
- F-key conflicts (F8–F12 vs OS/app shortcuts)
- Elevation / UIPI mismatch (elevated Notepad while Muesli is unelevated)
- Target closure during transcription
- Clipboard-only mode
- Clipboard restore after a successful paste
- Escape cancel during capture
- Escape cancel during transcription
- Hands-free double tap (lock, then stop)
- Original target regains focus
- Failed paste leaves recoverable text (clipboard or history)
- Dashboard history copy, delete, date filter, and search

## Rehearsal without a live dictation

```powershell
.\scripts\rehearse-dictation-qualification.ps1
```

Rehearsal must report `qualified=false`. It does not create passing target
reports.
