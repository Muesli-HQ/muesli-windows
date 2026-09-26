# Muesli for Windows — Privacy

Muesli is a local-first dictation and meeting-transcription application. It does not include product telemetry or analytics in the current Windows build.

## Audio and transcripts

- Dictation audio is captured to one or more reserved `dictation-tmp-*.wav` segments. Route changes close the current segment before Windows default-device or selected-device recovery, and reserved `dictation-merged-*.wav` files are created locally only when needed. After local transcription finishes (including failure, cancellation, and no-speech paths), all temporary segments and merged files are removed; the next app start removes any reserved files left by a crash before opening the microphone. At most one `last-dictation.wav` file is retained for the local benchmark tool; retention is limited to 10 minutes and 96 MiB, and an over-limit capture removes the previous alias instead of retaining new audio.
- Meeting microphone and system audio are captured into separate, session-owned parts under `%APPDATA%\muesli\captures\in-progress\<meeting-id>\`. A redacted, versioned journal retains those parts across suspend, shutdown, capture failure, finalization cancellation, or a crash so the user can explicitly recover an interrupted recording. Empty or unusable parts are not promoted to a meeting.
- If **Save meeting recording** is enabled, Muesli stores unique `microphone.wav` and `system.wav` files under `%APPDATA%\muesli\captures\recordings\<meeting-id>\`. If it is disabled, Muesli removes session-owned meeting audio only after the meeting record is durably persisted. Cancelling a recording attempts to delete its temporary session directory; a deletion failure is reported rather than treated as success.
- Imported media remains in its original location and is user-owned. Muesli never deletes an imported source file.
- Dictation history, meeting transcripts, summaries, folders, templates, and dictionary entries are stored under `%APPDATA%\muesli\data`.

The About page includes an explicit capture-inspection action for legacy or interrupted top-level capture files. It reports file count and size and asks for confirmation. It excludes retained meeting recordings, imports, transcripts, settings, models, and the bounded latest-dictation file.

## Models and optional cloud processing

- Offline ASR and speaker-diarization models download only after an explicit user action from pinned HTTPS release URLs into `%USERPROFILE%\.cache\muesli`. Archives and every ASR file required at runtime are SHA-256 verified before native initialization. Choosing a dictation or final-meeting role does not access the network.
- Live transcription is off by default. Preparing Nemotron 3.5 and Silero is an explicit network download into `%APPDATA%\muesli\streaming-models`; preparing does not select or enable live transcription. Archive, ASR-file, and VAD SHA-256 hashes are pinned and verified. Once prepared, live preview, VAD, final ownership, and measured-gap recovery run locally and do not send audio or transcript text over the network.
- Optional Qwen/GGUF cleanup is disabled by default. Models offers explicit download and verification; existing manually placed GGUF files remain usable.
- The **local** summary provider keeps meeting summarization on the device.
- **Ollama**, **LM Studio**, and the **Custom HTTP** provider send the transcript only to the endpoint you configure. On a loopback (localhost) endpoint nothing leaves the machine; pointing any of them at a non-loopback host is disclosed in the UI as leaving the machine. LM Studio needs no credential.
- Selecting **OpenAI** or **OpenRouter** sends the full meeting transcript and the selected summary instructions to that provider. Their service terms and privacy policies then apply. Provider failures are disclosed in the UI and Muesli preserves the transcript while producing a local fallback summary.
- API keys can come from `OPENAI_API_KEY`, `OPENROUTER_API_KEY`, or `MUESLI_CUSTOM_LLM_API_KEY`. Keys entered in the UI are masked and stored in Windows Credential Manager for the current Windows user (the custom HTTP key under `custom-llm-api-key`); they are not written to `windows-settings.json`.

## Meeting detection

When enabled, Muesli examines foreground and visible-window metadata, supported meeting-app process names, and—where browser accessibility permits—meeting URLs to recognize Google Meet, Zoom, Teams, and Webex. Detection happens locally. Logs record process/platform diagnostics and hashes or redacted values rather than complete titles, URLs, meeting codes, document names, or email subjects.

For a detected meeting with a live process ID on a supported Windows build, Muesli first attempts Windows process-tree loopback for the remote-participant track. If targeted activation is unavailable or fails, the app explicitly warns that it is using render-endpoint loopback. Endpoint loopback can include unrelated sounds played through that Windows output device. Manual recordings do not claim process isolation.

## Optional post-meeting automation

Post-meeting executable hooks and automatic Markdown export are disabled by default and apply only to future completed recordings or successful recovery completions after the meeting is durably saved. Opening, editing, manually exporting, or re-summarizing a meeting does not run automation.

The user chooses an exact `.exe`; Muesli starts it directly without a command shell and sends the documented versioned JSON payload over standard input. Audio and provider secrets are never payload fields. Transcript exposure is a separate explicit setting: metadata only (default), inline transcript, or the path of a successful automatic export. Generated and manual notes are documented payload fields when the hook is enabled. See the [post-meeting automation contract](POST_MEETING_AUTOMATION.md) for the full contract.

Hook output is untrusted. When a production meeting contains transcript or notes content, retained stdout/stderr is replaced with a redaction marker; the metadata-only manual test retains only bounded secret-pattern-redacted output. Automatic Markdown files are written to a user-selected, user-owned destination with collision-safe names; Muesli does not overwrite or delete published/user files there. It owns only hidden `.muesli-automation` idempotency records and hidden temporary files, which it may recover or remove.

## Optional Computer Use

Computer Use is disabled by default and can start only from its dedicated voice-session control after explicit provider/model, application allowlist, limits, and credential configuration. Ordinary dictation text, meetings, saved records, clipboard content, and post-meeting hooks cannot enter planner mode. OpenAI receives the explicit command and a privacy-minimized description of the single approved foreground window; there is no silent provider fallback.

The observation excludes password fields, element values, titles, unrelated windows, Credential Manager, clipboard data/history, browser history, cookies, and downloads. Window text, page text, and screenshots remain disabled until a separately qualified masking implementation exists. Browser control, when enabled, uses only an explicit loopback DevTools endpoint and exact allowlisted HTTPS origins. The bounded local trace stores action categories and outcomes, never commands, values, URLs, titles, screenshots, provider bodies, credentials, or exception text. See the [Computer Use contract](COMPUTER_USE.md).

## Logs and deletion

Logs are stored under `%APPDATA%\muesli\logs`, rotate after 14 days or a bounded total size, and contain runtime state, model/provider identity, timings, process name, capture mode, route state, repair counts, redacted errors, and operational counts. Dictation does not read or hash the active window title for diagnostics. Meeting-session diagnostics do not record transcript text, meeting titles, URLs, meeting codes, or audio paths. Logs are not intended to contain transcript text, API keys, full URLs, or full window titles.

Users can delete history in the app, delete owned audio when deleting a meeting, clear stored provider keys, clear model caches, inspect legacy captures, and remove remaining Muesli data through normal Windows file-management or uninstall workflows. Uninstall does not silently delete transcripts or recordings.
