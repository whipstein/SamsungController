# Development roadmap

This document preserves the implementation plan and research direction that are useful to contributors. The main [README](../README.md) is intentionally limited to installing and using SamsungController.

Status was reconciled with the repository on 2026-08-30. A checked box means the capability is present in the code and covered by automated tests where practical; it does not imply that every Samsung model or firmware has been tested.

## v1 IP Remote — default interface

Development and real-display feedback began on `feature/ip-remote-v1` at `1.0.0-alpha.1`. With user approval, that work is now merged into `main` for v1.0.0. See the [current workflow](direct-ip-interface.md). Earlier guarded milestones remain development history, not current Menu access requirements.

- [x] Promote the direct-IP interface to main and version 1.0.0.
- [x] Add self-contained background app launchers, explicit Quit, private shutdown authorization, and package smoke tests for Windows/macOS/Linux.
- [x] Add Developer ID signing/notarization/stapling tooling for Mac release packages without exporting private keys to CI.

- [x] Replace the primary web workflow with Display, direct Menu, direct Remote, and optional Diagnostics; hide the menu builder, verification pages, and macro editor while preserving their files.
- [x] Populate current controls from base/dedicated TV queries; make documented settings usable without capability/pass-count gates.
- [x] Group direct settings into Picture/Sound/System and calibration tabs, with two-point gains/offsets grouped separately.
- [x] Add direct multi-setting Apply, persisted immediate-mode preference, before/after queries, Stop, rejection handling, and private interrupted-write recovery.
- [x] Investigate range metadata: no documented range query found; use and label 2023 command-table limits, not guessed defaults or boundary probing.
- [x] Cover the cutover with simulated service and Blazor event tests; update installation/use documentation.
- [ ] User exercises the replacement interface on real displays, especially advanced calibration and mode-dependent controls.
- [x] Restore simultaneous 20-point and Custom color RGB grids using queried per-row values, explicit selector routing/restoration, staged/immediate updates, and cancellation/restart tests.
- [x] Restore compact native sliders/pill switches; accelerate grid reads with shared context checks and endpoint/trust-scoped HTTPS connection reuse, covered by request-count and loopback TLS tests.
- [x] Preload all Menu sections, other documented read/list methods, and already-active calibration grids on Connect; report unsupported/missing values and avoid tab-triggered retries after Stop.
- [x] Request persistent HTTPS connections with no client-side idle expiry, TCP keepalive, explicit disconnect, and handshake/server-close diagnostics; test connection reuse and server closure using loopback TLS.
- [ ] Enable and restore inactive calibration modes during Connect only with user approval, so off-mode 20-point and non-Custom color banks can also preload; preserve recovery information if interrupted.
- [x] Preserve unrelated calibration caches across local mode changes, prevent unnecessary rescans when revisiting loaded tabs, and label slider minimum/maximum at the track ends.
- [x] Hide currently query-rejected/absent controls, render prerequisite-dependent controls gray/disabled, and reevaluate on refresh without a permanent unsupported blacklist.
- [x] Replace testing-page routes with a live/persisted communication log, search/filter/pagination, redacted full-filter NDJSON export, and manual recovery for retained interrupted operations.
- [ ] Design direct-IP macro editing and portable full calibration preset import/export.

