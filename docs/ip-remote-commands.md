# IP Commands: documented methods and controlled testing

This working-branch feature expands IP Remote beyond the three established picture controls. It implements the **24 documented command families**, not every menu item or a guarantee that all methods work on every Samsung display. Existing Contrast/Color/Sharpness verification remains separate and cannot be bypassed from this page.

The catalog is based on [Samsung's IP command list](https://s7d2.scene7.com/is/content/SamsungUS/samsungbusiness/products/tvs/tvci-8-21-17/resource-center/control-codes/TV_IP_CommandList_v.1.1_1Pager.pdf). Parameter spelling, modern enum candidates, and device-list response shapes were cross-checked against the [reference client's implementation](https://github.com/iloveicedgreentea/py-samsungtv/blob/master/pysamsungtv/client.py) and [its enum definitions](https://github.com/iloveicedgreentea/py-samsungtv/blob/master/pysamsungtv/enums.py). These are protocol candidates, not measurements of your TV's ranges or capabilities. No arbitrary method/parameter editor is provided.

## Start here

1. Build and restart the server using the [preview setup guide](ip-remote-preview.md#1-start-the-working-branch).
2. In **IP Remote · Preview**, select the paired display and enter accurate model, firmware, input, picture mode, and signal annotations. Reuse the existing token; do not pair again just to use this update.
3. Open **IP Commands** from the navigation or [http://127.0.0.1:5050/ip-commands](http://127.0.0.1:5050/ip-commands). Opening the page or choosing a command sends nothing.
4. Select **TV state** or **Video state**, then press **Read/list** to confirm communication. Failed/unsupported methods show their actual outcome; no substitute command is tried.

The existing WebSocket connection indicator, quick-access keys, and predicted menu position are separate. IP Commands does not update that predicted position or invoke the old menu-key workflow automatically. Avoid other controllers or manual changes during a prepared test.

## Test a command

1. Select a command family and enter its parameters. Choices are documented candidates, not a live list of values available on your display. Empty, malformed, unknown, or out-of-envelope parameters are rejected before sending.
2. Check the actual TV and note the original value. Use a small, reversible change first. **Brightness protocol field** is not automatically mapped to modern Brightness/backlight: it may represent another adjustment. Its documented envelope is −5…5. Tint's historical envelope is 0…100; that does not establish the modern TV's signed range. If the actual mapping/range is unclear, collect manual-change readbacks before testing a write.
3. Press **Prepare command (read only)**. The app reads TV/video state and saves the exact parameters and baseline privately. It does not execute the selected command.
4. Review the parameters and baseline, then check the explicit conditions/effects box. Changing a parameter requires preparation again. A prepared baseline expires after two minutes.
5. Press **Send prepared command once**. The app rechecks the entire reported baseline, saves that the command may be sent, and sends once. Changed state stops the write and requires new preparation.
6. Where there is a known readback field, the app reads independently and compares it with the requested value. Power, remote keys and app launching have no automatic post-action read; they may close/change the display or connection. No command is retried automatically.
7. Inspect the actual TV. **Observed expected effect — keep** records your confirmation and keeps the result on the TV. There is no automatic restoration. If it failed, or you do not want the changed state, check/restore it using the physical remote and press **Checked / handled manually — close without verification**. Closing review sends nothing.
8. To restore a setting through IP Commands, finish the current review, enter its original value, and prepare/send that as a new explicit command. The before-state details retain the original value. Do not attempt this if the original value, availability or mapping is uncertain.

An acknowledgment is not verification. The page distinguishes:

- **Readback + user verified:** acknowledged command, a different known original value, matching independent field readback, and positive visual confirmation. Already-at-target values are not sent as a test, and an unknown original cannot establish a verified write. Brightness/Tint tests also reject changes to other fields/context.
- **User-confirmed effect only:** the user observed the expected effect, but no independent target readback is available. It is not labeled read/write verified.
- **Mismatch/unsuccessful/unverified:** cannot count as a successful read/write test. Resolve any review before more writes.

Evidence includes the exact command parameters, original profile annotations, and before/after state. A pass is not proof of every value or other display/context. The coverage table shows the latest reviewed test for each method in the selected annotated profile; consult the saved report for its precise parameters and original TV-reported context. A later failed test remains visible instead of being hidden by an older pass.

## Available families

| Group | Methods | Use/limits |
| --- | --- | --- |
| Status | `getTVStates`, `getVideoStates` | Explicit read-only snapshots. |
| Established picture | `contrastControl`, `colorControl`, `sharpnessControl` | Links to the existing verification/range-checked picture workspace; not a second unrestricted writer. |
| Experimental picture fields | `brightnessControl`, `tintControl` | Typed integer tests; actual mapping/range needs confirmation. |
| Picture modes | `pictureModeControl`, `pictureSizeControl` | Documented choices; may change availability or recall other settings. |
| Sound | `directVolumeControl`, `volumeUpDnControl`, `muteControl`, `soundModeControl`, `speakerSelectControl` | Volume/enum controls; external audio support must be tested. |
| External speakers | `externalSpeakerControl` | Read/list first; select an actual returned ID/name pair. |
| Sources | `inputSourceControl`, `USBSourceControl`, `RVUSourceControl` | Input selection, or read/list and select an actual device. |
| Channels | `directChannelControl`, `channelUpDnControl` | Tuner selectors plus channel 0–999, or one up/down command. |
| Remote | `remoteKeyControl` | One explicit documented IP key. No script expansion, held-key loop or automatic navigation. |
| Apps | `directAccessControl` | Documented app choices; an optional HTTP/HTTPS URL is allowed only for the browser, without credentials. |
| Art/power | `artModeControl`, `powerControl` | Model-specific art mode and explicit power/reboot. Turning off/rebooting may terminate communication. |

For USB/RVU/external speakers, **Read/list** has no selection parameters. The page preserves the returned device ID's string/integer type and only permits a matching ID/name pair from the current profile's recent list. No returned device means there is nothing to select; IDs are never invented. Device discovery here means asking the configured TV, not scanning the LAN.

`powerOn` is only an HTTPS command to a reachable endpoint. Preparing a test requires successful TV/video reads, so this page is not a way to wake an unreachable powered-off TV. No Wake-on-LAN, automatic pairing or fallback keys are hidden inside it.

## Stops, failures, and restarts

**Stop request** prevents subsequent requests but cannot undo an already delivered command. A timeout, lost response, rejection or failed readback keeps a pending review; a canceled request is never followed by automatic reads, writes or retries. Keep the baseline, inspect the actual TV, and handle any unwanted change manually. A rejected or mismatched command cannot be counted as successful.

Pending reviews block further writes, profile changes and token removal across the IP pages. Read-only diagnostics remain available. Re-pairing is still an explicit action if authorization was lost. After handling the TV, close the review as unsuccessful or confirm the observed effect only when acknowledgment/readback permit it. An uncertain response cannot establish verification.

The private `ip-remote/command-tests.json` stores the current preparation/review and the latest 100 reviewed trials. Startup sends nothing, never resumes a command, and expires old preparations. An interrupted send remains pending for review. No shared menu, calibration file or menu-verification sidecar is changed.

Export the diagnostic report on **IP Remote · Preview** to share results. It includes the catalog query, current command trial and history, plus normal token/IP/MAC/UUID/certificate-fingerprint redaction. The local private file/log retains identifiers. Free-text names, annotations and supplied URLs may contain personal information: review exports before sharing.

## Out-of-range picture values

On **IP Remote · Preview** or **Direct picture controls**, expand **Allowed picture ranges for this display**. Choose a control, enter the minimum and maximum shown on the actual TV, and save. For example, **if** your TV's Sharpness slider is 0–20, enter 0 and 20. Do not assume this example applies to all displays. Limits are saved in the private IP profile, not in a menu definition. Existing verified capability evidence is retained; refresh the picture reading after saving limits.

Both the UI and service reject changed targets outside these limits before sending. The defaults remain the historical 0–100 envelope until you supply actual limits. Presets cannot bypass configured limits when staged for a change.

The 2026-09-07 report showed Sharpness **0 → 74** receiving RPC error **−32002**. It did not establish a precise maximum or contain a process-crash stack trace. The corrected picture workflow handles correlated rejection codes −32002, −32003 and −32602 with a **read-only unchanged-value check**. If the original value, reported input/mode and other video fields are unchanged, it shows an inline rejection and allows a corrected target without recovery lock. No retry or restoring write is sent. The rejected target remains recorded, and prior verification is not erased.

If those reads fail, any checked field changed, or the request was canceled/ambiguous, recovery remains required. No reply alone is treated as proof that the TV did nothing. In a batch, rejection stops later rows; earlier successful changes stay on the TV.

An unresolved operation created by an older build is not silently discarded on upgrade. Use the existing **Check and restore original…** recovery: if the original is already present, it only reads and closes recovery without a restoring write. Then set the correct allowed range and continue.

## What remains unavailable

No confirmed direct command in this catalog exposes the full menu tree, white-balance gains/offsets, 20-point intervals, custom-color matrices, gamma, HDMI black level or every HDR/processing control. Factory/service resets and calibration-start operations are deliberately absent. These cannot be implemented safely by guessing method names or assuming ordinary key acknowledgments report setting values.

Further expansion needs actual protocol evidence for those operations and context-specific on-display verification. The existing menu-key controller remains separate; `main` is unchanged until the owner explicitly approves a merge.
