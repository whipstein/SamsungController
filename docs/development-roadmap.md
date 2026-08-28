# Development roadmap

This document preserves the implementation plan and research direction that are useful to contributors. The main [README](../README.md) is intentionally limited to installing and using SamsungController.

Status was reconciled with the repository on 2026-08-28. A checked box means the capability is present in the code and covered by automated tests where practical; it does not imply that every Samsung model or firmware has been tested.

## Completed foundation

### Repository and platform baseline

- [x] Create a Git repository and .NET solution with separate Core, Automation, CLI, Web, and test projects.
- [x] Target plain `net10.0` without a platform-specific desktop UI dependency.
- [x] Build and run fake-transport tests on macOS, Windows, and Linux in CI.
- [x] Keep application configuration human-readable and store user state outside the source tree.
- [x] Exclude normal token and session-log paths from source control.

### Samsung transport and pairing

- [x] Define an `ISamsungTransport` abstraction independent of console and browser UI.
- [x] Implement Samsung WebSocket connections with `ClientWebSocket`.
- [x] Support secure port 8002 and non-secure port 8001 with explicit overrides.
- [x] Base64-encode the controller name and parse Samsung pairing responses.
- [x] Persist tokens by TV host and reuse them on later connections.
- [x] Expose connection states including connecting, pairing, connected, reconnecting, faulted, and disconnected.
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
- [x] Remember the selected macro catalog and allow validated macros in persistent quick access.

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

- [x] Create new TV interface definitions entirely from the web UI.
- [x] Define, edit, delete, sort, and custom-order the overall menu tree before recording routes.
- [x] Distinguish unrecorded tree nodes in red and recorded-but-unverified nodes in yellow.
- [x] Identify transitions by source and target instead of requiring a manual transition ID.
- [x] Send buttons to the TV while recording and persist successful commands into draft YAML.
- [x] Cancel, clear, undo, replace, and re-record a capture without hand-editing YAML.
- [x] Record forward traversal and target-specific return-to-video keys as one integrated command.
- [x] Configure default menu-root, deeper-menu, and exact-state return scripts.
- [x] Use a recorded return sequence while preparing the source during verification.
- [x] Configure system-wide default, screen-change, and return delays.
- [x] Test the system-wide timing profile against a selected traversal.
- [x] Override the post-button wait for individual recorded steps.
- [x] Require three user-confirmed visual passes before exposing a command as verified.
- [x] Preserve separate verification counts while switching between draft commands.
- [x] Set the expected current position after each accepted pass and return to video after the third.
- [x] Collapse authoring sections by default and reopen them when contained state requires validation.
- [x] Remove incorrect verified settings with dependency-aware safeguards.

### Read-only protocol research

- [x] Send known `ed.edenApp.get` and `ed.installedApp.get` query envelopes.
- [x] Preserve supported responses and the absence of a response as observable results.
- [x] Capture labeled `/api/v2/` snapshots while the TV is active or the WebSocket is disconnected.
- [x] Compare, retain, redact, and export device-state snapshots.

## In progress

- [ ] Complete and reverify the S95F menu map for every intended picture setting.
- [ ] Record separate contexts where firmware, input/source, SDR/HDR signal, or picture mode changes traversal behavior.
- [ ] Keep real-TV observations in `docs/research-notes.md` and promote assumptions only after repeatable evidence.
- [ ] Expand automated menu-planning fixtures as new tree shapes and state-specific return rules are discovered.
- [ ] Reconcile older empirical questions in `docs/architecture.md` with the completed S95F observations.

## Remaining product work

### Device discovery and live state

- [ ] Discover compatible Samsung TVs automatically on the local network while preserving manual IP entry.
- [ ] Evaluate reliable, read-only ways to identify the active application, input/source, picture mode, and power state.
- [ ] Feed trustworthy device state into navigation context without presenting inference as TV-reported fact.
- [ ] Support multiple saved TV profiles without conflating their tokens, menu definitions, or timing profiles.

### Macro management

- [ ] Add a browser macro editor with create, duplicate, edit, delete, import, and export workflows.
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
- [ ] Continue to use verified menu navigation when a safe direct API is unavailable.
- [ ] Keep service-menu and potentially destructive operations out of normal remote and macro workflows.

### Safety model

- [ ] Classify commands and capabilities as Normal, Advanced, Service, Experimental, or Potentially destructive.
- [ ] Require progressively stronger opt-in and warnings for higher-risk classifications.
- [ ] Add read-before-write diagnostics and an auditable change record where the protocol permits it.
- [ ] Define recovery guidance before exposing any experimental setting write.

### Packaging and remote access

- [ ] Produce versioned release artifacts for macOS, Windows, and Linux so end users do not need a source checkout.
- [ ] Decide whether releases should be framework-dependent, self-contained, or both.
- [ ] Add upgrade and configuration-migration handling before distributing installers or packaged services.
- [ ] Design an explicit opt-in LAN mode for tablets or other computers.
- [ ] Add authentication, request forgery protection, TLS guidance, network binding controls, and threat documentation before enabling LAN access.
- [ ] Keep loopback-only hosting as the default.

### Broader compatibility and usability

- [ ] Validate pairing, keys, and protocol behavior on additional Samsung models and firmware releases.
- [ ] Add shareable menu-definition profiles with provenance and exact compatibility metadata.
- [ ] Improve accessibility and responsive behavior for small screens without weakening localhost security.
- [ ] Add a guided backup and restore flow for settings, tokens, macros, and menu definitions with clear secret handling.

## Recommended next sequence

1. Finish the intended S95F menu tree and collect three-pass results for each supported context.
2. Convert sanitized session captures into deterministic replay tests before expanding protocol parsing.
3. Extend read-only device and application research, clearly separating confirmed responses from inference.
4. Prototype macro-plan preview and browser-based macro editing without adding new execution semantics.
5. Define the capability and safety-classification model needed for direct picture reads and writes.
6. Investigate direct picture reads first; expose writes only when validation, bounds, and recovery are in place.
7. Package signed or checksummed release artifacts, then separately design authenticated opt-in LAN access.

## Contributor references

- [Architecture, transport, tokens, logging, and assumptions](architecture.md)
- [Protocol observations and research envelopes](protocol.md)
- [Macro engine format and validation](macros.md)
- [Menu model, confidence, YAML, and authoring workflow](menu-model.md)
- [Real-TV research log](research-notes.md)
- [Current S95F draft definition](../samples/menus/s95f-draft.yaml)
