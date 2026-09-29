# Phase 8 context transfer — recorded media and meeting library

Written 2026-08-02. Hand this to the next session so it can continue without re-deriving state.

## Where the build stands

- Debug build: clean, 0 errors, 0 CS warnings (only offline-NuGet `NU1900`).
- `dotnet test windows-native\Muesli.Windows.Tests\Muesli.Windows.Tests.csproj --no-restore` → **337/337**.
- Fresh-log gate passing: no `ERROR`, `Unhandled UI exception`, or `XamlParseException`.
- Settings schema **5**; meeting record schema **4**; session-journal schema **3**.

## Phase 8 work already done

### Codec qualification (complete)

`Services/MediaImportFormats.cs` is the single source of truth for importable media. The list was
established empirically, not from documentation: real human-speech fixtures were transcoded with
ffmpeg from the multilingual reference audio and decoded through the app's own decoder
(`NAudio.AudioFileReader`), comparing decoded duration against the source WAV.

| Format | Result |
|---|---|
| wav, m4a, mp4, mov | decoded, 0.000 s drift |
| mkv, webm (Opus) | decoded, 0.007 s drift |
| aac | decoded, 0.027 s drift |
| mp3 | decoded, 0.096 s drift |
| **ogg (Vorbis)** | **throws on open — Windows ships no Vorbis decoder** |

`.ogg` was previously advertised in the import dialog and has been removed. Unsupported files now get
specific guidance (reason + convert-to-WAV/MP3 + "your original is never modified or deleted"), and are
rejected **before** any decode is attempted.

### Export (complete)

- `MeetingExporter.Export` now returns `MeetingExportResult` (cancelled / success / failed). It
  previously swallowed every failure into `Debug.WriteLine`, so a failed export looked identical to a
  successful one. `MainWindow.RunExport` surfaces failures with a dialog stating the saved meeting is
  unchanged.
- **Manual notes are now included in exports.** They were added in Phase 7 but the exporter predated
  them, so a user's own writing was silently missing from every export. Included in Notes and
  FullMeeting under `MeetingNotesDocument.ManualHeading`; deliberately excluded from Transcript-only.
- `BuildMarkdown` / `SuggestFilename` / `Write` are now `internal` so they are testable.

### Import source-file ownership (complete)

Tests prove an imported original is never treated as owned audio and survives meeting deletion, that
only `microphone.wav` / `system.wav` inside the meeting's own directory are deletable, and that path
traversal cannot escape the meeting directory.

## Test fixtures — reuse these

Generated with ffmpeg at `C:\Users\madha\ffmpeg\ffmpeg-master-latest-win64-gpl\bin\ffmpeg.exe` from
`%APPDATA%\muesli\streaming-models\sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-560ms-int8-2026-06-11\test_wavs\de.wav`
(real human speech, ~2.75 s).

Fixtures live in the session scratchpad and are **not** committed. Regenerate with the recipe in
`Phase8MediaImportTests`, or re-run the transcode: one file per advertised extension named
`speech.<ext>`, plus `speech.wav` as the duration reference.

Point the gated tests at them:

```bash
$env:MUESLI_MEDIA_FIXTURE_DIR='<fixtures dir>'; dotnet test windows-native\Muesli.Windows.Tests\Muesli.Windows.Tests.csproj --no-restore
```

Two tests are gated on that variable: every advertised format decodes real speech to the source
duration, and decoding is deterministic. **The first fails if a format is added to `Supported`
without a passing fixture** — that is the guard keeping the advertised list honest.

Other gated variables already in the suite:
- `MUESLI_STREAMING_QUALIFICATION_MODEL` — real Nemotron inference + full live session.
- `MUESLI_MULTISPEAKER_FIXTURE_SOURCE` — real multi-speaker diarization.

## What Phase 8 still needs

Ordered by risk:

1. **Import progress and cancellation.** `ImportMeeting_Click` runs the whole transcribe/diarize
   pipeline with no progress indicator and no cancel. A long video import is currently an
   uninterruptible freeze of the workflow. `CreateMeetingSummaryWithSettingsAsync` already accepts a
   token and the notes UI has a working cancel pattern (`_summaryCancellation`, `IsSummarizing`,
   `CanRetrySummary`) — copy that shape.
2. **Per-format transcription quality.** Only decode duration and determinism are proven. WER/CER per
   format, and diarization on imported media, are unverified. The fixtures above are the input.
3. **Import path features:** diarization, title generation, template selection, and notes on import
   are wired but never exercised end-to-end on an imported file.
4. **Library features never re-verified this pass:** search, date filters, folders (move / rename /
   reorder / delete), playback seek and duration, speaker alias editing. All pre-existing code.
5. **PDF pagination.** `GeneratePdf` is verified to emit a real `%PDF-` file over 1 KB; multi-page
   behaviour with a long transcript is untested.
6. **Auto-open on export** is implemented and its failure is reported separately from a write
   failure, but it has not been exercised interactively.

## Conventions worth keeping

- Picker record types need a `ToString()` override — the shared ComboBox template renders
  `SelectionBoxItem` without `SelectionBoxItemTemplate`, so records otherwise leak their
  compiler-generated form. This has bitten once already (`LiveModelChoice`).
- Schema bumps go with a migration test proving legacy records load losslessly.
- Verification protocol: kill `Muesli.exe` → build → full test run → launch visibly and foreground
  the dashboard → exercise the changed UI → inspect only the log bytes after a recorded byte marker.
- Secrets: `SettingsStore.Save` deliberately blanks resolved API keys; they live in Credential
  Manager via `SaveSecret`. Never persist or log them.
