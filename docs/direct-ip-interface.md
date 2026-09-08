# Direct IP interface

The v1 web workflow is **Display → Menu**, with Communication log and a global right-side Remote drawer. Remote has no separate page or navigation entry: its edge button opens a full-window-height, modeless native popover, preserving the current page, scroll, and edits. The close header stays visible while the buttons scroll independently. Close, Escape, and outside-click dismiss it; it uses the active theme and honors reduced motion. Opening/closing sends no TV requests. It replaces menu traversal for normal settings. Existing IP Remote profiles/tokens are reused; v0 menus, verification, macros, and calibration files are preserved but do not drive this interface. The macro editor is hidden until it supports direct commands. The earlier command/picture-testing pages are no longer exposed.

For installation, all platform launchers, first pairing, and updates, start with the [README](../README.md). `feature/ip-remote-v1` remains separate from `main`.

## Read, edit, apply

1. Choose a saved display and Connect. Initial reads are `getTVStates`, `getVideoStates`, and the optional `getDeviceInformation` identity query, followed by every Menu section and the catalog's remaining documented read/list methods. Existing aggregate results are reused. Connect reuses the token; Pair is the explicit approval flow.
2. Connect also preloads all 20-point percentages and Custom colors when their modes are already enabled, then opens Menu. These grids move their selector to read every row and restore it; no RGB values or modes change during loading. Inactive modes remain unchanged, with unavailable rows reported in settings-load details. Missing fields remain **Not reported**, not defaults or values imported from a saved profile. Tab changes use the loaded values without restarting incomplete/unsupported reads. Stop cancels the preload; Refresh TV values explicitly starts a new load without reconnecting.
3. Edit a control. In Wait for Apply mode, only a pending target changes locally. The current-value label still shows the TV reading. Applying unchanged values does not send redundant setters.
4. Apply sends pending settings sequentially. Each has a fresh preflight, a saved original/target, a single setter, and an independent readback. Successful values stay on the TV. There are no verification checkboxes/pass counts, menu keys, auto-return scripts, or automatic rollback.
5. Apply immediately is a persisted option under the collapsed Update behavior box. Clear/apply pending edits before enabling it. Number/selection/switch edits send when committed; range drags preview locally and send on release.

Only queried, representable values can be edited. This is a current-state requirement, not a manual verification requirement. Controls with an unmet prerequisite remain visible, gray, and disabled, even when their getter fails: Judder Reduction requires Picture Clarity / Auto Motion Plus = Custom, and HDR gamma sliders require the matching Gamma mode. The explanation names that prerequisite and its current value. Applying a local prerequisite reloads its section.

