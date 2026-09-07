# Direct IP interface

The v1 web workflow is **Display → Menu**, with Remote and optional Diagnostics. It replaces menu traversal for normal settings. Existing IP Remote profiles/tokens are reused; v0 menus, verification, macros, and calibration files are preserved but do not drive this interface. The macro editor is hidden until it supports direct commands.

For installation, all platform launchers, first pairing, and updates, start with the [README](../README.md). `feature/ip-remote-v1` remains separate from `main`.

## Read, edit, apply

1. Choose a saved display and Connect. Initial reads are `getTVStates`, `getVideoStates`, and the optional `getDeviceInformation` identity query. Connect reuses the token; Pair is the explicit approval flow.
2. Open Menu. The first visit to each section after connecting reads its settings. Getters send the access token without setting values. The 20-point and Custom color grids additionally move their selector to read every row, then restore it. No RGB values or modes change during loading. Missing fields remain **Not reported**; they are not assigned defaults or imported from a saved profile.
3. Edit a control. In Wait for Apply mode, only a pending target changes locally. The current-value label still shows the TV reading. Applying unchanged values does not send redundant setters.
4. Apply sends pending settings sequentially. Each has a fresh preflight, a saved original/target, a single setter, and an independent readback. Successful values stay on the TV. There are no verification checkboxes/pass counts, menu keys, auto-return scripts, or automatic rollback.
5. Apply immediately is a persisted option under the collapsed Update behavior box. Clear/apply pending edits before enabling it. Number/selection/switch edits send when committed; range drags preview locally and send on release.

Only queried, representable values can be edited. This is a current-state requirement, not a manual verification requirement. A documented method that fails in the current display/mode is shown with its response/error and can be retried with Refresh. Unsupported fields do not prevent other fields in the section from loading.

Refresh keeps staged targets, so the user can see pending work. If another controller changes a staged setting, the next Apply refuses to overwrite it using an old baseline: refresh and re-enter the target, or discard the draft. Switching/reconnecting displays clears unsent drafts. The app does not continuously poll or detect external HDMI signal changes in the background.

## Calibration sections

### Expert settings

The Picture category opens here. It includes current picture mode, modern Brightness/Backlight, Contrast, Shadow Detail, Color, Sharpness, Tint, processing, gamma and HDR fields. Other categories contain Sound and System settings, not picture calibration controls.

Input/picture-mode changes can recall other settings. Apply them separately from other drafts, then allow a fresh read before editing the newly active context. Game/Art/calibration-mode changes also invalidate cached settings. The app does not infer which settings a TV stores per signal or picture mode; it asks the TV again.

### 2-point white balance

RGB gains are in one column and RGB offsets in another. Each edit sends only that channel through `WB2PointControl`; omitted channels are not zeroed. The app supports flat getter fields, a `WB2Point` object, or a `WB2Point` JSON-object string, and checks that other returned channels do not unexpectedly change.

### 20-point white balance

1. Open the 20-point tab. Its On/Off control is first.
2. Set On, then Apply (unless using immediate mode).
3. The full grid loads automatically: 5%, 10%, …, 100%, all visible together. Use Load all rows / Reload all rows or Refresh TV values to read it again.
4. Edit Red/Green/Blue in any rows using the slider, −/+, or number input, then Apply. Edits across different rows remain separate. Immediate mode also addresses the exact row automatically.

The RGB getters address the **current interval**, not a full array. The app handles that selector behind the scenes: temporarily select each percentage, query its three values, check context/selection, and restore the initial interval after successful loading. Only queried values are placed into the matching row. This takes more requests than an ordinary refresh; progress and Stop are available throughout. No mode is enabled automatically.

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
- A journal saves originals and targets before each setter. Confirmed earlier changes remain; later steps are not automatically resumed after failure or restart.
- A correlated value rejection can be followed by read-only checks. If the original/context are unchanged, it is marked Rejected unchanged and can be corrected without a false recovery lock.
- An ambiguous write, readback mismatch, or interrupted delivered request requires checking the TV. Refresh remains read-only. Close the interrupted-update review explicitly after checking; this sends no restoration or verification command.
- Transport/authentication failure clears Connected. Connect reuses credentials; pairing is never retried automatically.
- The optional early picture-test/command diagnostics retain their own unresolved-write safeguards. An unfinished experiment must be resolved before normal writes; historical lack of verification does not block Menu.

## Persistence and boundaries

All new state is private under the configured data root's `ip-remote` directory. `menu-preferences.json` stores Apply immediately. `menu-update.json` stores the last update and interrupted-write information, including each row's unique ID and original value. `menu-selector.json` records selector movement before sending, the original/last-confirmed selector, and completion/interruption. Profiles/tokens and diagnostic history retain their existing files. Runtime query caches and unsent targets are not resumed after restart. Diagnostic exports include the Menu snapshot, grid values, and update/selector state with the existing redaction options.

No menu definition or verification file is read/written by the new primary pages. No existing macro/calibration file is deleted or interpreted as a direct-IP script. The older three-control preset workspace remains an optional diagnostic endpoint. The full interval/color grids now have live queried snapshots, but portable direct-IP calibration preset import/export remains future work.

Settings with numeric/enum getters appear in Menu. Apps, discovered devices, channels, and broad power/reboot actions remain explicit in Diagnostics. Remote and header keys send one direct IP key each; they do not predict the on-screen location. A power key clears local connection readiness. Connection status is based on the last request, not a live socket or a heartbeat.

## Implementation and verification

`IpMenuCatalog` maps the typed command catalog into sections and documented bounds. The menu service shares the IP client's cancellation gate and token store, but never calls menu-key automation or capability/pass-count checks. Its staging/apply/readback tests use simulated TVs; Blazor tests dispatch real component events for number/slider changes, Apply, immediate mode, tab selection, navigation visibility, and connection status.

Hardware compatibility still needs ordinary user feedback on each display, but it is not a setup gate. Check a small adjustment first if trying a model-specific field. No real TV settings are changed by the automated suite.