- [x] Add an independent HTTPS JSON-RPC client with explicit pairing and a two-getter read-only allowlist.
- [x] Keep endpoint-scoped credentials, user-entered test context, and redacted observations separate from WebSocket tokens, calibration values, and menu verification.
- [x] Provide a guided diagnostic page with cancellation, strict/pinned/explicitly opted-in TLS, field comparisons, and downloadable reports.
- [x] Test protocol envelopes, errors, redaction, profile isolation, cancellation, and page interactions using simulated replies.
- [x] Confirm pairing, token reuse after restart, and contrast readback 45 → 44 → 45 on S95F firmware 1301 in the reported HDMI4 / FilmmakerMode / annotated RGB 8-bit context (2026-09-07).
- [ ] Confirm pairing, token reuse, and actual getter responses on each target display/firmware.
- [ ] Verify field meaning and context effects against manual TV changes, including Brightness versus Shadow Detail.
- [x] Implement an explicitly confirmed one-step contrast experiment with fresh context checks, visual pass/fail, restoration readback, private crash-recovery journal, and offline safety/page tests.
- [x] Verify the contrast write and restoration on S95F firmware 1301: direct 45 → 44 → 45, independent readbacks, visual pass, stable HDMI4/FilmmakerMode and other reported fields (2026-09-07).
- [x] Add private, context-keyed contrast read/write evidence and a direct contrast editor with explicit Apply/readback and optional Undo. Preserve completed evidence independently of later adjustments; recover interrupted writes without replaying them.
- [x] User confirms direct Contrast Apply works (2026-09-07); preserve its conditions checkbox during target editing and remove extra Apply/Undo browser prompts.
- [x] Implement separate guarded Color and Sharpness verification/restoration and capability-gated direct adjustment, including a 0 → 1 → 0 test, field-specific recovery, and persistence of existing Contrast evidence.
- [x] User confirms Color and Sharpness tests work on the current display (2026-09-07); the subsequent interface cutover removes manual verification gates for documented settings.
- [x] Add a combined direct-picture workspace: read all supported values with two getters, stage multiple changes, serialize Apply with preflight/readback per setting, Stop, persist originals/progress, and stage previous values explicitly.
- [x] Add portable JSON picture presets with strict context/format validation; upload stages targets only and never imports TV readings or verification.
- [x] Test workspace/preset page interactions and batch cancellation, partial failure, stale/context drift, storage failure, and restart recovery using simulated displays.
- [ ] User verifies combined multi-setting Apply, previous-values restoration, preset staging, and Stop on the real display; confirm optional single-control Undo separately.
- [x] Reproduce Sharpness 74 / RPC −32002 and handle explicit rejections with read-only unchanged-state checks, preserving recovery for ambiguous/changed results.
- [x] Expose and persist actual user-defined picture ranges without changing menu definitions or losing existing capability evidence.
- [x] Implement a closed catalog for the 69 cataloged IP method families, typed parameters, device-list queries, and an explicit prepare/send/review UI with durable baseline/history and no automatic retry/rollback/resume.
- [x] Test protocol shapes, invalid values, redaction, command preflight/readback, review persistence, and UI interactions offline.
- [ ] Verify the expanded catalog on each actual display/context; confirm modern Brightness/Shadow Detail/Tint mapping and advanced calibration read/write behavior.
- [x] Extend the catalog using the 2020 list and 2025 firmware notes: dedicated calibration getters, partial two-point updates and guarded interval/color/mode prerequisites.
- [ ] Expand proven controls and context-aware polling; investigate advanced white balance/custom color independently.
- [ ] Merge into `main` only when the owner explicitly approves.

## Completed foundation

### Repository and platform baseline

- [x] Create a Git repository and .NET solution with separate Core, Automation, CLI, Web, and test projects.
- [x] Target plain `net10.0` without a platform-specific desktop UI dependency.
- [x] Build and run fake-transport tests on macOS, Windows, and Linux in CI.
- [x] Keep application configuration human-readable and store user state outside the source tree.
- [x] Exclude normal token and session-log paths from source control.
- [x] Document the complete raw menu-definition topology and available settings with equivalent YAML and JSON examples.

### Samsung transport and pairing

- [x] Define an `ISamsungTransport` abstraction independent of console and browser UI.
- [x] Implement Samsung WebSocket connections with `ClientWebSocket`.
- [x] Support secure port 8002 and non-secure port 8001 with explicit overrides.
- [x] Base64-encode the controller name and parse Samsung pairing responses.
- [x] Persist tokens by TV host and reuse them on later connections.
- [x] Expose connection states including connecting, pairing, connected, reconnecting, faulted, and disconnected.
- [x] Verify idle WebSockets with PING/PONG and expose a warming state before first-command delivery.
- [x] Refresh the authorized channel after configurable idle time while queuing one command and never retrying an ambiguous key send.
- [x] Provide bounded reconnect behavior in the reusable core client.
- [x] Make the web UI disconnect immediately when its TV channel is lost instead of presenting a stale session.
- [x] Implement `status` and host-specific `forget` operations.

### Remote keys and protocol capture

