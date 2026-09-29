# Post-meeting automation contract

Muesli for Windows can optionally export Markdown and run one user-selected Windows executable after a meeting becomes durable. Both features are disabled by default. Enabling either feature affects only future documented completion events; existing meetings are never replayed.

## Events that can run automation

Production automation runs only after the meeting record has been saved successfully and the recording journal has acknowledged that persistence:

- `recording-completed`: a recording stopped and reached the `Completed` state.
- `recovery-completed`: an interrupted recording was recovered and reached the `Completed` state.

Opening, viewing, editing, copying, exporting manually, or re-summarizing a meeting does not run automation. Imported media is not a hook completion event. `manual-test` is available only from Settings, uses synthetic metadata, and never includes transcript or notes.

An automation failure occurs after the durable save and cannot roll back, delete, or invalidate the meeting.

## Executable boundary

The hook path must be an absolute path to an existing `.exe` selected by the user. Muesli creates that exact application directly in a suspended state, attaches it to the kill-on-close Job Object, and only then resumes it. There is no interpreter, shell command string, meeting-derived argument, or shell expansion. The JSON payload is written as UTF-8 to standard input after successful Job assignment and then standard input is closed.

Each attempt has a user-configurable timeout of 1–600 seconds. The retry policy is bounded to 1–3 total attempts. Cancellation, including app shutdown, terminates the process and its descendants through a Windows Job Object. Descendants are also terminated when the main hook process exits, so inherited output handles cannot keep an attempt alive. Standard output and standard error are drained concurrently into fixed-size buffers; retained diagnostics are truncated when necessary.

## JSON payload, schema version 1

All documented fields are present. Nullable fields are encoded as `null`; they are not silently repurposed.

```json
{
  "schemaVersion": 1,
  "event": {
    "name": "meeting.completed",
    "trigger": "recording-completed",
    "occurredAtUtc": "2026-08-03T12:34:56.789+00:00"
  },
  "meeting": {
    "id": "meet_123",
    "title": "Project sync",
    "createdAtUtc": "2026-08-03T11:30:00.000+00:00",
    "durationMs": 3600000,
    "wordCount": 7421,
    "template": "Standard Meeting Notes",
    "folderId": null,
    "recoveredFromInterruption": false
  },
  "transcript": {
    "policy": "metadata-only",
    "path": null,
    "data": null
  },
  "notes": {
    "generated": "## Summary\n…",
    "manual": "Follow up on the launch date."
  },
  "warnings": [],
  "completion": {
    "state": "Completed"
  },
  "export": {
    "path": null,
    "ownership": "none"
  }
}
```

Field semantics:

- `schemaVersion`: integer contract version. Consumers must reject unsupported future versions rather than guessing.
- `event.name`: `meeting.completed` for production or `meeting.hook.test` for the Settings test action.
- `event.trigger`: `recording-completed`, `recovery-completed`, or `manual-test`.
- `event.occurredAtUtc`: time the durable completion was dispatched, not a derived recording end time.
- `meeting`: saved meeting identity and non-audio metadata. `createdAtUtc` is the recording start time.
- `transcript.policy`: `metadata-only`, `inline`, or `auto-export-path`.
- `transcript.data`: populated only for the explicit `inline` policy.
- `transcript.path`: populated only for `auto-export-path` and only after automatic Markdown export succeeds.
- `notes.generated` and `notes.manual`: the two separately owned notes fields from the saved meeting. The manual test sends both as empty strings.
- `warnings`: saved meeting health warnings.
- `completion.state`: the saved meeting completion state. Production dispatch requires `Completed`.
- `export.path`: successful automatic Markdown path when export is enabled; otherwise `null`.
- `export.ownership`: `none` when no automatic file was published, otherwise `userSelectedDestination`. Files in that destination belong to the user; Muesli does not delete or overwrite them.

Audio paths, audio data, provider credentials, environment secrets, and API keys are never payload fields. The executable's output is treated as untrusted. For production meetings that contain transcript or notes content, retained stdout/stderr is replaced with a redaction marker rather than attempting unsafe partial matching across a truncation boundary. The synthetic metadata-only test can retain bounded output after secret-pattern redaction.

## Automatic Markdown and PDF export

Automatic export supports Notes, Transcript, or Full meeting content through the existing deterministic renderer. The destination must be an absolute, user-selected directory. Muesli may create that directory, but does not claim ownership of it. Markdown and PDF are independent requested formats: either, both, or neither may be enabled, and each publishes its own artifact with its own diagnostics.

Writes use exclusive collision-safe reservation and atomic completion. Existing files are never overwritten. A hidden `.muesli-automation` directory in the selected destination contains a versioned ownership marker plus hashed, versioned claim/manifest control files; the control key includes the requested format, so Markdown and PDF for the same meeting/event/mode never share a claim or manifest. Muesli creates this directory atomically; if a directory with that name already exists without the valid marker, export fails closed and leaves it untouched. Muesli may recover, replace, or remove only files it proved it created inside that owned namespace, plus hidden temporary files successfully created by the current run; published `.md`/`.pdf` files and every other destination entry remain user-owned. The manifest closes the crash window between publishing a document and persisting per-meeting diagnostics, while an exclusive claim makes concurrent runs converge on one file. A prior successful path is reused only when its actual content hash matches the newly rendered mode. Transient write/publish failures and nonzero hook exits use the bounded retry policy; invalid configuration, timeout, user/app cancellation, and a successful result are never retried. Failures remain visible in the meeting's automation diagnostics and never affect the saved meeting.

PDF rendering is release-gated on the QuestPDF Community-license decision (EXP-01). While that gate is closed, a requested automatic PDF fails closed with a named reason and no file is created; the setting is also disabled in Settings with an honest explanation.

## Per-meeting diagnostics

Each completed meeting stores the latest automation result separately from capture health warnings: event/finish time, hook state, attempts, exit code, timeout/cancellation state, bounded redacted stdout/stderr, truncation flags, Markdown export state/path, optional PDF export state/path, and a redacted failure category. The WinUI meeting detail renders this result in an "After-meeting automation" card with the retained hook output behind a disclosure; the card is hidden until a run exists. The Settings test action reports its result only in Settings and does not modify any meeting.
