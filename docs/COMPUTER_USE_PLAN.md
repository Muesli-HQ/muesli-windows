# Computer Use on Windows — build plan

Goal: match the macOS Computer Use feature (`muesli-macos`, `ComputerUse*.swift`): the user holds a
shortcut, speaks a command ("open Notepad and write a shopping list", "change the dictation model to
Parakeet"), and Muesli drives the desktop step by step until it is done.

## How macOS does it (what we port)

- **One tool call per step.** Each step sends the planner one stateless request: the command, the
  latest window state, and the outcomes of earlier steps. The planner answers with exactly one tool
  call. Muesli executes it, observes again, and repeats until `finish` or `fail`.
- **Window state** = app name, window title and frame, the focused element, selected text, an
  optional screenshot, and up to 80 elements (`e1`, `e2`, …) with role, title, label, value,
  enabled, frame and available actions.
- **Tool catalog `muesli-cua-tools-v1`** (26 tools): `list_apps`, `launch_app`, `list_windows`,
  `get_app_state`, `click_element`, `click_point`, `move_cursor`, `perform_secondary_action`,
  `set_value`, `edit_text`, `type_text`, `paste_text`, `press_key`, `hotkey`, `scroll`, `drag`,
  browser tab/navigation/page tools, `finish`, `fail`.
- **Safety:** actions whose label contains words like send, delete, pay or submit, and unlabelled
  coordinate clicks, stop and ask for confirmation. Step and time limits, and a guard against
  repeating an action that changed nothing.
- **Planners:** ChatGPT account sign-in (default). macOS also offers on-device Gemma 4; Windows adds that after v1 parity.
- **Settings by voice:** "change a setting" commands go to a settings router that only touches
  registered settings, asks follow-up questions, and verifies the save.

## Design: what we do better than a straight port

Taken from the strongest agents (OpenAI Codex computer use on Windows, Microsoft UFO², browser-use,
Windows-MCP):

1. **Set-of-marks screenshot.** Interactive UI Automation elements get numbered boxes drawn on the
   screenshot, plus a short text list of the same numbers with role, name and value. The model
   picks a number; coordinates are only a fallback for controls that UI Automation cannot see.
2. **Multi-action steps.** The planner may return several tool calls at once (click field, type,
   press Enter). They run in order and stop at the first failure or unexpected window change.
3. **Conversation memory.** Earlier tool calls and their results are sent back each step, with only
   the latest screenshot, so the model keeps its own plan instead of re-deriving it.
4. **Native paths first.** Apps launch from the Start-menu app list; the model is told to prefer
   keyboard shortcuts and the browser address bar over hunting for pixels.
5. **`wait_for`.** Waits inside one tool call for a window, text or element to appear.
6. **OpenAI's Windows safety rules.** Never touch terminals, the Run dialog, the Windows key,
   password managers or security/privacy settings; confirm before sending, deleting, paying,
   installing or changing account access.

## Modules

Each module ends with a test that proves the feature works on this PC before the next one starts.

| # | Module | What it builds | Done when |
|---|---|---|---|
| 1 | Observation | UIA snapshot of the target window: named elements with ids, roles, values, actions, focus, selection, window info, optional screenshot. Never crashes on elements that lack properties; password values are never read. | Live probe on Notepad, Calculator, Explorer and Chrome returns labelled elements; unit tests pass. |
| 2 | Actions | Apps and windows (list, launch, focus), click element/point, move cursor, secondary actions, set value, type text, paste text with clipboard restore, keys and hotkeys, scroll, drag. | Live: Calculator computes 7+5=12 by clicking; Notepad receives typed and pasted text; a hotkey and scroll work. |
| 3 | Tool catalog and runtime | The 26-tool catalog with JSON schemas, tool-call decoding and validation, the step loop, risk confirmation, change verification, repeat guard, time/step limits, cancellation, trace. | Unit tests with a scripted planner; a scripted live run opens Notepad and types a line end to end. |
| 4 | ChatGPT planner | ChatGPT account sign-in (same flow as macOS) and the Responses transport with native tool calling; the primary planner. Optional later: OpenAI API key and on-device Gemma 4. | Parsing tested on recorded fixtures; after sign-in, a spoken-free command completes live on this PC. |
| 5 | Browser | Tab list/activate/new tab, navigate, page text and query, built on UI Automation and the keyboard (works with any Chrome/Edge profile, no debugging port). | Live: opens a URL in a new tab, lists tabs, reads page text. |
| 6 | Writing (`edit_text`) | Write or rewrite an observed text field with the planner model; checks the field did not change while generating; never sends. | Live: rewrites a Notepad selection. |
| 7 | Settings by voice | Settings router over registered Muesli settings, with clarifying questions and save verification. | Unit tests; live "change the dictation model to …" changes and persists the setting. |
| 8 | App integration | Voice shortcut, shared microphone with dictation, indicator progress, confirmation and question prompts, Settings page (planner choice, Gemma download, ChatGPT sign-in), un-hide the feature, remove the old allowlist-based implementation. | Full suite green; spoken command runs end to end from the shortcut. |
| 9 | End-to-end acceptance | Scripted voice-free and spoken runs of the macOS release-note scenarios. | All scenarios pass or have a documented limit. |

## Windows-specific decisions

- **Accessibility source:** UI Automation (FlaUI, already a dependency) replaces macOS AX.
- **Modifiers:** macOS `command` maps to `Ctrl` for shortcuts; `option` maps to `Alt`.
- **Browser:** no AppleScript on Windows and no default-profile DevTools port in current Chrome, so
  browser tools use UI Automation plus the keyboard (Ctrl+L, Ctrl+T, Ctrl+Tab).
- **Browser (M5, done):** no browser-specific tools were needed. The planner opens tabs and navigates
  with ctrl+t / ctrl+l and reads pages through UI Automation (headings are marked). Verified live in
  Chrome.
- **Writing (M6, done):** no `edit_text` tool. The planner model composes the text itself and replaces
  the selection (or ctrl+a) with `paste_text`. Verified live rewriting a Notepad list.
- **Settings by voice (M7, done):** `get_muesli_settings` / `set_muesli_setting` over a fixed list of
  choice settings (models, theme, indicator, sounds, filler words, double-tap, meeting detection).
  Models that are not downloaded are refused. The dictation shortcut can be changed too, with the
  Shortcuts page's rules; a shortcut Windows refuses is rolled back.
- **App integration (M8, done):** Ctrl+Shift+F8 voice session sharing dictation's microphone (dictation
  cannot start over it; Escape cancels both). The pill shows a stop cross, the spoken command while
  the planner thinks, the current step while it acts, then the result as a green or amber notice.
  Risky steps open the approval window; `ask_user` opens a question window with suggested answers
  and a free-text box. Settings > Computer Use keeps the last 20 run traces.
- **Safety beyond macOS:** clicks named Close, Quit or Don't save ask first, and so does Enter (or a
  typed newline) in a chat composer. Keys Computer Use sends are tagged so Muesli's own shortcuts
  ignore them.
- **Acceptance (M9):** verified live with the ChatGPT planner: Spotify (play from Liked Songs, search
  and play, pause), Calculator, Chrome (Hacker News, YouTube search), File Explorer, Clock timer,
  Windows Settings, WhatsApp (read; sending asks first), copying between Chrome and Notepad, Muesli
  settings and the dictation shortcut by voice, and clarifying questions. The spoken path needs a
  person at the microphone.
- **Not built:** the on-device Gemma planner (optional), and Computer Use entries in the Timeline,
  which needs a history schema change.
- **ChatGPT sign-in** uses the same sign-in flow and endpoint as macOS, with the same risk that
  OpenAI may change it.