- [x] Send `ms.remote.control` keys using `Click`, `Press`, and `Release`.
- [x] Permit arbitrary Samsung key strings for controlled research.
- [x] Record timestamp, direction, channel, event or method, parsed payload, raw JSON, parse failure, and connection generation.
- [x] Preserve unknown and malformed Samsung messages instead of discarding them.
- [x] Write complete connected sessions as NDJSON.
- [x] Expose passive listening and a long-lived interactive console.
- [x] Add cross-platform console line editing and privacy-filtered persistent history.

### Macro engine

- [x] Parse hierarchical YAML macro catalogs.
- [x] Support scalar variables, repeated keys, explicit delays, nested macro calls, and Click/Press/Release.
- [x] Validate the complete expanded plan before sending its first key.
- [x] Reject recursive calls, invalid variables, unsafe expansion sizes, and invalid delays or repeat counts.
- [x] Report operation-by-operation progress and support cancellation.
- [x] Attempt a best-effort matching Release when cancellation interrupts a held key.
- [x] Run and validate macros from the CLI, interactive console, and web UI.
- [x] Remember the selected macro catalog and allow behaviorally verified macros in persistent quick access.
- [x] Create, duplicate, edit, delete, import, and export macro catalogs from the browser.
- [x] Persist independent three-pass visual verification and revoke it after behavioral edits or failed replays.
- [x] Capture successfully sent remote buttons directly into a macro draft with configurable replay waits.
- [x] Call verified menu destinations from macros through the current-state-aware navigation planner.
- [x] Declare and visibly prepare verified starting TV states for root and nested macros.
- [x] Launch and capture verified menu calls beside the macro remote, with pause/resume capture for unrecorded repositioning.
- [x] Delete macros directly from the catalog list and optionally require a web confirmation before Replay or quick-access execution.

### Local web control surface

- [x] Build a localhost-only ASP.NET Core Blazor application.
- [x] Share one TV session across Connection, Remote, Macros, Menu, Build & Verify, and Protocol pages.
- [x] Provide persistent connection controls, quick-access actions, and a legible expected-current-menu label.
- [x] Pair and reconnect with the same settings and token store as the CLI.
- [x] Send common and arbitrary keys from the Remote page.
- [x] Load, validate, run, cancel, and pin catalog macros.
- [x] Search, filter, copy, clear, and export live protocol messages.
- [x] Redact pairing tokens separately from UUID, MAC, and IP address values in browser displays and exports.
- [x] Gate the raw JSON sender behind an explicit developer-mode checkbox.

### Data-driven menu navigation

- [x] Model menu positions as nodes, directed transitions, deterministic anchors, and conditional TV contexts.
- [x] Track expected menu state with Unknown, Low, Probable, and Synchronized confidence.
- [x] Keep unverified routes out of ordinary Menu navigation.
- [x] Plan verified routes from the expected current position, including relative sibling and ancestor traversal.
- [x] Prefer the shortest safe route and avoid returning to normal video when a verified direct route exists.
- [x] Resynchronize through verified anchors when the current position is unknown.
- [x] Run the preferred verified known-state anchor after a successful web connection.
- [x] Mark expected position unknown after unmodeled menu keys, cancellation, partial failure, or a reported visual failure.
- [x] Capture failed traversal keys for diagnosis and return to normal video afterward.

### Menu authoring and validation

