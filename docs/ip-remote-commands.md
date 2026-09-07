# IP Commands: documented methods and controlled testing

This working-branch feature expands IP Remote beyond the three established picture controls. It implements **69 command families**: the 52 families in the 2020 list plus 17 additional consumer-TV methods from the linked firmware notes. This is not every menu item or a guarantee that all methods work on every Samsung display. Existing Contrast/Color/Sharpness verification remains separate and cannot be bypassed from this page.

The catalog combines [Samsung's 2020 IP command list](https://www.hillresi.com/wp-content/uploads/2022/01/2020_IP_command_list.pdf), [py-samsungtv's client](https://github.com/iloveicedgreentea/py-samsungtv/blob/master/pysamsungtv/client.py) and [enums](https://github.com/iloveicedgreentea/py-samsungtv/blob/master/pysamsungtv/enums.py), and [TheFab21's consumer-TV firmware notes](https://github.com/TheFab21/ha-samsungtv-smart/blob/8c7000522b4045b42ff26d129d8d5fe9daf280cb/notes/QN55LS03FAFXZA/IPCONTROL_DECOMPILED.md). The latter identifies exact case-sensitive method names and parameter fields and includes observations from **QN55LS03FAFXZA / T-PTMFAKUC-0090-1296.8**, not the user's S95F. We also checked that project's [IP-control client](https://github.com/TheFab21/ha-samsungtv-smart/blob/8c7000522b4045b42ff26d129d8d5fe9daf280cb/custom_components/samsungtv_smart/api/ipcontrol.py).

The PDF supplies ranges and menu labels but not a complete wire schema. Firmware-note methods and candidate strings are labeled experimental, especially where the upstream test rejected a setter. A method disappearing in Art/Ambient mode does not prove permanent incompatibility. The source project's WebSocket, SmartThings, Art image management, hotel, commercial-wall and soundbar APIs are distinct; they are not silently routed through this TV client. No arbitrary method/parameter editor or upstream library dependency is introduced.

## Start here

1. Build and restart the server using the [preview setup guide](ip-remote-preview.md#1-start-the-working-branch).
2. In **IP Remote · Preview**, select the paired display and enter accurate model, firmware, input, picture mode, and signal annotations. Reuse the existing token; do not pair again just to use this update.
3. Open **IP Commands** from the navigation or [http://127.0.0.1:5050/ip-commands](http://127.0.0.1:5050/ip-commands). Opening the page or choosing a command sends nothing.
4. Select **TV state** or **Video state**, then press **Read/list** to confirm communication. Failed/unsupported methods show their actual outcome; no substitute command is tried.

The existing WebSocket connection indicator, quick-access keys, and predicted menu position are separate. IP Commands does not update that predicted position or invoke the old menu-key workflow automatically. Avoid other controllers or manual changes during a prepared test.

## Test a command

1. Select a command family and enter its parameters. Choices are documented candidates, not a live list of values available on your display. Empty, malformed, unknown, or out-of-envelope parameters are rejected before sending.
2. Check the actual TV and note the original value. Use a small, reversible change first. The 2020 list identifies **backlightControl** (0…50) as modern Brightness and **brightnessControl** (−5…5) as Shadow Detail. Tint is signed **−15…15**, correcting the old list's 0…100 envelope. Confirm each mapping on your display. Dedicated **Read/list** actions send only the token, not the values in the editing fields. **Fill fields from this reading (no TV change)** copies returned values into the editor; it does not Apply them.
3. Press **Prepare command (read only)**. The app reads TV/video state plus the command's dedicated getter and required mode/selector getters where applicable, then saves the exact parameters and baseline privately. It does not execute the selected command. Missing dedicated fields or an unsupported getter stop preparation; defaults are never substituted. Required mode/interval/color conditions are displayed before preparation and must be set explicitly.
4. Review the parameters and baseline, then check the explicit conditions/effects box. Changing a parameter requires preparation again. A prepared baseline expires after two minutes.
5. Press **Send prepared command once**. The app rechecks the reported baseline, dedicated setting and required mode/interval/color, saves that the command may be sent, and sends once. Changed state stops the write and requires new preparation.
6. Where there is a known readback field, the app reads independently and compares it with the requested value. Power, remote keys and app launching have no automatic post-action read; they may close/change the display or connection. No command is retried automatically.
7. Inspect the actual TV. **Observed expected effect — keep** records your confirmation and keeps the result on the TV. There is no automatic restoration. If it failed, or you do not want the changed state, check/restore it using the physical remote and press **Checked / handled manually — close without verification**. Closing review sends nothing.
8. To restore a setting through IP Commands, finish the current review, enter its original value, and prepare/send that as a new explicit command. The before-state details retain the original value. Do not attempt this if the original value, availability or mapping is uncertain.

An acknowledgment is not verification. The page distinguishes:

- **Readback + user verified:** acknowledged command, a different known original value, matching independent field readback, and positive visual confirmation. Already-at-target values are not sent as a test, and an unknown original cannot establish a verified write. Brightness/Tint tests also reject changes to other fields/context. Advanced tests re-read the dedicated method independently of its setter response and check the input/picture-mode and required selectors. Explicit Art/Game/Calibration-mode toggles may themselves change picture context; the before/after context is recorded instead of requiring equality. Numeric advanced tests reject changes to the TV/video baseline, and two-point tests reject unintended changes to omitted channels.
- **User-confirmed effect only:** the user observed the expected effect, but no independent target readback is available. It is not labeled read/write verified.
- **Mismatch/unsuccessful/unverified:** cannot count as a successful read/write test. Resolve any review before more writes.

Evidence includes the exact command parameters, original profile annotations, and before/after state. A pass is not proof of every value or other display/context. The coverage table shows the latest reviewed test for each method in the selected annotated profile; consult the saved report for its precise parameters and original TV-reported context. A later failed test remains visible instead of being hidden by an older pass.

## Available families

| Group | Methods | Use/limits |
| --- | --- | --- |
| Status | `getTVStates`, `getVideoStates`, `getDeviceInformation` | Explicit read-only snapshots; identity query does not overwrite the saved profile. |
| Established picture | `contrastControl`, `colorControl`, `sharpnessControl` | Links to the existing verification/range-checked picture workspace; not a second unrestricted writer. |
| Experimental picture fields | `backlightControl`, `brightnessControl`, `tintControl` | Brightness/Shadow Detail/signed Tint; confirm actual mapping. |
| Picture modes | `pictureModeControl`, `pictureSizeControl` | Documented choices; may change availability or recall other settings. |
| Sound | `directVolumeControl`, `volumeUpDnControl`, `muteControl`, `soundModeControl`, `speakerSelectControl` | Volume/enum controls; external audio support must be tested. |
| External speakers | `externalSpeakerControl` | Read/list first; select an actual returned ID/name pair. |
| Sources | `inputSourceControl`, `USBSourceControl`, `RVUSourceControl` | Input selection, or read/list and select an actual device. |
| Channels | `directChannelControl`, `channelUpDnControl` | Tuner selectors plus channel 0–999, or one up/down command. |
| Remote | `remoteKeyControl` | One explicit documented IP key. No script expansion, held-key loop or automatic navigation. |
| Apps | `directAccessControl` | Documented app choices; an optional HTTP/HTTPS URL is allowed only for the browser, without credentials. |
| Art/power | `artModeControl`, `powerControl` | Model-specific art mode and explicit power/reboot. Turning off/rebooting may terminate communication. |
| Motion / processing | `digitalCleanViewControl`, `autoMotionPlusControl`, `AMP.blurReductionControl`, `AMP.judderReductionControl`, `AMP.LEDClearMotionControl`, `localDimmingControl`, `filmModeControl`, `contrastEnhancerControl`, `colorBoosterControl`, `gameModeControl` | Blur/judder 0…10; Custom motion mode required for AMP children. Color booster/game values are model-specific candidates. |
| Two-point white balance | `colorToneControl`, `WB2PointControl` | Tone choices; six gain/offset channels −50…50. Partial flat updates supported. |
| Twenty-point white balance | `WB20PointModeControl`, `WB20P.IntervalControl`, `WB20P.RedControl`, `WB20P.GreenControl`, `WB20P.BlueControl` | On/Off; intervals `5%` through `100%` in 5% steps; RGB −50…50. Mode/interval changes are explicit separate commands. |
| Gamma / HDR | `gammaModeControl`, `gamma.BT1886Control`, `gamma.ST2084Control`, `gamma.HLGControl`, `HDRToneMappingControl`, `peakBrightnessControl`, `autoHDRRemasteringControl` | Gamma adjustments −3…3 with matching gamma mode. HDR choices depend on actual signal; candidate values are labeled. |
| Color space | `RGBOnlyModeControl`, `colorSpaceControl`, `colorSpace.ColorControl`, `colorSpace.ColorAdjustmentPointControl`, `colorSpace.RedControl`, `colorSpace.GreenControl`, `colorSpace.BlueControl`, `colorSpaceGamutControl` | Custom RGB 0…100 for the selected Red/Green/Blue/Yellow/Cyan/Magenta. Adjustment-point percentages are deprecated; gamut strings are experimental. |
| Eco / power behavior | `brightnessOptimizationControl`, `energySavingSolutionControl`, `motionLightingControl`, `autoPowerSavingControl`, `autoPowerOffControl` | Explicit setting tests; may affect picture or future power behavior. |
| Other picture / panel | `applyPictureSettingsControl`, `pictureCalibrationModeControl`, `pixelShiftMenuControl`, `displayRotatorControl` | Current/all sources; deprecated 2019 calibration-mode toggle; panel protection; landscape/portrait. These can have broad effects—read the warning first. Calibration mode is not Smart Calibration. |
| App / Multi View discovery | `firstScreenAppControl`, `multiviewControl` | Preserve object/array query results. Exact target string must appear in this profile's successful query within two minutes; empty results/invented values cannot be sent. No universal mode-string list is assumed. |

For USB/RVU/external speakers, **Read/list** has no selection parameters. The page preserves the returned device ID's string/integer type and only permits a matching ID/name pair from the current profile's recent list. No returned device means there is nothing to select; IDs are never invented. Device discovery here means asking the configured TV, not scanning the LAN.

`powerOn` is only an HTTPS command to a reachable endpoint. Preparing a test requires successful TV/video reads, so this page is not a way to wake an unreachable powered-off TV. No Wake-on-LAN, automatic pairing or fallback keys are hidden inside it.

### Advanced calibration walkthrough

Start with a dedicated **Read/list** for **Brightness / Backlight** or **Color tone**. Compare with the on-screen setting, fill the fields, choose one adjacent value, prepare/send/review, then explicitly prepare/send the original value to restore it. If the getter fails, stop and export the report; do not try arbitrary method names or values.

For **2-point white balance**:

1. Read/list and inspect the six channels. Getter shapes supported for comparison are flat fields, a `WB2Point` object, or a `WB2Point` JSON-object string. Other shapes remain unverified.
2. Enter only a small adjustment to one channel. Blank fields are omitted, not zeroed. The optional fill button populates all returned channels; you can clear fields you do not want to send.
3. Prepare, confirm, send once, and check the picture/readback. The app also checks that omitted channels did not change.
4. Close the review, then send the original channel value explicitly when ready to restore it.

For **20-point white balance**, explicitly test/set **20-point white balance enabled** to `On`, then **20-point interval** to the desired percentage. Review each command before continuing. When preparing an RGB adjustment, the saved baseline shows both the mode and exact interval. The app refuses to send if either changes. It does not iterate all intervals or change selectors implicitly. The same approach applies to **Custom color**: explicitly select `Custom`, select the color, then test one RGB channel. Review and restore your original values/selectors afterward.

For **Gamma**, the app requires the corresponding gamma mode (`BT.1886`, `ST.2084`, or `HLG`) before testing its −3…3 adjustment. Set the physical signal and gamma mode yourself. The mode string `2.20` comes from the 2020 list; its setter is not yet verified on the user's TV. HDMI bit depth alone is not treated as proof of a gamma/HDR mode.

Examples of **method-specific parameters only** (the client supplies the stored token):

```json
{"R-Gain": -1}
```

is a partial `WB2PointControl` write. `WB20P.RedControl` uses:

```json
{"WB20P.Red": -1}
```

Dots and hyphens are literal JSON property names, not nested objects. `colorSpace.ColorAdjustmentPointControl` uses percentages (`50%`, `75%`, `100%`) from the PDF, not the failed color-name guesses in the upstream notes. Do not use the deprecated control without a matching, working getter.

## Stops, failures, and restarts

**Stop request** prevents subsequent requests but cannot undo an already delivered command. A timeout, lost response, rejection or failed readback keeps a pending review; a canceled request is never followed by automatic reads, writes or retries. Keep the baseline, inspect the actual TV, and handle any unwanted change manually. A rejected or mismatched command cannot be counted as successful.

Pending reviews block further writes, profile changes and token removal across the IP pages. Read-only diagnostics remain available. Re-pairing is still an explicit action if authorization was lost. After handling the TV, close the review as unsuccessful or confirm the observed effect only when acknowledgment/readback permit it. An uncertain response cannot establish verification.

The private `ip-remote/command-tests.json` stores the current preparation/review and the latest 100 reviewed trials. Startup sends nothing, never resumes a command, and expires old preparations. An interrupted send remains pending for review. No shared menu, calibration file or menu-verification sidecar is changed.

Export the diagnostic report on **IP Remote · Preview** to share results. It includes the catalog/schema, query, current command trial and history, dedicated before/after settings and mode/selector prerequisites, plus token/IP/MAC/UUID/serial-number/certificate-fingerprint redaction. The local private file/log retains identifiers. Free-text names, annotations and supplied URLs may contain personal information: review exports before sharing.

## Out-of-range picture values

On **IP Remote · Preview** or **Direct picture controls**, expand **Allowed picture ranges for this display**. Choose a control, enter the minimum and maximum shown on the actual TV, and save. For example, **if** your TV's Sharpness slider is 0–20, enter 0 and 20. Do not assume this example applies to all displays. Limits are saved in the private IP profile, not in a menu definition. Existing verified capability evidence is retained; refresh the picture reading after saving limits.

Both the UI and service reject changed targets outside these limits before sending. The defaults remain the historical 0–100 envelope until you supply actual limits. Presets cannot bypass configured limits when staged for a change.

The 2026-09-07 report showed Sharpness **0 → 74** receiving RPC error **−32002**. It did not establish a precise maximum or contain a process-crash stack trace. The corrected picture workflow handles correlated rejection codes −32002, −32003 and −32602 with a **read-only unchanged-value check**. If the original value, reported input/mode and other video fields are unchanged, it shows an inline rejection and allows a corrected target without recovery lock. No retry or restoring write is sent. The rejected target remains recorded, and prior verification is not erased.

If those reads fail, any checked field changed, or the request was canceled/ambiguous, recovery remains required. No reply alone is treated as proof that the TV did nothing. In a batch, rejection stops later rows; earlier successful changes stay on the TV.

An unresolved operation created by an older build is not silently discarded on upgrade. Use the existing **Check and restore original…** recovery: if the original is already present, it only reads and closes recovery without a restoring write. Then set the correct allowed range and continue.

## What remains unavailable

The referenced consumer-TV schema does not provide every on-screen menu item, a full menu tree, HDMI black level or a universal reset/Smart Calibration command. Hotel provisioning/firmware updates, commercial-wall cabinet controls and soundbar-only dispatch schemas are outside this display workflow. Factory/service resets and forced reboot are deliberately absent. Unknown payloads are not guessed and no automatic remote-key fallback is used.

All newly exposed advanced commands still need context-specific on-display verification. The three established controls remain in the guarded picture workspace; adding experimental methods to IP Commands does not unlock them in presets or bulk calibration. The existing menu-key controller remains separate; `main` is unchanged until the owner explicitly approves a merge.
