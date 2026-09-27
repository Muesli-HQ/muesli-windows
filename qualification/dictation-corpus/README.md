# L14 human dictation corpus (operator kit)

This folder is the Windows Phase 2 human-corpus kit. It is **not** a passing
qualification corpus. Audio and references must be recorded, listened to, and
written by a human. Automation must not fabricate WAVs or copy model output
into a reference and label it reviewed.

Authoritative protocol: `docs/PHASE2_DICTATION_QUALIFICATION.md`.
Current status: `docs/L14_L15_EVIDENCE_STATUS.md`.

## Layout

| Path | Purpose |
|---|---|
| `windows-dictation-human-qualification.template.json` | Schema-2 placeholder slots. Must fail `-ValidateOnly`. |
| `windows-dictation-human-qualification.json` | Create this by copying the template after filling real evidence. Git will not contain dummy WAVs. |
| `audio/` | Human-recorded WAV files (`*.wav` is gitignored). |
| `references/` | Human-written `.txt` references for transcript cases only. |

Required categories, collectively: `short-command`, `paragraph`, `dictionary`
(names, multi-word phrases, and replacements), `numbers-punctuation`, `accent`
(only advertised accents/languages for the selected model), `silence`,
`background-noise`.

Default gates unless the release owner approves stricter values:

- WER ≤ 0.15
- CER ≤ 0.08
- RTF ≤ 0.20

`parakeet-v3` advertises "25 European languages · automatic". Do not add accent
cases for languages that model does not advertise.

## Filling a case

1. Record or select a real WAV for that slot.
2. Listen to the complete audio.
3. For `expectedOutcome: "transcript"`, write `references/<id>.txt` by hand.
   Model output may be a review aid only.
4. For `silence` / `background-noise`, keep `expectedOutcome: "no-speech"` and
   do not add a text reference. Set `referenceProvenance` to `human-reviewed`.
5. Set `reviewedBy` to your real name or handle and `reviewedAt` to an ISO
   timestamp. Do not use placeholder, todo, automation, or dummy identities.
6. Remove `"placeholder": true` from that case.
7. Repeat until every case is filled, then copy the template to
   `windows-dictation-human-qualification.json`.

The runner rejects empty references, model-only provenance, placeholder flags,
and missing reviewer data.

## Commands

Validate without inference (must fail on the template; must pass on the filled
reviewed manifest):

```powershell
.\scripts\test-transcription-corpus.ps1 `
  -ManifestPath .\qualification\dictation-corpus\windows-dictation-human-qualification.template.json `
  -ValidateOnly
```

After a human-reviewed manifest exists:

```powershell
.\scripts\test-transcription-corpus.ps1 `
  -ManifestPath .\qualification\dictation-corpus\windows-dictation-human-qualification.json `
  -ValidateOnly
```

Full CPU and CUDA qualification (needs prepared `parakeet-v3` and real audio).
CUDA unavailability is a failed CUDA gate, not an automatic CPU pass:

```powershell
.\scripts\qualify-dictation-corpus.ps1 `
  -ManifestPath .\qualification\dictation-corpus\windows-dictation-human-qualification.json `
  -ModelId parakeet-v3 `
  -Runs 3
```

Do not put expected or observed transcript text in logs or report arguments.