Explicitly unsupported queries, absent fields, and rejected getters without a known unmet prerequisite are hidden from Menu for the current context. The section reports how many controls were hidden and links to their raw exchanges. Refresh reevaluates them; no permanent model blacklist is created. A failed setter does not hide the entire setting. In particular, `−32002` means a generic failure, not proof of permanently unsupported hardware; unmet modes and invalid values can produce it too. See the [firmware error reference](https://github.com/TheFab21/ha-samsungtv-smart/blob/8c7000522b4045b42ff26d129d8d5fe9daf280cb/notes/QN55LS03FAFXZA/IPCONTROL_DECOMPILED.md).

Controls use the compact slider and pill-switch layout: a prominent target value, native slider with round −/+ buttons, and an editable number box. The minimum is immediately to the left of the slider and the maximum immediately to its right. Native slider thumbs/fill remain aligned at both endpoints. The **TV:** label remains the last queried value while an edit is pending. Switches support keyboard focus/Space as well as clicking; a staged toggle still needs Apply unless immediate mode is selected.

**Refresh TV values** explicitly clears cached readings, grids, unavailable-method results, and unsent targets, then reads all settings for the current signal using the existing connection/token. Its baseline comes from fresh TV queries, not the original connection context. This handles an external bit-depth/HDR change even when the HDMI port and picture-mode strings stay identical. Enabled calibration grids are reread and their selectors restored. If 20-point WB is queried Off, this explicit refresh temporarily enables it, reads all 60 RGB values, restores the original interval, and restores/queries Off. The read values remain visible but disabled. No RGB values are written, and inactive Custom color is not enabled. A stopped/failed load can be retried without reconnecting after resolving any pending restoration. Load completion is not reported if the input/mode changes or a temporary mode needs restoration; a value-cache revision detects invalidation even if the context subsequently returns to its original name.

**Refresh section** (and Load/Reload all rows within a calibration tab) reads only that section and keeps staged targets unless the reported input/picture mode changes. If another controller changes a staged setting, the next Apply refuses to overwrite it using an old baseline: re-enter the target or discard the draft. Full refresh, changed reported input/picture mode, and switching/reconnecting displays clear unsent drafts. The app does not continuously poll or detect external HDMI signal changes in the background. Keep the physical signal stable during reads and writes; not every external condition is reported by this protocol.

## Connection lifetime and speed

The IP Remote endpoint uses HTTPS JSON-RPC requests, not a WebSocket event stream. Each request includes the saved access token; pairing is a separate, explicit operation. One owned HTTP client/pool is reused for the exact endpoint and certificate-trust policy. Requests are serialized because interval/color selectors are shared TV state, and some TV implementations reject overlapping connections.

The client sends `Connection: keep-alive`, allows one pooled connection per server, and disables client-side idle/lifetime expiry. TCP keepalive probes are enabled (15-second idle / 5-second interval where supported; otherwise OS defaults), with TCP_NODELAY for small command messages. These probes are not TV commands or a state-polling heartbeat. The TV may close an HTTP connection or time it out despite keep-alive; the next request then needs a fresh TCP/TLS connection but still uses the saved pairing token. Disconnect, profile switching, changed certificate trust, and application shutdown release the old pool. Certificate validation/pinning and per-request cancellation/timeouts still apply.

Each diagnostic exchange reports `NewTlsHandshake` and `ServerClosesConnection`; injected transports may not report handshake reuse. The header tooltip and connection-preload details summarize the latest successful query. This makes it possible to distinguish server-requested closure from client connection reuse without assuming the TV keeps sockets open forever. There is no automatic RPC replay, background settings polling, or re-pairing.

Preloading moves the initial read cost to Connect; it does not eliminate the multiple documented requests needed for indexed values. It only covers the active input/picture context, not other inputs/picture modes. Changing TV context invalidates cached settings. Actual speed and server idle behavior need testing on the display. See [.NET connection-pooling guidance](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines) and the [firmware protocol reference](https://github.com/TheFab21/ha-samsungtv-smart/blob/8c7000522b4045b42ff26d129d8d5fe9daf280cb/notes/QN55LS03FAFXZA/IPCONTROL_DECOMPILED.md).

## Communication log

Open **Communication log** in the sidebar. This replaces the initial command catalog/picture-test UI; existing bookmarks open the log too. Viewing the log never sends queries or commands to the TV.

1. **Live session** follows completed request/reply exchanges as they arrive. It retains the newest 2,000 exchanges, enough to include the initial calibration preload; an overflow notice points to Saved history. Pause freezes the view without stopping recording or the active TV operation. Paging older live entries also pauses the view.
2. **Saved history** reads the private `ip-remote/diagnostics.ndjson` archive, including earlier app runs. It is paged rather than limited to the last 100 requests. Refresh it to see newly appended records. Nothing in the source file is deleted or rewritten.
3. Search by display name, method, message, error code, or payload. Filter Queries/Writes/Pairing or Errors only. Expand a row for separate **TX** and **RX** panels, request ID, timestamp, duration, HTTP/RPC outcome, endpoint, and TLS reuse/server-close information. A missing reply is explicit.
4. Under **Privacy and export**, download **Export filtered log**. The NDJSON download contains every matching exchange from the selected source, not only the visible page. A live export uses the latest retained records even if the view is paused; use Saved history for the full archive. Tokens and token echoes are always redacted. IP/MAC/UUID/serial and certificate SHA-256 redaction default to on for viewing and export; local source records are unchanged. Free-text labels should still be reviewed before sharing.
5. Malformed/incomplete records are reported. Exports refuse to silently omit them; refresh after active requests finish or inspect the original private archive. Browser exports are limited to 20 million characters: narrow the filter for a large archive, or access the private source file directly (it retains identifiers, so do not share it unreviewed).

If a prior testing operation was interrupted, a manual-recovery section preserves its originals, requested values, and known results. After checking/handling the correct TV, explicitly close the recovery record. Closing it sends no TV command and awards no verification. No test controls or automatic restoration are exposed by the log.

## Calibration sections

### Expert settings

The Picture category opens here. It includes current picture mode, modern Brightness/Backlight, Contrast, Shadow Detail, Color, Sharpness, Tint, processing, gamma and HDR fields. Other categories contain Sound and System settings, not picture calibration controls.

Expert settings supports **whole-box drag-and-drop ordering**. Click and drag a heading, value label, or background, then drop at the highlighted edge: before/after the target box. The entire box (or linked group) moves; there are no separate move handles or arrow buttons inside it. The grid flows left to right, then to the next row; in a single-column window it flows top to bottom. Gestures starting on sliders, switches, selections, text inputs, buttons, links, and help toggles retain their normal behavior instead of moving the box. For keyboard ordering, focus the box itself with Tab and use **Alt + Left/Up** for earlier or **Alt + Right/Down** for later (Option on macOS). Arrow keys inside inputs never rearrange boxes. **Reset layout** restores the catalog order.

Controls linked by local prerequisites form one movable group. Gamma and its BT.1886/ST.2084/HLG adjustments stay together, as do Picture Clarity and Blur Reduction/Judder Reduction/LED Clear Motion. Availability rules still hide or disable individual controls; hidden members retain their group, and hidden groups retain their saved position when they return. Newly added groups append after your saved order. Layout changes save in your private `ip-remote/menu-preferences.json`, shared across displays in this app's data directory, not in a menu definition or TV profile. They preserve Apply immediately, current readings, and pending edits and send no TV requests. Moving boxes does not change the order or safety checks used to Apply settings. Other tabs keep their existing layouts.

Input/picture-mode changes can recall other settings. Apply them separately from other drafts, then allow a fresh read before editing the newly active context. Game/Art/calibration-mode changes also invalidate cached settings. The app does not infer which settings a TV stores per signal or picture mode; it asks the TV again.

Local mode changes refresh only their own section. For example, choosing Custom Color Space reloads Color, but retains both white-balance sections and their last-read timestamps. Returning to those already-loaded tabs does not scan them again. Toggling 20-point mode likewise does not discard Custom color or 2-point readings. Gamma/Picture Clarity changes refresh Expert settings without discarding the calibration grids. Disconnecting or a queried input/picture-context change still invalidates cached values. Remote/header key presses keep readings, grids, pending targets, and their timestamps unchanged and send no follow-up reads. Manually Refresh if a key or another controller changed the TV; Apply's fresh-baseline checks still prevent overwriting a changed value using a stale draft.

### 2-point white balance

RGB gains are in one column and RGB offsets in another. Each edit sends one complete `WB2PointControl` request containing all six channels: the edited channel gets your target; the other five keep the values from the immediately preceding TV query. Missing, invalid, or out-of-range peer readings stop the request before sending; cached values and defaults are never substituted. Pending edits to other channels wait for their own step. The app supports flat getter fields, a `WB2Point` object, or a `WB2Point` JSON-object string, and independently checks all six channels after writing.

The observed partial-request behavior differed by channel: an R-Gain-only request changed R-Gain and returned `−32002`, while B-Gain-only requests returned that error without changing B-Gain. This suggests incomplete processing of a six-channel setting, not a connection timeout. The [firmware reference](https://github.com/TheFab21/ha-samsungtv-smart/blob/master/notes/QN55LS03FAFXZA/IPCONTROL_DECOMPILED.md#qn55ls03fafxza-method-reference-and-live-evidence) lists all six flat fields and a working same-value setter on another display. Sending the complete, freshly read block addresses the suspected partial-request problem without guessing a different method or resetting untouched channels; it still requires readback and on-display confirmation on the affected TV. Avoid concurrent white-balance adjustments from another controller while applying.

A `WB2PointControl` write has been observed returning JSON-RPC error `−32002` even though the requested channel changed correctly. For this specific method/error, the app can mark the row **Applied with TV warning**: independent queries must confirm the exact target, all six channels must have usable before/after readings, the other five channels must be unchanged, and the normal context/other-setting checks must pass. The warning appears under the row in Last update and survives restart; the original error remains in Communication log. It reuses the existing post-error readback, with no additional scan or setter retry. Missing/mismatched readbacks, other errors, and transport/authentication failures still stop the update.

An older interrupted record is not retroactively marked applied. Check the actual value and then select **I checked the TV — close interrupted update** in Menu once; refresh before editing again. Closing review does not resend or undo the adjustment.

### 20-point white balance

1. Open the 20-point tab. Its On/Off control is first.
2. Set On, then Apply (unless using immediate mode).
3. The full grid loads automatically: 5%, 10%, …, 100%, all visible together. Use Load all rows / Reload all rows or Refresh section to read just this grid again. These explicit buttons also work while Off, temporarily enabling WB and restoring Off after reading. Refresh TV values does this as part of its all-settings load. Initial Connect, tab visits, and applying Off do not temporarily enable WB.
4. Edit Red/Green/Blue in any rows using the slider, −/+, or number input, then Apply. Edits across different rows remain separate. Immediate mode also addresses the exact row automatically.

The RGB getters address the **current interval**, not a full array. The app handles that selector behind the scenes: temporarily select each percentage, query its three values, check context/selection, and restore the initial interval after successful loading. Only queried values are placed into the matching row. This takes more requests than an ordinary refresh; progress and Stop are available throughout. An Off-state refresh records the original context before enabling, confirms On, saves the original interval before moving it, and confirms the restored Off state before retaining the grid. Unknown/rejected mode queries never trigger a guessed mode change.

The optimized scan queries RGB once per cell, confirms a changed selector before reading, and checks input, picture mode, calibration mode, and selector after each row. That final context check also prepares the next row; unrelated `getVideoStates` values are no longer repeatedly queried between RGB cells. The simulated 20-point scan uses **176 requests instead of 340**, and Custom color uses **67 instead of 119**, including initial reads and successful selector restoration. Actual elapsed time depends on the TV/network. The client also reuses its HTTPS connection when the TV permits keep-alive, without sharing connections across endpoints or changed certificate-trust policies. Full pre/post-write safety checks for Apply are unchanged.

No bulk all-interval/all-color getter was found in the [firmware method reference](https://github.com/TheFab21/ha-samsungtv-smart/blob/8c7000522b4045b42ff26d129d8d5fe9daf280cb/notes/QN55LS03FAFXZA/IPCONTROL_DECOMPILED.md). The app uses documented selector/channel methods rather than guessing a batch API or querying different selectors concurrently. Avoid other controllers changing these settings during a scan.

`getVideoStates` is a small aggregate of ordinary picture values, not a complete calibration dump. `WB2PointControl` already reads all six 2-point channels in one call; 20-point and Custom color use separate selector/channel methods. A JSON-RPC batch is also not the same as an all-values getter: Samsung batch support/order has not been confirmed, and overlapping selector changes could associate values with the wrong percentage/color. First-time grid loading therefore still takes multiple round trips. Keeping unrelated sections loaded avoids paying that cost again merely because you changed a local mode elsewhere.

Apply groups pending edits by row, selects and confirms each required interval, reads the original value again, then sends/checks the requested RGB changes. Edits to other intervals are not overwritten. The selector is restored at the end of the group on success, without undoing RGB adjustments. Another controller changing the input, picture mode, calibration mode, or selected row during an operation stops it. Avoid concurrent TV adjustments while a load or Apply is running.

### Color

Color space mode appears first. Select Custom and Apply. Six fixed rows—Red, Green, Blue, Yellow, Cyan, and Magenta—are shown together, each with independent RGB controls. Loading and Apply use the same selector-addressing and successful-completion restoration as the 20-point grid; there is no separate color selector to operate. Changing Color Space mode or Color Adjustment Point is kept separate from pending grid edits and invalidates their cached readings.

### Gamma and mode-dependent fields

Gamma adjustment requires the matching queried Gamma mode. The app does not assume that bit depth implies HDR or a gamma mode. Choices come from documentation, not a live option-list query; the TV can reject an option not available with the current signal. No other setting is changed automatically to unlock a control.

## Ranges and availability

**No documented min/max/range-discovery query was found.** The getter contracts and referenced responses provide current values (or device/app lists), not numeric ranges. The app therefore uses known command-table limits and labels them as documented—not TV-discovered. It does not probe endpoints or send boundary values to discover limits.

The rendered columns of [Samsung's 2023 IP Command List](https://image-us.samsung.com/SamsungUS/samsungbusiness/resources/pdfs/ip-command-list/IP-Command-List_2023.pdf) confirm the modern consumer-TV bounds below. This improves on the wider historical transport envelope used by the earliest diagnostic controls.

| Setting | Menu limits | Wire field |
| --- | --- | --- |
| Brightness / Backlight | 0…50 | `backlight` |
| Contrast / Color | 0…50 | `contrast` / `color` |
| Sharpness | 0…20 | `sharpness` |
| Shadow Detail | −5…5 | `brightness` |
| Tint | −15…15 | `tint` |
| 2-point gains/offsets and 20-point RGB | −50…50 | `R-Gain` etc.; `WB20P.Red` etc. |
| Gamma adjustments | −3…3 | `gamma.BT1886`, `gamma.ST2084`, `gamma.HLG` |
| Blur/judder reduction | 0…10 | `AMP.blurReduction`, `AMP.judderReduction` |
| Custom color RGB | 0…100 | `colorSpace.Red`, `.Green`, `.Blue` |

The [2020 list](https://www.hillresi.com/wp-content/uploads/2022/01/2020_IP_command_list.pdf) and [consumer-TV firmware method notes](https://github.com/TheFab21/ha-samsungtv-smart/blob/8c7000522b4045b42ff26d129d8d5fe9daf280cb/notes/QN55LS03FAFXZA/IPCONTROL_DECOMPILED.md) provide additional wire spellings/getters. The upstream integration's [number entities](https://github.com/TheFab21/ha-samsungtv-smart/blob/master/custom_components/samsungtv_smart/number.py) also define numeric ranges statically, rather than querying min/max metadata. This supports using documented fallbacks; it is not proof that no Samsung firmware has any private range API.

The 2023 list spells Gamma `2.2`; the 2020 list spells `2.20`. Both exact forms are represented, without treating bit depth as a substitute for the queried mode. Some additional firmware-note enum candidates have only getter evidence or failed setters in the reference state; About this setting retains those qualifications. A successful getter is not proof that every enum value is available.

No pass-count gate is used for documented controls. Actual query failures, invalid local values, missing prerequisites, changed contexts, and uncertain delivered writes are still reported and handled. If a TV reports a numeric value outside the documented control range, it is displayed as returned but not silently clamped or overwritten; inspect/export diagnostics.

## Stop and failure handling

- Stop cancels pending I/O and prevents later settings from being sent. It cannot retract a delivered command.
- Stop also prevents any subsequent selector restoration. A stopped load/update reports the original and last-confirmed selector; no selector operation resumes after restart. Reload to obtain fresh values before continuing. Selector-only interruptions do not imply RGB values were changed.
- A stopped/failed Off-state 20-point read may leave WB On. Its private journal survives restart. Menu's **Restore 20-point WB to Off** checks the original endpoint, input, and picture mode, restores the saved interval when available, then restores/confirms Off without writing RGB. Keep the original physical signal active too, since bit depth is not reported reliably. If you have already restored the original interval and Off manually, confirm that instead; confirmation sends nothing. Uncertain rows are discarded and other writes remain blocked until recovery is resolved. A changed context never receives a blind restoration command.
- A journal saves originals and targets before each setter. Confirmed earlier changes remain; later steps are not automatically resumed after failure or restart.
- A correlated value rejection can be followed by read-only checks. If the original/context are unchanged, it is marked Rejected unchanged and can be corrected without a false recovery lock.
- A **Rejected unchanged** message names the requested and actual value. The controls remain available; correct the pending target, or use **Update behavior → Discard pending changes** before editing something else. This differs from an uncertain delivered write, which requires review.
- The narrow [2-point white-balance exception](#2-point-white-balance) accepts a `−32002` reply only when independent readback proves the requested change with the other channels/context unchanged. Its saved per-row warning remains visible even if a later setting fails; no command is retried.
- An ambiguous write, readback mismatch, or interrupted delivered request requires checking the TV. Ordinary section queries remain read-only; full refresh and calibration-grid loads can move selectors and temporarily enable 20-point WB, so they require resolving the interrupted-write review first. Closing review sends no restoration or verification command.
- Transport/authentication failure clears Connected. Connect reuses credentials; pairing is never retried automatically.
- Existing early picture-test/command records retain unresolved-write safeguards. Resolve an unfinished operation in Communication log's manual-recovery section before normal writes; historical lack of verification does not block Menu.

## Persistence and boundaries

All new state is private under the configured data root's `ip-remote` directory. `menu-preferences.json` stores Apply immediately. `menu-update.json` stores the last update and interrupted-write information, including each row's unique ID, original value, and any readback-confirmed TV warning. `menu-selector.json` records selector movement before sending, the original/last-confirmed selector, and completion/interruption. `white-balance-read.json` records a temporary Off → On read's original display/context, interval when known, and pending/completed restoration. Profiles/tokens and diagnostic history retain their existing files. Runtime query caches and unsent targets are not resumed after restart. Communication exports contain the filtered request/reply records with the selected redactions.

No menu definition or verification file is read/written by the new primary pages. No existing macro/calibration file is deleted or interpreted as a direct-IP script. The older three-control testing workspace is no longer exposed. The full interval/color grids now have live queried snapshots, but portable direct-IP calibration preset import/export remains future work.

Settings with usable numeric/enum getters appear in Menu. Discovery replies and other catalog reads can be inspected in the communication log; the log has no command execution controls. Remote and header keys send one direct IP key each; they do not predict the on-screen location. A power key clears local connection readiness. Connection status is based on the last request, not a live socket or a heartbeat.

## Implementation and verification

`IpMenuCatalog` maps the typed command catalog into sections and documented bounds. The menu service shares the IP client's cancellation gate and token store, but never calls menu-key automation or capability/pass-count checks. Its staging/apply/readback tests use simulated TVs; Blazor tests dispatch real component events for number/slider changes, Apply, immediate mode, tab selection, navigation visibility, and connection status.

Hardware compatibility still needs ordinary user feedback on each display, but it is not a setup gate. Check a small adjustment first if trying a model-specific field. No real TV settings are changed by the automated suite.

Run `dotnet test SamsungController.sln` for service/component regression tests. With Node.js 22 or newer, run `node --test tests/browser/expert-layout.test.cjs` for the standalone drag-handler tests (DOM doubles, no real browser/TV). The layout tests cover prerequisite grouping, persistence, hidden controls, failed saves, whole-box dragging, keyboard shortcuts, protecting interactive controls, cancellation, and preserving staged settings. Actual pointer feel and layout still benefit from a browser check.
