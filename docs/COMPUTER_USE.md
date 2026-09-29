# Muesli Computer Use contract and threat model

This document defines version 1 of the optional Windows Computer Use subsystem. Computer Use is a separate high-risk capability. It is disabled by default and has no call path from ordinary dictation, meeting transcription, saved text, clipboard content, or post-meeting automation.

## Explicit activation

The user must enable Computer Use after selecting the OpenAI planner provider, an exact model ID, at least one allowed application, bounded limits, and an available OpenAI credential. Each voice session begins only from **Speak planner command**. That control mints a private, one-use, one-minute activation token before microphone capture. Only a transcript carrying that token can become an `ExplicitVoiceCommand`; normal dictation never receives a token and is always saved/delivered as text.

The dashboard minimizes while the planner command is recorded. The user focuses one allowlisted target and stops through the visible floating control. Muesli captures only that foreground HWND and process identity. Stop/cancel and app shutdown cancel voice capture, provider I/O, confirmation, observation, visualization, and action execution.

## Planning and execution boundary

The planner receives untrusted command text plus a versioned, privacy-minimized observation. It emits JSON schema version 1 containing `schemaVersion`, `observationId`, `completed`, and zero or one proposed action. Additional or malformed properties, unknown actions, coordinates, unallowlisted applications/domains, non-HTTPS navigation, mismatched origins, and risk downgrades are rejected.

Planning and execution are separate interfaces. A successful action is followed by a fresh observation and a fresh planner request. The previous plan is never reused after state changes. Processing stops when the planner reports completion, the configured action cap is reached, state becomes stale, a timeout/cancellation occurs, confirmation is declined, or any adapter fails.

Supported local actions are foreground focus, UI Automation `InvokePattern`, and non-password writable `ValuePattern`. Raw mouse/keyboard injection, shell commands, filesystem commands, clipboard operations, password elements, ambiguous AutomationIds, elevated/UIPI-inaccessible targets, and arbitrary coordinates are unsupported. The approved HWND remains bound to its captured PID, process name, and process start time; ownership and element resolution are checked again immediately before execution and must still resolve to exactly one non-password enabled element.

## Confirmation policy

Risk is derived locally as a minimum and cannot be cleared by the planner. Browser actions and text entry are at least external. UI Automation invocation is treated as irreversible because accessible IDs alone cannot prove semantics. Only focusing the approved window may be risk-free. Every destructive, external, financial, credential-related, irreversible, text-entry, browser, or invoke action requires a fresh modeless confirmation before execution. The confirmation keeps Stop responsive and locally previews the exact application, origin, target identifier, URL, or text value; that preview is never logged or persisted.

## Observation and privacy

The production observation contains only the approved process identity, stable approved-window identity, a state fingerprint, virtual-screen bounds, and unique non-password UI Automation IDs/control types/enabled state/bounds. It never enumerates unrelated windows or Credential Manager, and never reads clipboard history, clipboard contents, password values, element values, window titles, browser history, cookies, downloads, or page text.

Window text, browser page text, and screenshots remain unavailable until scoped masking and retention qualification passes. Their settings default off, are disabled in the UI, and prevent Computer Use from enabling if manually set. Screenshot pixels are never persisted.

## Browser boundary

Browser control is available only when the user explicitly selects **Loopback DevTools**, supplies a loopback HTTP endpoint, allowlists Chrome (`chrome`) or Edge (`msedge`) as an application, allowlists exact canonical HTTPS origins on port 443, and launches that browser with its DevTools endpoint. CDP has no trustworthy HWND-to-tab mapping, so Muesli fails closed unless the endpoint exposes exactly one page target. That page must already be on the allowed origin and its `ws://127.0.0.1` debugger URL must use the configured port. The page target, URL, and debugger identity are folded into the stale-observation fingerprint and must match again at execution; browser-reported main-frame metadata is then checked immediately before every command. Navigation remains origin-pinned; invocation requires an explicit `data-muesli-target` marker. Muesli does not use address-bar UI Automation, browser profiles, extensions, cookies, history, or arbitrary remote debugging endpoints.

## Limits, trace, and recovery

Planner timeout is clamped to 5–120 seconds, per-action timeout to 1–30 seconds, action count to 1–20, and the overall session to at most five minutes. Provider and adapter waits are bounded even if an implementation ignores cancellation. Native UI Automation runs off the WPF dispatcher and checks cancellation again before invoking a pattern, so the UI and Stop control remain responsive and a timed-out lookup cannot later begin an action.

The local trace at `%APPDATA%\muesli\computer-use\trace.json` is schema-versioned, atomic, and bounded to 50 runs and 20 actions per run. It stores only run ID/status/times and action index/kind/application/risk/outcome/time. It never stores commands, typed values, AutomationIds, element text, titles, URLs, screenshots, provider bodies, credentials, or exception text. Failures leave ordinary dictation, meetings, settings, and the target application recoverable; no automatic retry executes another action.
