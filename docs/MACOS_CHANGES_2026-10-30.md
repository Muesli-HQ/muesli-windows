# macOS changes — full list (2026-10-30)

Every commit in `C:\Users\madha\projects\muesli-macos` (`Muesli-HQ/muesli`) since the last Windows sync. Nothing is filtered out. Hashes are macOS commits.

1. **main**: every non-merge commit from `cdcd9c6` (v0.8.5) to `2f10b23d` (276 commits). This includes `codex/buffered-live-dictation` (#677) and `codex/cotypist-imk` (#682), merged on 2026-10-30.
2. **Active branches**: every commit that is not in main, on branches that share history with the current main (7 branches).
3. **Older branches**: 72 branches from before main's history was rewritten (they share no history with the current main). Listed: each commit that is not in a release tag or in main and whose subject doesn't appear in main (198 commits). Duplicates by subject are removed and each commit is listed under the first branch that has it.

## 1. main (cdcd9c6..2f10b23d)

| Date | Commit | Subject |
|---|---|---|
| 2026-10-30 | `8a0d2d20` | Free Cotypist's model when it's turned off; white input-source icon; "No activity" |
| 2026-10-30 | `e533bace` | Have the helper verify Muesli before sending what the user types |
| 2026-10-30 | `592020b7` | Count Cotypist's acceptance rate only where untaken history is complete |
| 2026-10-30 | `ea4a23cb` | Move Cotypist's acceptance numbers from each timeline row to Insights |
| 2026-10-30 | `1e47160f` | Keep the frontmost app out of the stored context source |
| 2026-10-30 | `e86c3234` | Drop the helper's debug log; reset the keystroke record on Tab and function keys |
| 2026-10-30 | `1b8cb96f` | Keep the text before a line break in the last generated token |
| 2026-10-30 | `989eb616` | Keep which app the user typed in only for accepted suggestions |
| 2026-10-30 | `2214382c` | Judge Cotypist word completeness from the stop reason |
| 2026-10-30 | `7adad267` | Address Cotypist review: authenticate socket callers, gate on the bundled helper |
| 2026-10-30 | `389fd90a` | Show Cotypist model downloads with the shared progress details |
| 2026-10-30 | `1ecb3817` | Tell people what to do when macOS shows an empty English list |
| 2026-10-30 | `82c101c7` | Record the suggesting model; per-model gates in the benchmark |
| 2026-10-30 | `c3422be6` | Stop paused Cotypist downloads; tie outcomes to suggestion IDs |
| 2026-10-30 | `d4dc3d40` | Document Cotypist in the README |
| 2026-10-30 | `77eca619` | Add Cotypist model choice with Qwen 3.5 base models |
| 2026-10-30 | `4d7ed2de` | Address second Cotypist review round; support byte-level vocabularies |
| 2026-10-30 | `bc72692b` | Address Cotypist review feedback and prepare the helper for release |
| 2026-10-30 | `527b94b8` | Calibrate Cotypist's confidence gates per trigger |
| 2026-10-30 | `8a3c5970` | Let the Cotypist comparison sweep the first-word gate |
| 2026-10-30 | `75bb74db` | Add Cotypist settings, word-by-word accept and history |
| 2026-10-30 | `a2d78077` | Fix Cotypist mid-word suggestions and add a Release dev build |
| 2026-10-30 | `6a3c2eac` | Add experimental Cotypist typeahead via an input method |
| 2026-10-29 | `64fdb1c0` | Preserve streaming metadata and formatting defaults after main rebase |
| 2026-10-29 | `2ca15704` | fix(dictation): monitor corrections after acknowledged live finalization |
| 2026-10-29 | `ba871125` | Preserve Korean word spacing across streaming chunks |
| 2026-10-29 | `92029a36` | Keep Bodhan streamed language spans aligned through silence |
| 2026-10-29 | `d3fb1298` | Retire recovery snapshot at its last valid audio boundary |
| 2026-10-29 | `b0ff14a6` | Document DCO sign-off requirements for agents |
| 2026-10-29 | `678bd2a0` | Document exceptional opt-in Blacksmith use for agents |
| 2026-10-29 | `a1bc187c` | Make Blacksmith CI opt-in per PR and support manual cache warming |
| 2026-10-29 | `3ab81322` | Allow maintainer-triggered migration PR 698 on Blacksmith |
| 2026-10-29 | `c213050a` | Enforce three-snapshot retention on Blacksmith caches |
| 2026-10-29 | `105b59a1` | Reserve Blacksmith runners for maintainer-owned work |
| 2026-10-29 | `80ef58e5` | Migrate workflows to Blacksmith |
| 2026-10-29 | `9edaca3d` | Report History recovery only after dictation persistence succeeds |
| 2026-10-29 | `784e1a47` | Report clipboard ownership loss separately from paste shortcut failure |
| 2026-10-29 | `f5d21107` | Centralize clipboard ownership across paste transactions |
| 2026-10-29 | `5f1037e4` | Scope streaming buffering to the starting app and recover partial dictation |
| 2026-10-29 | `28adabba` | Add per-app dictation formatting templates |
| 2026-10-29 | `95816d2e` | Test automatic microphone routing across lid transitions |
| 2026-10-29 | `135ae51e` | Extend shared buffered streaming to Bodhan |
| 2026-10-28 | `e4d37656` | Follow the system default input when the lid is closed |
| 2026-10-28 | `10a28f8f` | Keep buffered streaming alive through alignment failures |
| 2026-10-28 | `3a9ade10` | Keep Whisper streaming through revisions to committed words |
| 2026-10-28 | `f240702c` | Avoid building the full settings catalog in the sidebar |
| 2026-10-28 | `54e12876` | Add system-following appearance with shared theme controls |
| 2026-10-28 | `b9cb9ab0` | Guard native CLI inference inputs and platform errors |
| 2026-10-28 | `ba2c1d01` | Add CLI parity for app transcription models |
| 2026-10-28 | `daf121eb` | Refresh changed clipboard snapshots before live paste dispatch |
| 2026-10-28 | `1c9e84b8` | Serialize library model preparation while keeping downloads parallel |
| 2026-10-27 | `0d70f8d1` | Share same-model preparation and isolate independent library downloads |
| 2026-10-27 | `31212b63` | Cancel superseded model preparation and guard FluidAudio load ownership |
| 2026-10-27 | `642c2259` | Show Phonon on its own card with official Fermion branding |
| 2026-10-27 | `c672d50b` | Assign Phonon preparation and integration tests to CI shard |
| 2026-10-27 | `fc3c58c9` | Warm Phonon GPU inference before publishing model readiness |
| 2026-10-27 | `23c3fe69` | Preserve recording template fallback and empty-note formatting |
| 2026-10-27 | `6fc98812` | Add Phonon-2.5 transcription with a GPU encoder |
| 2026-10-27 | `0eda9473` | Simplify recording layout with contextual controls and bordered notes |
| 2026-10-27 | `14617391` | Allow choosing meeting templates during recording |
| 2026-10-27 | `4481c15a` | Restore frequent Whisper word groups and expose stopped cursor updates |
| 2026-10-27 | `0bee39e1` | Clarify embedded meeting navigation versus anchored chat entry |
| 2026-10-27 | `a1e84426` | Document chat, persistence, and native interaction invariants for agents |
| 2026-10-27 | `cb68f286` | Refresh SQLite and meeting chat documentation; keep Context local-only |
| 2026-10-27 | `2cdb7b63` | Cover screen discard and CoreAudio cleanup with quit deadline |
| 2026-10-27 | `ef318ff9` | Fix confirmed quit deadlock by leaving the main dispatch queue |
| 2026-10-27 | `28bee944` | Cancel queued buffered decoding when a session closes |
| 2026-10-27 | `95b6d7b5` | Retain unrecognized rolling audio for bounded recovery |
| 2026-10-27 | `01fe34bc` | Retry empty embedded meeting scope preparation when idle |
| 2026-10-27 | `05b7ea59` | Preserve initial zero-timed words and keep previews local-only |
| 2026-10-27 | `3d3816e0` | Keep active chat scope intact during meeting-specific navigation |
| 2026-10-27 | `e68a5902` | Fix live dictation model selection, AX responsiveness, and output preservation |
| 2026-10-27 | `38ea11c0` | Preserve chat drafts and undo while clarifying context and copy output |
| 2026-10-27 | `fedb5ea2` | Focus newly mounted chat editors and avoid navigation reloads |
| 2026-10-27 | `1f588172` | Document final buffered dictation behavior and validation |
| 2026-10-27 | `7e0f0104` | Preserve unsent chat drafts when changing meeting scope |
| 2026-10-27 | `d29918b6` | Improve Ask Meetings workspace, embedded chat, and navigation |
| 2026-10-27 | `8b97a82c` | Filter marked non-speech cues from live and final transcription |
| 2026-10-27 | `3b4c8a8c` | Finalize live dictation from its streaming tail without full retranscription |
| 2026-10-27 | `2f1dd51a` | Reuse complete buffered dictation with confidence and alignment evidence |
| 2026-10-26 | `edc3bdaf` | Keep buffered dictation progressing across pauses and alignment revisions |
| 2026-10-26 | `d1385719` | Keep retired cleanup storage controls out of the model picker |
| 2026-10-26 | `9557bc3a` | Restore verified Codex placeholder handling on the stable streaming baseline |
| 2026-10-26 | `861201bf` | Fix Quill readiness and retired cleanup model storage controls |
| 2026-10-26 | `5a120827` | Restore the baseline from the successful 106-second Whisper test |
| 2026-10-26 | `ba646471` | Avoid rehashing cleanup downloads on the main actor |
| 2026-10-26 | `5560e689` | Polish cleanup readiness and remove private diagnostic content |
| 2026-10-26 | `b9a1cb40` | Recognize Codex generated placeholders before first dictation paste |
| 2026-10-26 | `df2f1158` | Keep optional cleanup preparation from blocking dictation |
| 2026-10-26 | `a044cb2a` | Acknowledge verified empty placeholder removal on first live paste |
| 2026-10-26 | `2e313720` | Use MLX for Qwen cleanup and prepare models before recording |
| 2026-10-26 | `1215f838` | Use text agreement and retained context for Whisper live windows |
| 2026-10-26 | `b843a3ad` | Suspend download test waits instead of blocking the cooperative executor |
| 2026-10-26 | `1e94aefc` | Render notch mode on external displays without a camera gap |
| 2026-10-26 | `9d0ad706` | Release loading ownership for notices and cache feedback configuration |
| 2026-10-26 | `c8230769` | Preserve notch disclosure and restore external-display status fallback |
| 2026-10-26 | `9136ebca` | Show model transfer progress and name Bodhan tiers Pro and Lite |
| 2026-10-26 | `b55523b7` | Route indicator status feedback to the selected notch |
| 2026-10-25 | `754422cf` | Update cleanup models and migrate Hugging Face downloads to Muesli-HQ |
| 2026-10-25 | `0d2a9a73` | Improve Whisper preview cadence and reuse completed streaming ASR |
| 2026-10-25 | `6a0f1c64` | Avoid delayed redundant caret writes during live paste |
| 2026-10-25 | `83b6ed7d` | Pause live dictation during temporary Accessibility failures |
| 2026-10-25 | `9db010b2` | Use bounded acknowledged plain-text pastes for Codex live dictation |
| 2026-10-25 | `05652627` | Acknowledge keyboard word boundaries before continuing live dictation |
| 2026-10-25 | `79cb708d` | Document accepted English-only Quill fallback limitation |
| 2026-10-25 | `95996771` | Smooth Whisper previews with stable word groups and bounded draft |
| 2026-10-25 | `02d8e594` | Fall back to full ASR text for long Quill instructions |
| 2026-10-25 | `074f56a5` | Fix Whisper live preview alignment and word-boundary stalls |
| 2026-10-25 | `ea9973d5` | Keep Quill transcript preview model loading local-only |
| 2026-10-25 | `9ff74ea3` | Send Gemma Quill audio directly with independent transcript preview |
| 2026-10-25 | `23ca0b63` | Normalize blank legacy Gemma overrides and retire LiteRT idle test |
| 2026-10-25 | `0b2b61b9` | Fix Whisper preview alignment to exclude padded audio |
| 2026-10-25 | `6703bec0` | Drain canceled model downloads before retry and clarify MLX benchmarks |
| 2026-10-25 | `6608be75` | Invalidate Gemma recording readiness when shared engines unload |
| 2026-10-25 | `c117a1e1` | Extend buffered live transcription to all batch Parakeet models |
| 2026-10-25 | `0e56543d` | Fix cleanup limits, drafter pause, readiness and CI cache paths |
| 2026-10-25 | `51819ea7` | Batch live cursor updates and paste final cleanup once |
| 2026-10-25 | `030e242d` | Keep hands-free finalization under explicit user control |
| 2026-10-25 | `8d93f473` | Recover live previews from transient word alignment failures |
| 2026-10-25 | `7c3abb66` | Preserve selected folder for Quick Note and audio imports |
| 2026-10-25 | `ac8351ce` | Freeze stable dictation prefixes and coalesce draft delivery |
| 2026-10-25 | `a57b21c6` | Document main rebase and preserve SwiftPM lockfile formatting |
| 2026-10-25 | `1ae3794f` | Update README for native Gemma MLX runtime and model options |
| 2026-10-25 | `30273e35` | Fall back to target decoding when Gemma drafter is unavailable |
| 2026-10-25 | `38685a36` | Enable Gemma MTP by default through one shared policy |
| 2026-10-25 | `4dd22295` | Keep legacy overrides from blocking managed Gemma model deletion |
| 2026-10-25 | `7410d086` | Compile pinned MLX shaders for native SwiftPM CI and preserve recording toggles |
| 2026-10-25 | `c71af540` | Build CI Metal resources and prepare local Quill ASR dependencies |
| 2026-10-25 | `99ddc520` | Gate Quill on its local models rather than hosted dictation credentials |
| 2026-10-25 | `f9e97eff` | Fix Gemma preparation lifecycle and native CI review findings |
| 2026-10-25 | `39b0237b` | Fix native Gemma downloads, optional preparation and migration packaging |
| 2026-10-25 | `da8687f4` | Replace LiteRT Gemma inference with native Swift MLX runtimes |
| 2026-10-25 | `62ba9a56` | Handle Codex empty composer placeholder on first dictation key |
| 2026-10-25 | `37d86737` | Reconcile asynchronous live dictation text and caret readbacks |
| 2026-10-25 | `3db3a449` | Use verified keyboard delivery for Codex live dictation |
| 2026-10-25 | `eef6ac8a` | Preserve imported notes across autosave and refresh word counts |
| 2026-10-25 | `0c7f5187` | Address Granola import review feedback |
| 2026-10-25 | `d2e843fd` | Wait for live dictation cursor updates to be acknowledged |
| 2026-10-25 | `a5b5f6ab` | Shorten dictation settings descriptions |
| 2026-10-25 | `0504c515` | Add in-app Granola ZIP and CSV meeting importer |
| 2026-10-24 | `e64cc78d` | Clarify hands-free live text toggle |
| 2026-10-24 | `ee782f18` | Add buffered live dictation and meeting previews |
| 2026-10-24 | `37386261` | Limit Slack attribution to helper bundle IDs |
| 2026-10-24 | `70880f50` | Recognize Slack huddle audio from helper processes |
| 2026-10-24 | `32790f6c` | fix: an earlier empty-recording result is not meeting content |
| 2026-10-24 | `20f598ae` | Remove redundant calendar filter subtitle |
| 2026-10-24 | `75afc141` | Keep calendar filter subtitle to one short line |
| 2026-10-24 | `8564a0f6` | Default calendar surfaces to events with meeting links |
| 2026-10-24 | `395e938e` | Reject ID-less Teams links with fragments or empty path segments |
| 2026-10-24 | `e211052b` | Address notch transcript interaction and appearance review findings |
| 2026-10-24 | `49340eb0` | Match notch transcript background to the black notch |
| 2026-10-24 | `f2f3a17e` | Show meeting transcript when hovering over the notch |
| 2026-10-24 | `996e6c2e` | Document meeting video recording and permissions |
| 2026-10-24 | `f6330b80` | Bound meeting audio drain retries before finalization |
| 2026-10-24 | `ca73406a` | Keep video finalization alive after timeout and isolate legacy storage failures |
| 2026-10-23 | `73f2342f` | Preserve interrupted meeting videos and complete review regressions |
| 2026-10-23 | `c4e50b33` | Warm Gemma cleanup during dictation and add stage timing diagnostics |
| 2026-10-23 | `89b12f20` | Show local model and meeting storage breakdown in General settings |
| 2026-10-23 | `6ad20c9d` | Harden meeting video recovery, lifecycle, audio timing and CI coverage |
| 2026-10-23 | `fe7f8802` | Simplify meeting video playback controls |
| 2026-10-23 | `a19ff38c` | Store meeting videos alongside audio with meeting database ownership |
| 2026-10-23 | `6a343f41` | Add explicit meeting screen recording and native video playback |
| 2026-10-23 | `bff10e64` | Switch Gemma cleanup to verified MTP artifact and validate native performance |
| 2026-10-23 | `0511dc2c` | Gate Gemma cleanup selection on supported macOS |
| 2026-10-23 | `e4107c93` | Record unresolved cleanup latency in merge handover |
| 2026-10-23 | `52d39af4` | Preserve dictated model lines during cleanup output filtering |
| 2026-10-23 | `3eee179a` | Open model picker immediately after enabling cleanup |
| 2026-10-23 | `6e9bfb99` | Hide cleanup model choices while cleanup is off |
| 2026-10-23 | `aa891157` | Cover compact and fixed-height meeting title layouts |
| 2026-10-23 | `7df6eb84` | Reject interrupted GGUF and capped Gemma cleanup output |
| 2026-10-23 | `71f63821` | Keep cached cleanup Gemma deletable on unsupported macOS |
| 2026-10-23 | `cd1fdf18` | Fix cleanup downloads, generation safety, and review regressions |
| 2026-10-23 | `27722cac` | DCO Remediation Commit for pHequals7 <ce17b115@smail.iitm.ac.in> |
| 2026-10-23 | `be104e4f` | Centralize cleanup release metadata and document model swaps |
| 2026-10-23 | `31b80f33` | Hide undownloaded cleanup models from menu bar |
| 2026-10-23 | `26abac95` | Add cleanup model selection to menu bar |
| 2026-10-22 | `e2dd5340` | Unify cleanup model activation labels |
| 2026-10-22 | `10083e15` | Add managed Muesli Pro Cleanup betas and preserve output formatting |
| 2026-10-22 | `4cd2f91b` | Align microphone diarization toggle with its subtitle |
| 2026-10-22 | `5c78a109` | Shorten in-room speaker help text |
| 2026-10-22 | `d4c2c9e4` | Stream bounded PCM windows directly to both meeting diarization passes |
| 2026-10-22 | `231f9f16` | Move in-room speaker setting into meeting transcription section |
| 2026-10-22 | `4545d80c` | Preserve brief local speakers in long meetings and clarify echo limitations |
| 2026-10-21 | `998412e9` | docs: retain resources and expand vocabulary guidance |
| 2026-10-21 | `cafe8475` | docs: streamline README and preserve public guide links |
| 2026-10-21 | `b02719c8` | docs: add published Muesli plugin to README |
| 2026-10-21 | `6a49dcf9` | Reject a repeated meeting start before it touches the running pipeline |
| 2026-10-21 | `0a53a83c` | Keep the live mic diarization track and clean up failed recorder setup |
| 2026-10-21 | `8d06028b` | Use a CPU encoder for Redux in app and CLI |
| 2026-10-21 | `03574367` | Avoid repeated history synchronization during UI refresh |
| 2026-10-21 | `4f68bb02` | Preserve model cache cleanup and consistent OS fallbacks |
| 2026-10-21 | `1b9decc4` | Respect model OS compatibility when restoring transcription settings |
| 2026-10-21 | `6fb831c4` | Add Parakeet Redux through FluidAudio Core ML |
| 2026-10-20 | `fd1b68f0` | Keep meeting recordings running when Escape is pressed |
| 2026-10-20 | `25068680` | Refresh local meeting date parsers when the time zone changes |
| 2026-10-20 | `9d5755a3` | Replace microphone graphs after failed native cleanup |
| 2026-10-20 | `daa52934` | Require a meeting ID in short-form Teams link patterns |
| 2026-10-20 | `606f9ba7` | Match Teams free event URLs by exact host and meeting path |
| 2026-10-20 | `25d7148e` | Test dictation microphone exception containment and retry |
| 2026-10-20 | `0e81d032` | Recognize short-form Teams join links in calendar events |
| 2026-10-17 | `ee1b5ce7` | DCO Remediation Commit for B Praveen <bpraveen.in@gmail.com> |
| 2026-10-17 | `09f318ea` | fix: guard dictation mic engine calls against AVAudioEngine NSExceptions |
| 2026-10-17 | `e4f47b71` | Scope the mic diarization track to the app identity and cover its lifecycle |
| 2026-10-16 | `8206adba` | Require significant local speakers and harden mic diarization lifecycle |
| 2026-10-16 | `bf72a0f6` | Harden in-room speaker attribution and register local labels |
| 2026-10-16 | `4070432d` | Identify in-room speakers by diarizing the microphone channel |
| 2026-10-16 | `ab4426fe` | test: cover both meeting selection branches in the observation test |
| 2026-10-16 | `91be7833` | fix: stop Settings onAppear from feeding a SwiftUI update loop |
| 2026-10-15 | `e9d4f2a9` | test: keep a release that arrives before the stuck job parks |
| 2026-10-15 | `875664ea` | test: resume the stuck job after the deadline so the test leaves nothing suspended |
| 2026-10-15 | `2dd3d4b3` | fix: the meeting title row keeps its own height |
| 2026-10-15 | `a5ef96d9` | fix: a long meeting title no longer widens the whole meeting page |
| 2026-10-15 | `6bb10e8e` | fix: mark an empty recording as empty instead of summarizing it |
| 2026-10-15 | `4a36479f` | fix: quitting anyway no longer freezes the app |
| 2026-10-15 | `591142fd` | Verify cleaned segment timing and artifact-only output |
| 2026-10-15 | `eff55b36` | fix: reject context-truncated summaries and classify streamed errors |
| 2026-10-15 | `675bb4b1` | fix: bound summary retries and validate stream completion |
| 2026-10-14 | `14b92782` | fix: redraw meeting rows on a locale change without replacing them |
| 2026-10-14 | `0c7ab7f8` | fix: redraw the meeting list on a locale change and cache only offset-bearing dates |
| 2026-10-14 | `e14c306d` | fix: drop cached start-time formatters when the system locale changes |
| 2026-10-14 | `bc67cafe` | fix: cache parsed meeting start times across renders |
| 2026-10-14 | `93c2b25a` | fix: reuse one DateFormatter per locale and zone for meeting start times |
| 2026-10-14 | `3fbfec8e` | fix: parse each meeting's start time once when sorting the All Meetings list |
| 2026-10-14 | `f3771ce9` | fix: detect truncated Anthropic summaries and stream long requests |
| 2026-10-14 | `7d2c1ff0` | fix: remove default meeting summary output caps |
| 2026-10-14 | `0c130df8` | Fix Cantonese voice availability and isolate model fixtures |
| 2026-10-14 | `32eef041` | fix(audio): require successful startup before microphone promotion |
| 2026-10-14 | `f1c723f0` | Keep Norwegian "er" and strip control tokens from meeting segments |
| 2026-10-14 | `ede5701f` | Add Norwegian to Whisper languages and skip special tokens |
| 2026-10-13 | `aa9c76cf` | refactor(audio): retain main-style microphone switching |
| 2026-10-13 | `6a8c7cf7` | fix(audio): keep ordinary microphone delivery FIFO |
| 2026-10-12 | `4f97a3d1` | Expose supported Whisper and Nemotron language selectors |
| 2026-10-12 | `00768616` | fix(chat): preserve retry memory and composer undo boundaries |
| 2026-10-12 | `f3b117b6` | test(chat): exercise attached context in passage priority regression |
| 2026-10-12 | `d81968c2` | fix(chat): fit retrieval to context budgets and finalize bounded reads |
| 2026-10-12 | `e80f1d6b` | fix(chat): honor model output caps during generation and compaction |
| 2026-10-12 | `1ae8e461` | feat(chat): add native meeting tools and durable conversation memory |
| 2026-10-12 | `7df9cc39` | fix(audio): drain accepted capture before session pause boundaries |
| 2026-10-12 | `34ada2eb` | test: bound the cumulative planning regression test runtime |
| 2026-10-12 | `184460b9` | test: isolate cumulative planner budget from CI scheduling delays |
| 2026-10-12 | `c28e77f8` | perf(chat): limit revision migration to referenced sources |
| 2026-10-12 | `b952cffd` | fix(chat): harden retrieval migrations and preserve draft and export context |
| 2026-10-12 | `a4bd271e` | fix: address meeting chat review and CI findings |
| 2026-10-12 | `f24fc7db` | refactor(audio): preserve microphone work in separate lifecycle PR |
| 2026-10-12 | `c7eed1a4` | fix: enable own-app dictation and simplify meeting chat UI |
| 2026-10-12 | `189b7d4f` | fix: handle Claude Code verbose results and report useful errors |
| 2026-10-12 | `e1121e05` | feat: integrate reusable SwiftUI chat components and fix reasoning summaries |
| 2026-10-12 | `40899b33` | refactor(pr605): separate microphone work from proven Zoom fix |
| 2026-10-12 | `940e3fa0` | feat: improve meeting chat harness context and native interface |
| 2026-10-12 | `8ca757e5` | fix(audio): preserve active input buffers across delayed resume |
| 2026-10-11 | `d08fd5cf` | fix(audio): drain native microphone tails during live handoff |
| 2026-10-11 | `7d576378` | fix(audio): preserve timestamp-bounded native microphone stop tails |
| 2026-10-11 | `3168aab5` | fix(indicator): preserve active notch during background callbacks |
| 2026-10-11 | `1ee6995e` | fix(audio): enforce per-buffer admission and ordered mic handoffs |
| 2026-10-11 | `a13ce391` | fix(audio): drain fractional microphone conversion output |
| 2026-10-11 | `ac236510` | fix(audio): drain accepted microphone samples during route retirement |
| 2026-10-11 | `0b04ca77` | fix: address meeting chat review findings and async races |
| 2026-10-11 | `ae3058e8` | fix(audio): forward buffered admission through recorder fallback |
| 2026-10-11 | `65fc4bc5` | fix(audio): retain pre-pause microphone buffers through shutdown |
| 2026-10-11 | `d4f112c1` | fix(audio): preserve accepted microphone samples during stop |
| 2026-10-11 | `05e87c47` | fix(audio): avoid tap-dependent aggregate startup for Zoom |
| 2026-10-11 | `e8f1d950` | fix(audio): move microphone processing off tap callbacks |
| 2026-10-10 | `2575276e` | fix: collapse empty meeting chat history and document UI |
| 2026-10-09 | `a8ea30da` | fix: preserve empty meeting details without a citation target |
| 2026-10-09 | `855749cc` | fix: preserve current provider contracts and bounded relevant context |
| 2026-10-09 | `cb8f850b` | fix: address meeting chat lifecycle scope and citation review findings |
| 2026-10-08 | `09c3203b` | Add Ukrainian to the Whisper and Nemotron language pickers |
| 2026-10-08 | `c1ec613c` | feat: add meeting chat quick prompts drafts and cited exports |
| 2026-10-08 | `d9e59ecd` | feat: add Ask Meetings page and source navigation |
| 2026-10-08 | `c2670d1f` | feat: generate cancellable meeting answers with validated citations |
| 2026-10-06 | `a7f9fb2e` | perf: search meeting passages without rereading unchanged transcripts |
| 2026-10-06 | `03e3891f` | fix: preserve passage relevance and verify index erasure |
| 2026-10-06 | `0e51ed7d` | feat: retrieve cited passages across saved meetings |
| 2026-10-06 | `d671fcb4` | feat: persist local meeting conversations and source dependencies |
| 2026-10-06 | `8eae522b` | docs: plan Ask Meetings implementation and validation |
| 2026-10-06 | `e92ccc2f` | docs: define Ask Meetings UX and grounded chat design |

## 2. Active branches

### `codex/medasr-integration` (2 commits, tip 2026-10-30)

| Date | Commit | Subject |
|---|---|---|
| 2026-10-30 | `c17e438b` | Replace MedASR HF sign-in with local terms acceptance |
| 2026-10-30 | `674f6979` | Add Google MedASR Core ML dictation and CLI support |

### `codex/vercel-main-only` (1 commits, tip 2026-10-30)

| Date | Commit | Subject |
|---|---|---|
| 2026-10-30 | `13b46765` | Restrict automatic Vercel deployments to main |

### `codex/bodhan-output-quality` (1 commits, tip 2026-10-29)

| Date | Commit | Subject |
|---|---|---|
| 2026-10-29 | `53704920` | Filter Bodhan placeholders and preserve overlapping punctuation |

### `codex/cua-trycua-benchmark` (30 commits, tip 2026-10-28)

| Date | Commit | Subject |
|---|---|---|
| 2026-10-28 | `0d3d70fa` | Capture driver startup task before entering shutdown deadline |
| 2026-10-28 | `aba61a39` | Preserve pending browser verification and group trace evidence |
| 2026-10-28 | `77b3ed83` | Preserve native planner images and recover transient browser reads |
| 2026-10-28 | `2bcc6c61` | Fix background CUA ownership, focus, routing and result reporting |
| 2026-10-28 | `795535f1` | DCO Remediation Commit for pHequals7 <ce17b115@smail.iitm.ac.in> |
| 2026-10-28 | `3be5d854` | Harden CUA connection recovery and visual verification |
| 2026-10-28 | `410cb6ff` | Add enforced Accessibility-only CUA comparison mode |
| 2026-10-28 | `cf6f6a36` | Keep background CUA awake through planning and execution |
| 2026-10-28 | `c9437ba7` | Improve reference CUA browser context, connection lifecycle and readable traces |
| 2026-10-28 | `10b036ac` | Observe CUA focus without cancelling unrelated foreground activity |
| 2026-10-28 | `f2dfe729` | Reuse reference driver across isolated CUA task sessions |
| 2026-10-28 | `779930c4` | Correct Chromium compositor surface ownership in reference driver |
| 2026-10-28 | `101a8443` | Preserve scoped setup inspection and strict background reference patch |
| 2026-10-28 | `d8157576` | Add explicit existing-profile authorization for reference benchmark |
| 2026-10-28 | `93504e66` | Prevent self-activating shortcut routes in strict background comparison |
| 2026-10-28 | `ad6790ba` | Enable upstream shadow cursor on trycua 0.35.4 comparison |
| 2026-10-28 | `a88ba623` | Refuse foreground delivery in the local trycua comparison |
| 2026-10-28 | `7a61b720` | Use shared screen permission flow for development CUA replay |
| 2026-10-28 | `899f99bf` | Exclude model thinking from reference execution timeout |
| 2026-10-28 | `d8a22652` | Integrate background execution with current settings and writing boundaries |
| 2026-10-28 | `1c96f1ea` | Preserve structured MCP observations for reference planning |
| 2026-10-28 | `5e00cff4` | Route dev CUA commands through the instrumented trycua planner |
| 2026-10-28 | `1faab83e` | Instrument local trycua reference driver under the DevA host |
| 2026-10-28 | `4838add6` | docs: establish local direct trycua comparison branch |
| 2026-10-28 | `4f7dcc24` | Restore background focus through the host application when appropriate |
| 2026-10-28 | `3e09cac2` | Distinguish locked sessions from responsive empty Chrome AX state |
| 2026-10-28 | `0467ae10` | Corroborate Chrome window identity when private lookup declines |
| 2026-10-28 | `0a3fcd79` | Resolve Chrome background windows after verified AX enablement |
| 2026-10-28 | `ee18c3cc` | Add opt-in native window-scoped background CUA execution |
| 2026-10-28 | `8817838e` | docs: define native background CUA acceptance contract |

### `codex/time-turner` (2 commits, tip 2026-10-25)

| Date | Commit | Subject |
|---|---|---|
| 2026-10-25 | `b8f79340` | Improve Time Turner schema and agent retrieval |
| 2026-10-24 | `48fee9d8` | Add Time Turner ambient memory, timeline, and agent CLI |

### `codex/pr-597-language-selectors` (1 commits, tip 2026-10-13)

| Date | Commit | Subject |
|---|---|---|
| 2026-10-13 | `e5e72999` | Expose supported Whisper and Nemotron language selectors |

### `vorflux/macos-ui-rendering-ci` (3 commits, tip 2026-10-11)

| Date | Commit | Subject |
|---|---|---|
| 2026-10-11 | `bc64b633` | Tighten visual evidence trust boundaries |
| 2026-10-11 | `eb8875e1` | Harden visual test isolation and reporting |
| 2026-10-11 | `23974ec5` | Add advisory macOS visual test contract |

## 3. Older branches (unreleased commits)

### `codex/CUA-refactor`

| Date | Commit | Subject |
|---|---|---|
| 2026-07-07 | `d13eba05` | Match CUA target windows by AX identity |
| 2026-07-07 | `c4cd85cc` | Harden CUA screenshot and OCR evidence contracts |
| 2026-07-07 | `36a9be0d` | Retry CUA accessibility keepalive assertions |
| 2026-07-07 | `c3ed76da` | Serialize CUA sessions and validate AX windows strictly |
| 2026-07-07 | `c0e06d27` | Enforce scoped CUA paste and OCR evidence |
| 2026-07-07 | `0f5ab772` | Fail closed on unscoped quiet CUA delivery |
| 2026-07-07 | `9282da7a` | Harden quiet CUA target ownership and text evidence |
| 2026-07-07 | `63ca36cf` | Address Greptile CUA persistence and targeting feedback |
| 2026-07-07 | `91396f21` | Enforce CUA session and target invariants |
| 2026-07-07 | `89004678` | Fail closed on quiet CUA click focus failure |
| 2026-07-07 | `2d4e92e2` | Harden CUA quiet keys and trace persistence |
| 2026-07-06 | `460d3756` | Block CUA during meeting pipeline |
| 2026-07-06 | `28197f31` | Allow CUA finish during target mismatch |
| 2026-07-06 | `33b9c622` | Harden quiet CUA target delivery |
| 2026-07-06 | `028022ed` | Harden CUA background action contracts |
| 2026-07-04 | `1b850774` | Redact CUA OCR text from traces |
| 2026-07-04 | `070ef458` | Harden CUA control harness |
| 2026-06-24 | `53b73998` | tighten CUA scoped tool contract |
| 2026-06-24 | `87b1e0ac` | Fix profiled dev app startup |
| 2026-06-24 | `19b4c4fd` | Add CUA app control preference |
| 2026-06-24 | `3572ccc0` | Soften CUA OCR text verification |
| 2026-06-24 | `dc587ca5` | Tighten CUA execution safety |
| 2026-06-24 | `5e7f7c1c` | Address CUA review edge cases |
| 2026-06-24 | `e91f3e24` | Harden CUA visual evidence handling |
| 2026-06-24 | `1d76b2a9` | Tighten CUA text write evidence |
| 2026-06-24 | `96615d95` | Harden CUA action lifecycle invariants |
| 2026-06-23 | `4c0aa77f` | Fix CUA audio race review issues |
| 2026-06-23 | `0b3df23f` | Address CUA review safety followups |
| 2026-06-23 | `776863ed` | Address CUA review P1s |
| 2026-06-23 | `6336b6a0` | Tighten CUA window refresh safety |
| 2026-06-23 | `a0c08225` | Make CUA OCR model-directed |
| 2026-06-23 | `a07606ee` | Align CUA target safety contracts |
| 2026-06-23 | `070eba54` | Harden CUA focused actions |
| 2026-06-23 | `512d2e1a` | Address CUA review feedback |
| 2026-06-23 | `fcda046c` | Refactor CUA around visual control |

### `codex/cotypist-qwen-text-fim`

| Date | Commit | Subject |
|---|---|---|
| 2026-08-01 | `18e72b0b` | Use Qwen3.5 Base FIM for Cotypist |
| 2026-07-17 | `47ae46e2` | Add Qwen Text FIM Cotypist backend |
| 2026-07-16 | `a8abf07a` | Simplify Cotypist settings |
| 2026-07-16 | `feb234bc` | Remove Qwen from Cotypist |
| 2026-07-16 | `6f7d0b83` | Prevent Cotypist retry prompt leakage |
| 2026-07-16 | `b2990c1b` | Fix Qwen Cotypist context repetition |
| 2026-07-16 | `0120d9fa` | Improve Qwen Cotypist quality feedback |
| 2026-07-16 | `f0473223` | Improve Cotypist inline suggestion UI |
| 2026-07-16 | `05d6872e` | Fix Cotypist caret overlay placement |
| 2026-07-16 | `46127eb6` | Harden Cotypist completion and add coverage |
| 2026-07-16 | `28d2cdeb` | Add experimental Cotypist typeahead |

### `codex/cua-model-first-harness`

| Date | Commit | Subject |
|---|---|---|
| 2026-07-11 | `d2d1fc5c` | Document and test CUA settings integration |
| 2026-07-11 | `7a678bed` | Run CUA tests in core CI shard |
| 2026-07-11 | `46a5d4dc` | Expand reversible CUA settings controls |
| 2026-07-11 | `b9535802` | Address settings readiness review feedback |
| 2026-07-11 | `90d2c5b6` | Add CUA settings controls |
| 2026-07-10 | `4d353740` | Bound CUA planner history |
| 2026-07-10 | `f1b61f94` | Reset CUA planner around intent tools |

### `codex/fix-profile-entitlement-arrays`

| Date | Commit | Subject |
|---|---|---|
| 2026-06-26 | `9b375a5f` | Fix CloudKit signing entitlement injection |

### `codex/fix-settings-permission-polling`

| Date | Commit | Subject |
|---|---|---|
| 2026-06-15 | `6b280d49` | Refresh login item state on Settings activation |
| 2026-06-15 | `eddd104b` | Avoid launch-at-login polling in Settings timer |

### `codex/implement-live-meeting-behavior`

| Date | Commit | Subject |
|---|---|---|
| 2026-04-28 | `3a3eb8f3` | Move cloud-agent workflow guidance to tracked AGENTS.md |

### `codex/localvqe-aec-replacement`

| Date | Commit | Subject |
|---|---|---|
| 2026-05-22 | `acbaaa79` | Productionize LocalVQE meeting AEC |

### `codex/meeting-mic-health-recovery`

| Date | Commit | Subject |
|---|---|---|
| 2026-08-13 | `40121c36` | Three-way recovery request results; async pending-candidate cleanup |
| 2026-08-13 | `e6b31017` | Classify user-muted input instead of opening recovery episodes |
| 2026-08-13 | `d1021849` | Keep cooldown back-pressure on transient recovery refusals |
| 2026-08-13 | `8e9c6e49` | Stop pending handoff candidates synchronously at teardown |
| 2026-08-13 | `c4772adb` | Publish teardown invalidation independently of the startup critical section |
| 2026-08-13 | `46b879b9` | Self-stop capture started during teardown; per-session telemetry authorization |
| 2026-08-13 | `464aea4a` | Make teardown-invalidation win every race with stale handoff workers |
| 2026-08-13 | `3c49b845` | Revalidate recovery candidates before prepare/start after teardown |
| 2026-08-13 | `5cc614f2` | Preserve meeting identity through discard for terminal mic telemetry |
| 2026-08-13 | `31cfebc4` | Close mic-health episodes on meeting discard; strengthen quiet-room test |
| 2026-08-13 | `30e06418` | Address review: sustained-recovery close, rich terminal telemetry, token-guarded dispatch, terminal-event gate |
| 2026-08-13 | `d5971bfa` | Invoke coordinator callbacks outside its lock |
| 2026-08-13 | `6e41ca28` | Fix stop()-ordering race and seal coordinator after meeting end |
| 2026-08-13 | `986b2973` | Recover meeting mic on confirmed health degradation; episode-level telemetry |

### `codex/pause-meeting-recording`

| Date | Commit | Subject |
|---|---|---|
| 2026-04-30 | `52de95b6` | Address pause review feedback |
| 2026-04-30 | `6df49f12` | Make discard confirmation always anchor |
| 2026-04-30 | `ca8855bf` | Strip underscore markdown from previews |
| 2026-04-30 | `3393c24c` | Make discard confirmation non-blocking |
| 2026-04-30 | `f4207ff7` | Confirm detail discard during recording |
| 2026-04-30 | `b36ad548` | Clean markdown from meeting previews |
| 2026-04-30 | `575e152d` | Make floating meeting control resume-safe |
| 2026-04-30 | `f90db7df` | Tighten screen context resume polling |
| 2026-04-30 | `b052531a` | Refine recording status bar menu |
| 2026-04-30 | `09e3b637` | Add pause controls for meeting recordings |

### `codex/release-signing-profile-guardrails`

| Date | Commit | Subject |
|---|---|---|
| 2026-06-25 | `beb3a97f` | Preserve default dev signing path |
| 2026-06-25 | `927488c2` | Tighten wildcard profile validation |
| 2026-06-25 | `09219cb1` | Address release signing review feedback |
| 2026-06-25 | `cdbc0a10` | Harden release signing profile checks |

### `codex/stabilize-vad-release-test`

| Date | Commit | Subject |
|---|---|---|
| 2026-06-10 | `12f9b523` | Stabilize streaming VAD boundary test |

### `codex/webrtc-aec-separation`

| Date | Commit | Subject |
|---|---|---|
| 2026-04-07 | `cb268e12` | Address Claude PR review: fix merge gap bug, clean dead code, extract WavWriter, cache AEC model |
| 2026-04-07 | `05a0099f` | Fix View Notes notification navigating to wrong meeting |
| 2026-04-07 | `118c592f` | Fix neural AEC dropping mic tail when system audio is shorter |
| 2026-04-07 | `76f0e835` | Add DTLN-aec CoreML neural echo cancellation for mic path |
| 2026-04-07 | `01320aef` | Revert Apple voice processing — ducking makes speakers unusable without headphones |
| 2026-04-07 | `75254c93` | Enable Apple voice processing AEC with minimum ducking and cap transcript consolidation |
| 2026-04-06 | `5da57580` | Remove WebRTC AEC from transcript path, add preserve-first reconciliation and sentence splitting |
| 2026-04-02 | `86c70fbc` | Fix chunk rotation queue re-entry |
| 2026-04-02 | `11b383cb` | Add transcript health recovery and indicator defaults |
| 2026-04-01 | `35d48ac9` | Harden meeting AEC service and notification state |
| 2026-03-30 | `52d5cabe` | Tighten meeting AEC runtime follow-ups |
| 2026-03-30 | `03e726c6` | Suppress meeting CTA during manual recording start |
| 2026-03-29 | `bff1ec19` | Remove meeting pipeline SVG artifact |
| 2026-03-29 | `f998158b` | Harden WebRTC AEC meeting transcription path |
| 2026-03-27 | `aa8cebd3` | Trim vendored WebRTC subset |
| 2026-03-27 | `e2b99ce7` | Spike official WebRTC AEC bridge |

### `cua-tts`

| Date | Commit | Subject |
|---|---|---|
| 2026-05-13 | `8a8afc74` | Avoid idle TTS stop work while muted |
| 2026-05-13 | `a09b6d22` | Serialize KittenTTS audio completion |
| 2026-05-13 | `3cd5575e` | Fix CUA speech review issues |
| 2026-05-13 | `0702bc0f` | Add native CUA voice feedback |

### `cursor/pr-342-settings-copy-12b4`

| Date | Commit | Subject |
|---|---|---|
| 2026-08-12 | `3370ee0d` | Fix recordOnly compile break and shorten settings copy |
| 2026-08-12 | `2dddfde9` | Update native/MuesliNative/Sources/MuesliNativeApp/MeetingsView.swift |
| 2026-07-27 | `cf5caebb` | Carry the calendar event ID through Coming Up recording actions |
| 2026-07-27 | `099d05d6` | Add a default meeting join action setting |

### `cursor/rebase-meeting-participants-eb39`

| Date | Commit | Subject |
|---|---|---|
| 2026-08-17 | `4b67afa8` | Scope meeting people to EventKit |
| 2026-08-17 | `9d7790ad` | Reconcile calendar people before meetings |
| 2026-08-17 | `46d63c6b` | Preserve curated meeting attendees |
| 2026-08-17 | `3bf9dc7b` | Coordinate calendar attendee test reads |
| 2026-08-17 | `6bab0ec5` | Address meeting people review feedback |
| 2026-08-17 | `e777e78e` | Polish meeting people and contact search |
| 2026-08-17 | `62da7b98` | Simplify meeting people around calendar attendees |
| 2026-08-17 | `364e2604` | Harden participant export names and Contacts name resolution |
| 2026-08-17 | `ae75e134` | Add Apple Contacts participants to meeting records |

### `cursor/rebase-pr-134-d382`

| Date | Commit | Subject |
|---|---|---|
| 2026-08-13 | `1ed40384` | Keep detection suppressed during meeting finalization |
| 2026-08-13 | `1a370e4f` | Isolate meeting processing indicator state |
| 2026-08-13 | `a432626a` | Allow dictation while meeting transcription finishes |

### `devin/1782027960-auto-record-scheduled-wakes`

| Date | Commit | Subject |
|---|---|---|
| 2026-06-23 | `8bd5acd7` | Calendar: drop teams.microsoft.us host to avoid downstream auto-stop gap |
| 2026-06-23 | `f3c658c6` | Calendar: unwrap Microsoft Safe Links and handle HTML/gov-tenant Teams join URLs |
| 2026-06-22 | `7ba9d05b` | Auto-record: revert pre-roll arm; hold App Nap opt-out while wakes pending |
| 2026-06-21 | `3560dc0c` | ci: re-trigger CI for fcb0f9b (dropped synchronize event) |
| 2026-06-21 | `fcb0f9b9` | Auto-record: engage App Nap opt-out only in pre-roll window before each wake |
| 2026-06-21 | `17372125` | Auto-record: claim on attempt (no retry of failed starts) |
| 2026-06-21 | `1637a30d` | Address review: only claim auto-record on successful start, re-validate late wakes against catch-up policy, inclusive catch-up boundary |
| 2026-06-21 | `66ee9f94` | Auto-record: schedule per-event wake timers at meeting start |

### `feat/cohere-transcribe-integration`

| Date | Commit | Subject |
|---|---|---|
| 2026-03-31 | `7787f911` | Point to new phequals/cohere-transcribe-coreml-mixed-precision HF repo |
| 2026-03-31 | `40f52865` | Add MLState contract comment, extract testable utils, add test coverage |
| 2026-03-31 | `d0b66da6` | Fix load generation race, preserve pre-anchor words in dedup, guard readVarint |
| 2026-03-31 | `d9d994c5` | Guard against model/warmup resurrection after shutdown |
| 2026-03-31 | `fc1acd1d` | Cancel loadTask on shutdown to prevent 3.8GB model resurrection |
| 2026-03-31 | `eb2d238c` | Replace O(n²) overlap dedup with hash-based trigram matching |
| 2026-03-31 | `6fa9407d` | Improve chunk overlap dedup with fuzzy word matching |
| 2026-03-31 | `5c950ad6` | Fix KV mask off-by-one, deduplicate chunk overlaps, remove dead param |
| 2026-03-31 | `8c30cd19` | Add VAD silence check for Cohere dictation to prevent hallucination |
| 2026-03-31 | `7f2ca489` | Add Cohere Transcribe card to Models UI between Parakeet and Whisper |
| 2026-03-31 | `5f49f8b8` | Promote Cohere Transcribe to non-experimental, fix encoder mismatch, remove dead code |
| 2026-03-31 | `040d0342` | Use FP16 dynamic encoder directly, retain palettize6 GPU codepath |
| 2026-03-31 | `f9ed2c5c` | Strip file-based profiling, lazy mask init, cache log2n, remove dead code |
| 2026-03-30 | `ae41e59e` | Fix FFT setup allocation per-frame and unsafe logits mutation |
| 2026-03-30 | `53f32ecf` | Add Cohere Transcribe 2B mixed-precision backend (FP16 encoder + INT8 decoders) |

### `feature/qwen3-postprocessor`

| Date | Commit | Subject |
|---|---|---|
| 2026-04-15 | `28aef33f` | Preserve alpha build errors |
| 2026-04-15 | `9fef9256` | Harden post-processor readiness checks |
| 2026-04-15 | `a8ed1fca` | Address post-processor review polish |
| 2026-04-15 | `e8e2dff7` | Require downloaded cleanup model before enabling |
| 2026-04-15 | `c9f1c0bd` | Discard stale post-processor warmups |
| 2026-04-14 | `65e952c4` | Run filler cleanup after rejected LLM output |
| 2026-04-14 | `8203d157` | Handle cancelled post-processor waiters |
| 2026-04-14 | `b2e042aa` | Address post-processor review guardrails |
| 2026-04-14 | `def0bb3f` | Raise post-processor context guardrails |
| 2026-04-13 | `451c9aca` | Fix post-processor lifecycle races |
| 2026-04-13 | `58eb3a43` | Keep meeting transcription raw |
| 2026-04-13 | `8a77e5fe` | Quiet post-processor diagnostics by default |
| 2026-04-13 | `71893375` | Gate post-processor transcript diagnostics |
| 2026-04-13 | `5e19e4e4` | Harden Qwen3 post-processor runtime |
| 2026-04-13 | `49e1d7a9` | Add Post-Proc v3 (phequals/qwen35-postproc-v3-gguf) |

### `feature/ux/sound_and_color_effects`

| Date | Commit | Subject |
|---|---|---|
| 2026-04-08 | `885c36f7` | Restore dynamic waveform, add global accent color and menu bar icon picker |
| 2026-04-08 | `10a74c42` | refactor: remove dead EQ animation infrastructure from FloatingIndicatorController |
| 2026-04-08 | `c3ae55fe` | Update native/MuesliNative/Sources/MuesliNativeApp/SoundController.swift |
| 2026-04-08 | `8c0fa329` | Update native/MuesliNative/Sources/MuesliNativeApp/FloatingIndicatorController.swift |
| 2026-04-08 | `89a918cb` | feat: sound & visual UX effects (sound, glass pill, SF Symbol icons, color theming) |

### `feature/whisperkit-backend`

| Date | Commit | Subject |
|---|---|---|
| 2026-04-17 | `d775941c` | Fix hideLoading race and scope spinner to whisper backend only |
| 2026-04-17 | `6c60556b` | Address PR review feedback: progress labels, TODO comment, ordering |
| 2026-04-17 | `77e45f97` | Fix isModelDownloaded path for WhisperKit storage layout |
| 2026-04-17 | `d6aa40cd` | Show animated spinner in floating indicator during model warmup |
| 2026-04-17 | `dcb2e2b5` | Add WhisperKit warmup and model-switch loading indicator |
| 2026-04-17 | `8b40dbaa` | Replace SwiftWhisper with WhisperKit for Whisper transcription backend |

### `fix/cmd-shortcuts-text-editing`

| Date | Commit | Subject |
|---|---|---|
| 2026-05-05 | `dfefe72a` | fix: skip hotkey handling when text input has keyboard focus |

### `fix/ui-ux-polish`

| Date | Commit | Subject |
|---|---|---|
| 2026-04-08 | `862d23b0` | Harden model downloads: HTTP validation, retry with backoff, cancellation |
| 2026-04-08 | `f34d7bb6` | Per-folder DB queries, single-sync folder creation, upgrade migration, minor fixes |
| 2026-04-08 | `4e214ba8` | Fix folder snap-back after drag and cap meeting list at 200 records |
| 2026-04-08 | `32d4bdd1` | Use lightweight count queries for sidebar instead of full record scan |
| 2026-04-08 | `f9921b72` | Address PR review: move folder order to config, fix drag UX, cap meeting query |
| 2026-04-08 | `1cd98ce1` | Add dark/light mode toggle to sidebar and standardize settings controls |
| 2026-04-08 | `22cdc76c` | Fix SPM resource bundle packaging, meeting count limit, folder reorder and folder picker UX |

### `join-record-notification-pipeline`

| Date | Commit | Subject |
|---|---|---|
| 2026-04-19 | `f2122178` | Fix P1/P2: fputs leak, title fallback, Zoom icon, stale timer |
| 2026-04-19 | `a0e7a49a` | Event-driven calendar monitoring, Google Calendar sync link |
| 2026-04-18 | `cd9f0559` | Fix P1: guard calendar notification from detection override |
| 2026-04-18 | `b3717507` | Fix P1/P2: timer dictionary, @MainActor, joinAndRecord, title lookup |
| 2026-04-18 | `baf95b5b` | Remove all diagnostic logging from timer investigation |
| 2026-04-18 | `e87cbb58` | Fix P1 onClose reverse race, P2 merge URL loss and cleanup |
| 2026-04-18 | `4455aa08` | Remove diagnostic log from checkUpcomingCalendarNotifications |
| 2026-04-18 | `38bb6938` | Fix P1 onClose race, P2 thread safety and debug logging |
| 2026-04-18 | `a36169b7` | Join & Record notifications, unified calendar notification pipeline |

### `openhands-fix-issue-225`

| Date | Commit | Subject |
|---|---|---|
| 2026-06-19 | `fdb18bf8` | Fix issue #225: Pause media during dictation: already-paused media gets un-paused on press and re-paused on release |

### `openhands-fix-issue-74`

| Date | Commit | Subject |
|---|---|---|
| 2026-04-29 | `c29aedb6` | Fix issue #74: Add support for AI summary using Ollama |

### `openhands-fix-issue-94`

| Date | Commit | Subject |
|---|---|---|
| 2026-05-06 | `a53c72df` | Add edit menu for text shortcuts |
| 2026-05-05 | `0b4b95f4` | Fix hotkey cleanup while editing text |
| 2026-05-05 | `f8a47dc5` | Fix issue #94: bug: Cmd+C/V shortcuts broken when editing meeting notes |

### `openhands/fix-pause-media-during-dictation`

| Date | Commit | Subject |
|---|---|---|
| 2026-06-19 | `8b636e3b` | Fix pause-media-during-dictation un-pausing already-paused media |

### `pHequals7-patch-1`

| Date | Commit | Subject |
|---|---|---|
| 2026-03-17 | `f9a78f54` | Update README.md |

### `revert-227-openhands-fix-issue-225`

| Date | Commit | Subject |
|---|---|---|
| 2026-06-20 | `c347e3be` | Revert "Fix issue #225: Pause media during dictation: already-paused media ge…" |

### `revert-417-codex/dictation-buffer-drain`

| Date | Commit | Subject |
|---|---|---|
| 2026-08-15 | `c87d499f` | Revert "Drain the buffer ring on stop; 512-frame dictation buffers" |

### Older branches with no unreleased commits

- `add-claude-github-actions-1773811119307` (tip 2026-03-18)
- `cleanup/remove-python-legacy` (tip 2026-03-16)
- `codex/dco-contribution-policy` (tip 2026-07-30)
- `codex/dictation-buffer-drain` (tip 2026-08-15)
- `codex/dictionary-auto-add-suggestions` (tip 2026-06-24)
- `codex/fix-browser-audio-meeting-false-positives` (tip 2026-05-26)
- `codex/fix-finalizing-modal-zoom-audio` (tip 2026-05-25)
- `codex/fix-launch-at-login` (tip 2026-05-10)
- `codex/harden-sparkle-update-flow` (tip 2026-04-22)
- `codex/hotfix-0.6.1-spm-bundle-path` (tip 2026-04-20)
- `codex/improve-onboarding-flow` (tip 2026-05-09)
- `codex/indic-asr-coreml-integration` (tip 2026-06-29)
- `codex/meet-notification-polish` (tip 2026-05-01)
- `codex/meeting-mic-degradation-recovery` (tip 2026-08-13)
- `codex/meeting-transcription-recovery` (tip 2026-05-12)
- `codex/onboarding-reliability-polish` (tip 2026-05-03)
- `codex/preprod-0.8.2-preprod.1` (tip 2026-08-15)
- `codex/preprod-0.8.2-preprod.2-appcast` (tip 2026-08-18)
- `codex/preprod-0.8.2-preprod.3-appcast` (tip 2026-08-18)
- `codex/preprod-update-ux` (tip 2026-05-25)
- `codex/repo-housekeeping` (tip 2026-08-19)
- `codex/review-standards` (tip 2026-06-24)
- `codex/stable-aggregate-uid` (tip 2026-08-09)
- `codex/summary-backends-lmstudio-custom-endpoint` (tip 2026-05-26)
- `codex/sync-onboarding-bridge` (tip 2026-06-21)
- `codex/system-audio-tap-health` (tip 2026-08-14)
- `cursor/sync-pr-134-d382` (tip 2026-08-13)
- `cursor/transcribe-join-default-action-1875` (tip 2026-08-19)
- `devin/1782200981-nested-meeting-folders` (tip 2026-06-24)
- `devin/1782343413-upcoming-meetings-window` (tip 2026-06-29)
- `devin/1782663232-auto-export-markdown` (tip 2026-07-02)
- `feat/meeting-folders-and-detection` (tip 2026-03-16)
- `feature/coreaudio-tap-migration` (tip 2026-04-17)
- `feature/meeting-follow-up` (tip 2026-07-09)
- `landing-page-updates` (tip 2026-03-16)
- `native-swift` (tip 2026-03-16)
- `openhands-fix-issue-154` (tip 2026-05-21)
- `openhands-fix-issue-209` (tip 2026-06-20)
- `openhands-fix-issue-74-try2` (tip 2026-05-09)
- `trendshift-badge-readme` (tip 2026-07-09)
- `ui-frontend` (tip 2026-03-14)