- [x] Create new TV menu profiles entirely from the web UI.
- [x] Read and write menu definitions as YAML or JSON through one validated schema, default new profiles to YAML, and preserve the selected format on later saves.
- [x] Inspect one menu file or whole catalogs from the web UI and CLI with shared parsing, calculated-route regeneration, actionable source diagnostics, warnings, counts, and scriptable exit codes.
- [x] Define, edit, delete, sort, and custom-order the overall menu tree before recording routes.
- [x] Bulk-create or synchronize a full menu branch from an indented outline with stable-ID reuse and a safe change preview.
- [x] Describe rows as sub-menus, bounded sliders, selections, submenu selections, indexed selection grids, switches, confirmation dialogs, or non-activated TV functions with the appropriate defaults, ordered choices, and permanent or conditional disabled/gray rules.
- [x] Model conditionally hidden rows and descendants and recalculate live sibling offsets from predicted setting values.
- [x] Store named settings-dependent menu configurations in one TV definition file and scope route verification/navigation to the active configuration.
- [x] Distinguish unrecorded tree nodes in red and recorded-but-unverified nodes in yellow.
- [x] Identify transitions by source and target instead of requiring a manual transition ID.
- [x] Send buttons to the TV while recording and persist successful commands into the draft definition file.
- [x] Cancel, clear, undo, replace, and re-record a capture without hand-editing the definition file.
- [x] Record forward traversal and target-specific return-to-video keys as one integrated command.
- [x] Configure default menu-root, deeper-menu, and exact-state return scripts.
- [x] Use a recorded return sequence while preparing the source during verification.
- [x] Configure separate system-wide Up/Down, Left/Right adjustment, screen-change, and Return delays.
- [x] Test the system-wide timing profile against a selected traversal.
- [x] Override the post-button wait for individual recorded steps.
- [x] Require three user-confirmed visual passes before exposing a command as verified.
- [x] Preserve separate verification counts while switching between draft commands.
- [x] Set the expected current position after each accepted pass and return to video after the third.
- [x] Collapse authoring sections by default and reopen them when contained state requires validation.
- [x] Remove incorrect verified settings with dependency-aware safeguards.
- [x] Expose sliders, switches, and selections from every menu area in a top-level tabbed, section-grouped Menu page after full display verification.
- [x] Separate command-free current-value entry from TV updates and keep Expert Settings, grouped 2pt gains/offsets, full 20pt RGB sliders, and full color RGB sliders in dedicated tabs with each tab's controlling switch/dropdown first.
- [x] Apply conditional enablement in dependency order and derive enabled-child routes from verified containing sections.
- [x] Promote shared slider behavior through three distinct guided Display Verification confirmations instead of verifying every slider.
- [x] Promote TV-specific selection behavior through one guided confirmation per interaction type while fingerprinting every declared option list.
- [x] Support submenu selections that choose from an ordered value list and return to the containing menu afterward.
- [x] Present 20 Point percentages and Custom Color choices as fixed multi-slider grids.
- [x] Persist a local desired-value profile and reapply it after an explicitly confirmed verified factory-reset command.
- [x] Import/export portable calibration values as either staged targets or command-free current-TV baselines; provide confirmed reset-to-defaults and reset/reapply workflows; and make long Apply/reset batches cancellable at the remote-key boundary.
- [x] Store every external-input combination in one portable calibration file; load once, automatically select matching current/target values, and retain separate display-scoped baselines and indexed values across restart.
- [x] Declare inheritable per-setting saved-value contexts (including Picture Mode), independent of visibility and conditional defaults; share values across irrelevant signals, retain indexed cells, support version-3 multi-context calibration files, and review ambiguous older records without silently choosing values.
- [x] Derive a compact verification checklist using topology branches, shared control behaviors, and representative disabled/hidden behavior classes instead of per-control line items.
- [x] Bind verification evidence to the exact display/context combination with per-check fingerprints and reopen only checks affected by a later definition edit.
- [x] Provide one Display Verification workspace and an always-visible app status that clearly distinguish fully verified from verification-required profiles.
- [x] Restore guided verification adjustments and prerequisite controls to their prior values before exiting the menu.
- [x] Exclude reset/destructive confirmations from required representative verification.
- [x] Save and load named current-TV value states locally without sending TV commands.
- [x] Keep distributable menu structures in a tracked repository catalog while storing display verification and saved TV states locally.
- [x] Auto-discover local, repository, and packaged menu structures and select their configurations from the Connection page.
- [x] Consolidate everyday settings into Menu, remove the separate navigator page, and hide all controls until the complete menu/display profile is current.

### Read-only protocol research

- [x] Send known `ed.edenApp.get` and `ed.installedApp.get` query envelopes.
- [x] Preserve supported responses and the absence of a response as observable results.
- [x] Capture labeled `/api/v2/` snapshots while the TV is active or the WebSocket is disconnected.
- [x] Compare, retain, redact, and export device-state snapshots.

## In progress

- [ ] Complete and reverify the local TV menu map for every intended picture setting.
- [x] Simplify menu context to firmware only; identify menus and verification by model/firmware and keep setting conditions and external states separate.
- [ ] Keep real-TV observations in ignored `user-data/research-notes.md` and publish only deliberately sanitized conclusions.
- [ ] Expand automated menu-planning fixtures as new tree shapes and state-specific return rules are discovered.
- [ ] Reconcile empirical questions in `docs/architecture.md` with sanitized, repeatable observations.

## Remaining product work

### Device discovery and live state

- [ ] Discover compatible Samsung TVs automatically on the local network while preserving manual IP entry.
- [ ] Evaluate reliable, read-only ways to identify the active application, input/source, picture mode, and power state.
- [ ] Feed trustworthy device state into navigation context without presenting inference as TV-reported fact.
- [x] Support multiple saved display definitions with host-keyed tokens and references to independent user/repository/installation menu and timing profiles, without changing display-verification identity.

### Macro management

- [ ] Add conditional macro steps only after their inputs and failure behavior are explicitly modeled.
- [ ] Add a dry-run or expanded-plan preview that can be reviewed without connecting to a TV.
- [ ] Consider macro-level validation records for model, firmware, and context rather than treating syntax validity as behavioral verification.

### Protocol research and replay

- [ ] Replay recorded NDJSON sessions into parsers and regression tests.
- [ ] Maintain sanitized protocol fixtures that exercise unknown events, malformed JSON, pairing variants, and reconnect generations.
- [ ] Expand read/query experiments for device, application, input, picture, and service-state information.
- [ ] Document confirmed firmware-specific responses without generalizing one television's behavior.

### Picture and calibration control

- [ ] Investigate whether current firmware exposes supported direct read APIs for picture settings.
- [ ] Start with read-only diagnostics for picture mode, brightness, contrast, color, tint, color tone, gamma, shadow detail, peak brightness, contrast enhancer, white balance, color space, and HDR-related controls.
- [ ] Define typed capabilities and value ranges per model, firmware, mode, and signal context.
- [ ] Add direct writes only after reads, rollback behavior, and repeatable validation are understood.
- [x] Continue to use verified menu navigation when a safe direct API is unavailable.
- [ ] Keep service-menu and potentially destructive operations out of normal remote and macro workflows.

### Safety model

- [ ] Classify commands and capabilities as Normal, Advanced, Service, Experimental, or Potentially destructive.
- [ ] Require progressively stronger opt-in and warnings for higher-risk classifications.
- [ ] Add read-before-write diagnostics and an auditable change record where the protocol permits it.
- [ ] Define recovery guidance before exposing any experimental setting write.

### Packaging and remote access

- [x] Produce versioned release artifacts for macOS, Windows, and Linux so end users do not need a source checkout.
- [x] Publish self-contained x64 and Arm64 packages with a platform launcher and optional CLI.
- [ ] Add upgrade and configuration-migration handling before distributing installers or packaged services.
- [ ] Design an explicit opt-in LAN mode for tablets or other computers.
- [ ] Add authentication, request forgery protection, TLS guidance, network binding controls, and threat documentation before enabling LAN access.
- [x] Keep loopback-only hosting as the default.

### Broader compatibility and usability

- [ ] Validate pairing, keys, and protocol behavior on additional Samsung models and firmware releases.
- [ ] Add shareable menu-definition profiles with provenance and exact compatibility metadata.
- [ ] Improve accessibility and responsive behavior for small screens without weakening localhost security.
- [ ] Add a guided backup and restore flow for settings, tokens, macros, and menu definitions with clear secret handling.

## Recommended next sequence

1. Finish the intended local TV menu tree and collect three-pass results for each supported context.
2. Convert sanitized session captures into deterministic replay tests before expanding protocol parsing.
3. Extend read-only device and application research, clearly separating confirmed responses from inference.
4. Add an expanded macro-plan/dry-run preview without adding new execution semantics.
5. Define the capability and safety-classification model needed for direct picture reads and writes.
6. Investigate direct picture reads first; expose writes only when validation, bounds, and recovery are in place.
7. Add upgrade and configuration-migration handling for packaged releases, then separately design authenticated opt-in LAN access.

## Contributor references

- [Architecture, transport, tokens, logging, and assumptions](architecture.md)
- [Protocol observations and research envelopes](protocol.md)
- [Macro engine format and validation](macros.md)
- [Menu model, confidence, and authoring workflow](menu-model.md)
- [Raw YAML/JSON menu-definition format tutorial](menu-definition-file-format.md)
- [Generic research template](research-notes.md)
- [Generic menu-definition template](../samples/menus/menu.example.yaml)
