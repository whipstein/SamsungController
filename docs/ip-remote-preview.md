# IP Remote preview: setup and staged verification

Version: **1.0.0-alpha.1** · Branch: **feature/ip-remote-v1**

This document preserves the **early staged diagnostic workflow**. The working branch now replaces the primary web interface with [direct Menu controls](direct-ip-interface.md), without manual verification gates. The old WebSocket/menu-key pages and macro editor are hidden. The picture test page remains at `/diagnostics/picture-tests`, with its optional three-control workspace at `/diagnostics/picture-workspace`, for experiments and recovery. `main` is unchanged and must not be merged until the owner explicitly approves. Follow the current [README](../README.md) for normal installation and use; the numbered steps below describe the earlier diagnostic screens, not today's primary navigation.

The supplied IP Remote handoff is design background. This repository is C#/.NET, so the Python examples informed the protocol shape, not the implementation language. Pairing, getter names, and transport conventions come from the sources below; support on your actual displays remains unverified until we collect their responses.

## What this checkpoint does

- Sends **one explicitly requested pairing call**: `createAccessToken`.
- Sends **two explicitly requested read methods**: `getTVStates` and `getVideoStates`.
- Offers **separate guarded Contrast, Color, and Sharpness experiments**: fresh reads → a one-step change → readback → visual pass/fail → original value → restoration readback. Each lowers by one, except a zero starting value tests 0 → 1 → 0. No arbitrary method editor is provided.
- Unlocks a **direct picture editor** for each control only after its own experiment passes for the matching display/context. Target edits are local until explicit Apply; successful readback keeps the new value, with an optional checked Undo.
- Provides a separate **Direct picture controls** workspace with one refresh for all three controls, staged multi-setting Apply, per-setting progress, Stop, saved batch originals, and command-free JSON preset loading.
- Saves a separate credential per HTTPS host/port. Existing WebSocket tokens are never read or replaced by this client.
- Shows method outcomes, timestamps, raw JSON fields/types, and differences from the previous successful response in the same annotated context.
- Preserves unknown fields. Missing fields say **Not reported**, not zero, false, or a previous value. A successful empty result does not prove any control is available.
- Provides cancellation, finite timeouts, redacted request/response history, and a downloadable report.

The guarded picture workspace still contains only Contrast, Color, and Sharpness; each needs independent verification. A separate **IP Commands** page now offers controlled testing of the [69 cataloged command families](ip-remote-commands.md), including advanced calibration fields, modes, sound, sources, channels, IP keys, apps and power. The preview does not infer unknown mappings, discover TVs or scan ports, poll in the background, retrieve menu trees/screenshots, issue reset/Smart Calibration-start commands, or automatically fall back to remote keys. It does not alter menu definitions, verification sidecars, saved calibration values, or predicted menu position.

## 1. Start the working branch

Stop the existing development server with Ctrl+C first. A refresh alone cannot replace its running assemblies. From the repository directory:

```sh
git switch feature/ip-remote-v1
dotnet restore SamsungController.sln
dotnet build SamsungController.sln --no-restore --disable-build-servers -m:1
dotnet run --project src/SamsungController.Web --no-build --no-restore
```

These commands work in macOS/Linux shells and Windows PowerShell. Preserve any uncommitted work before switching branches. Open [http://127.0.0.1:5050/ip-remote](http://127.0.0.1:5050/ip-remote), or select **IP Remote · Preview** in the navigation.

The existing top-of-page **Connect** status and quick-access buttons still control the WebSocket remote. You do not need to connect that remote, verify a menu, or run an anchor for this test. Leave its buttons alone while collecting a baseline. Pairing on this page is a separate process, not a persistent “Connected” session.

## 2. Save the endpoint and test conditions

1. Turn on the TV, keep it on a known input, and enable its **IP Remote** setting if available. On some models this is under **Settings → General → Network → Expert Settings**; menus differ. The [RTI integration guide](https://driverstore.rticontrol.com/driver/samsung-ip-television) describes the setting, explicit TV authorization, and the 1516/1515 port distinction. That guide is not evidence of compatibility with every Samsung display.
2. Enter the TV address, without `https://` or a path. Start with HTTPS port **1516**. An older endpoint may use **1515**; changing ports requires saving and explicit pairing for that endpoint, not a scan.
3. Enter the model, firmware, actual input source, picture mode, and signal conditions. Include useful detail such as SDR/HDR, RGB/YCbCr, and bit depth, but do not assume bit depth alone proves HDR. These fields are **user annotations**, not detected values or commands to the TV.
4. Select **Save IP Remote profile**. Saving and selecting a profile sends no TV request. Profiles are separate from existing display/menu combinations during this diagnostic phase.
5. Before changing display/input/mode/signal conditions later, update and save these annotations. Results from different contexts are not used for comparisons with one another. Changing only the observation label keeps the comparison context unchanged.

Unsaved profile edits disable Pair and Read so the app cannot accidentally use the old endpoint or attach stale annotations. **Discard unsaved edits** restores the saved profile. **New display** clears the form; saving another address adds it to the profile list. The same endpoint retains its latest saved annotations.

## 3. Pair while watching the TV

1. Select **Pair with TV** once.
2. Watch for the approval dialog and accept it on the TV. Pairing allows 60 seconds by default; reads allow 10 seconds. Both are adjustable under **Certificate trust and timeouts**, up to 120 seconds.
3. Success shows **Separate IP Remote token saved**. It does not claim that state reads work or that the display is being monitored.

If the certificate is rejected, inspect the observed SHA-256 fingerprint. System trust is the default. After verifying the fingerprint through a trusted means, enter it in **Trusted certificate SHA-256 fingerprint**, save, and retry. A displayed fingerprint identifies the responding server but does not itself prove it is your TV. Alternatively, on a trusted LAN, you can explicitly select **Allow an untrusted certificate for this endpoint only**, save, and retry. This weakens server authentication for that endpoint; it is never a global TLS bypass. An entered pin takes precedence and must match even if the checkbox is selected.

Use **Cancel request** to stop waiting. A canceled or timed-out pairing request might already have reached the TV, so dismiss any remaining prompt before retrying. There is no automatic retry or re-pair. Repeated denied attempts are not a useful discovery strategy.

An authorization failure leaves the existing token intact and asks for explicit re-pairing. A malformed or unsuccessful pairing response never replaces it. **Forget local IP Remote token** only removes the selected endpoint's locally saved credential; it does not revoke permissions on the TV or affect the WebSocket token.

The page shows the last pairing outcome beside the pairing buttons and explains why reads are disabled. TV approval alone is not enough: the app must receive and save the token. The client accepts a response ID as either the original number or exactly the same decimal text (for example, `2` and `"2"`); other IDs still fail correlation. Earlier preview builds rejected text IDs, so an affected pairing must be repeated after updating—no token was saved from those rejected replies.

## 4. First live checkpoint: reads only

For the first test, **do not change any settings**:

1. Set the observation label to **Baseline — TV on**.
2. Select **Read both state queries**.
3. Inspect both cards. The app sends `getTVStates`, then `getVideoStates`, sequentially. If the first method is explicitly unsupported, it still tries the second. Authorization, timeout, transport, certificate, and other failures stop the sequence. Individual method buttons let you retry one query explicitly.
4. Download the diagnostic report, keeping **Redact IP, MAC, and UUID identifiers** and **Redact certificate SHA-256 fingerprints** checked unless those details are needed privately. The options are independent and default to on; turning one off never reveals access tokens.
5. Send the report back for review before attempting the first write on a new display. Include what the TV did, which port/trust option worked, and whether an approval prompt appeared.

Repeat for the other display using **New display** or the saved profile selector. Do not presume an S95F and an Odyssey support the same methods.

After a successful first read, restart the app and try reading the saved profile **without pairing again**. This checks that token persistence works on that display. A successful response is evidence for that method in that context, not proof of every field or a lasting availability guarantee.

**Pair again… requests a new token**, rather than reconnecting with the saved one. It may require another TV approval. For normal reads after restarting, go directly to **Read both state queries**.

## 5. Next checkpoint: verify what the fields mean

After reviewing initial replies, we will choose a small, reversible manual test:

1. Record the original TV setting value and capture a baseline response.
2. Change **one** non-destructive setting using the physical remote. Keep input, picture mode, and signal fixed.
3. Label the observation accurately (for example, **TV Brightness manually 20 → 21**) and read again.
4. Compare which field changed, including its raw type. No changed field is also a useful result; it does not justify guessing a setter.
5. Restore the original value manually, read again, and label the restoration.

Do not test resets, service-menu functions, or calibration-start commands. In particular, **TV Brightness/backlight and Shadow Detail must be distinguished empirically**. Historical API names are not a reliable mapping to modern menu labels. The preview does not map any returned field to your calibration controls or assume a value range. White balance, 20-point interval grids, custom color, and related read/write support remain unproven.

The comparison table unions field names from the latest response and previous successful response in the same saved context. A field that disappears is marked **Not reported**. When a new request fails, its card reports the failure and marks any prior success as historical/stale; old values are not presented as a current reading.

## 6. Verify one guarded contrast write

Evidence collected on 2026-09-07: the user confirmed saved-token reuse after restarting, and the S95F firmware **1301** capture showed `contrast` **45 → 44 → 45** during the manual change/restore test. The TV reported **HDMI4** and **FilmmakerMode**, with user annotations describing RGB 8-bit. This establishes contrast readback in that context, not setter support, HDR status, other settings, or Odyssey compatibility.

The experiment uses `contrastControl` with `params.contrast` and the separate access token. The [Samsung command list](https://s7d2.scene7.com/is/content/SamsungUS/samsungbusiness/products/tvs/tvci-8-21-17/resource-center/control-codes/TV_IP_CommandList_v.1.1_1Pager.pdf) identifies this method; the [reference client implementation](https://github.com/iloveicedgreentea/py-samsungtv/blob/master/pysamsungtv/client.py) supplies the parameter shape. Their historical 0–100 envelope is **not** a claim about this display's actual slider range.

The subsequent user-supplied report on 2026-09-07 confirms successful direct writes **45 → 44 → 45**, independent readbacks, positive visual confirmation, and restoration on the S95F firmware 1301 in that same context. Other returned video fields stayed unchanged. No new pairing occurred during that test. This is evidence for contrast in that context only; other displays, settings, and full value ranges remain unverified.

1. Rebuild and restart the working branch using section 1. Open **IP Remote · Preview**. Reuse the saved token; do not pair again unless authorization actually fails.
2. Save accurate model, firmware, input, picture-mode, and signal annotations. Keep the physical remote available for inspection or recovery. Do not run other adjustments simultaneously.
3. In **5. Guided contrast test and recovery**, choose **Prepare contrast test (read only)**. This reads `getTVStates` and `getVideoStates` and shows the original contrast, proposed one-step target, and reported input/mode. It changes nothing. Missing input/mode, a non-integer contrast, or a value outside the protocol envelope blocks the experiment. Zero proposes 1 instead of a negative value.
4. Check the actual Contrast value on the TV. Confirm that both the original and target are valid on this display and that input, mode, and signal conditions will stay unchanged. Check the confirmation box. Preparation expires after two minutes; prepare again if needed.
5. Select **Apply one-step contrast test**. The inline conditions checkbox is the confirmation; there is no extra browser popup. The app re-reads input/mode and video values before sending **one** contrast write. If the original or other reported video fields changed, it stops before writing. After an acknowledged write it reads again; the returned contrast must equal the target. The setter's own reply is not accepted as readback evidence.
6. The app **pauses at the target** so you can inspect the TV's Contrast number. It does not navigate menus for you. Select **Matches — restore original** or **Does not match — restore original**. Either selection rechecks the context/value, restores the original once if needed, and reads back. A failed visual check restores but does not mark the experiment verified.
7. Wait for **restoration readback: matched**. A passed experiment requires changed-value readback, your positive visual confirmation, an acknowledged restoration command, and matching restoration readback. Download the diagnostic report and share it before expanding to other direct controls. The record is specific to this display and saved context and does not enable existing calibration controls.

### Stop, failure, and restart recovery

**Stop requests** and **Cancel request** stop the pending operation and prevent follow-up requests. They do **not** undo a write already delivered to the TV. There is no automatic retry, automatic re-pairing, or hidden restoration after cancellation. A timeout, malformed reply, mismatch, or connection failure leaves a conspicuous recovery warning with the original value.

The app saves `ip-remote/contrast-test.json` **before** sending the first write. If it cannot save that recovery record, it does not write to the TV. This private journal survives restarts; startup never resumes requests automatically. An unresolved test prevents replacing its profile/context or forgetting its token. Explicit re-pairing for the same endpoint remains available after an authorization failure.

To recover, first return the TV to the original input/mode/signal conditions. Choose **Check and restore original…** and confirm those conditions. Recovery reads first:

- If contrast already equals the original, it confirms the reading without a redundant write.
- If it equals the test target and no restoration was previously attempted, it writes the original once and reads back.
- If the input/mode, other returned video fields, or contrast have changed unexpectedly, it refuses to overwrite them. Restore manually in the original context instead.
- If a restoration was already attempted, it is never sent a second time. Restore manually if needed, then run the read-first recovery again to confirm the original.

If communication is unavailable, restore the original value with the physical remote and choose **I restored manually — close test…**. Confirm the dialog only after doing so. This sends no request and closes the warning **without** claiming readback or successful direct-write verification.

The baseline and recovery reads are sequential, not atomic. Signal format/bit depth and some other context cannot be detected through these two getters. Keep those conditions fixed yourself, and avoid changing settings with another app or remote during the experiment. A canceled/late request can have an uncertain outcome; inspect the TV before recovery. Merely navigating away does not cancel a running operation or restore a paused test.

## 7. Use a verified direct control

On startup, a previously completed **local** contrast test is carried into the separate capability file automatically. No TV requests occur, no diagnostic report needs importing, and a successful existing test does not have to be repeated just for this upgrade. Failed, canceled, manually closed, or incompletely restored tests cannot unlock direct writes.

1. Restart the updated server and open **IP Remote · Preview** with the same saved display and annotations. Look for **Contrast read/write verification saved** under **4. Direct picture controls**, with **Contrast** selected.
2. Select **Read direct contrast**. This retrieves the current contrast plus reported input/mode. Apply stays locked unless those match saved evidence for the endpoint, model, firmware, annotated source/picture mode/signal, and reported input/mode. A success on one display or signal context does not unlock another.
3. Enter a target using the number box or +/−. Editing sends nothing. For this first UI checkpoint, if the TV is still at **45**, choose the already tested **44**. The 0–100 numeric limit is the historical protocol envelope, **not** a verified TV slider range; use only values valid on the TV.
4. Check the unchanged-conditions box and select **Apply direct contrast**. You can check the box before or after editing: typing a target or pressing +/− no longer clears it. A new reading, saved profile, or selected control clears the confirmation so it cannot carry into a different baseline/context. Apply sends directly without a browser permission popup. The app rechecks the baseline immediately before writing. Changed contrast, other returned video fields, input/mode, stale readings (over two minutes), unsaved profile edits, or missing context verification stop the write. A mismatch requires another read and review; it never silently rebases your edit.
5. A successful Apply writes once, reads back independently, and **keeps the requested value on the TV**. It does not automatically restore it. Inspect the actual value on the TV as the next hardware checkpoint. An HTTP/setter acknowledgment without matching readback is not success.
6. To reverse it, keep the original conditions and select **Undo last direct change**. There is no extra popup. It rechecks before restoring the original, then reads back. If a different value or context is now present, it refuses to overwrite it. If the original is already restored, no redundant write is sent. Only the latest operation has an Undo; starting another test or adjustment replaces that operation record.
7. Download the diagnostic report after Apply and Undo and share it for review. Select Color or Sharpness to test the next control as described below. Unverified controls cannot be used for ordinary direct adjustments.

Verification evidence and operations are separate: `ControlCapabilities` in the report describes historical read/write verification, while `PictureTest.Purpose` distinguishes `Verification` from `DirectAdjustment`. A direct adjustment does not manufacture a new verification pass. Your saved contrast capability remains available after later adjustments and restarts; the current reading is deliberately not restored from disk. Read again for a current value.

Cancellation/timeout/failure after a possible write uses the same private original-value journal and explicit recovery described above. No request is replayed on restart. A successfully kept adjustment is not an unresolved recovery; you may leave it in place or explicitly Undo it later. A failed preflight for Undo leaves the earlier successful adjustment intact and sends no restoring write. Keep input, picture mode, and external signal unchanged during operations; these getter calls are not atomic and cannot identify every signal condition.

## 8. Verify Color and Sharpness

These are **candidates awaiting your hardware verification**, not capabilities inferred from Contrast. The [reference client](https://github.com/iloveicedgreentea/py-samsungtv/blob/master/pysamsungtv/client.py) defines `colorControl` with `params.color` and `sharpnessControl` with `params.sharpness`. A matching protocol name does not establish this display's actual behavior or range.

1. Keep the same saved TV profile, input, picture mode, and signal. In **4. Direct picture controls**, choose **Color** from **Picture control**. This selection sends nothing.
2. Next to the locked **Apply direct color** button, select **Prepare color verification (read only)**. This reads the baseline and scrolls to **5. Guided Color test and recovery**, without changing a setting. You can also use **Prepare color test (read only)** directly in that section.
3. Check the original Color number on the TV and the proposed one-step target. If it reads 25, the test proposes 24. Check the inline conditions box only if both values are valid and the original matches.
4. Select **Apply one-step color test**. It rechecks, sends one Color change, and reads back. The app stops for you to inspect the actual Color number.
5. Select **Matches — restore original**, or **Does not match — restore original** if it is incorrect. Wait for restoration readback before continuing. A successful test unlocks Color only for the matching context.
6. Choose **Sharpness** and repeat. A starting value of 0 proposes **0 → 1 → 0**; a nonzero value proposes one lower and back. The test always shows the exact original and target.
7. Download the diagnostic report after both tests. Existing Contrast verification stays saved throughout. Each verified control can then be selected, read, adjusted, and optionally undone using section 7.

A pending write or interrupted test blocks selecting/writing another control until recovery is resolved. Readback checks exclude only the control being tested: changes to Contrast while testing Color, for example, stop the operation. Both the recovery journal and capability evidence record the exact control. Old local journals without a `Control` field retain their original Contrast meaning.

**Why is Apply direct disabled?** Reading Color is not verification, and checking a conditions box only confirms the setup. Each control needs its own successful one-step change, independent readback, visual confirmation, and restoration. Until that is complete for the actual display/input/mode/signal context, the page offers **Prepare verification** instead of the direct target editor and conditions checkbox. During verification, use **Apply one-step color test**, not **Apply direct color**. After the test passes, read the control again to reveal its direct editor.

The next section combines only the three established picture controls. **IP Commands** now exposes controlled tests for the documented Brightness/Tint protocol fields, but does not map them to modern menu labels or enable advanced calibration. White balance/custom color remain unimplemented without protocol evidence. See the [command-testing guide](ip-remote-commands.md) for the wider catalog and its safety boundaries.

## 9. Use the combined direct picture workspace

The Color and Sharpness tests were reported working by the user on 2026-09-07. This is a user-confirmed checkpoint, not a newly reviewed diagnostic capture or proof for other displays or contexts. Existing saved successful tests unlock their matching controls automatically; updating the app does not require repeating them.

Restart the development server after building this update, then choose **Direct picture controls** in the navigation, or open [http://127.0.0.1:5050/ip-controls](http://127.0.0.1:5050/ip-controls). The button at the top returns to **Setup, verification & diagnostics**. Opening either page sends nothing to the TV. The global header's Connect/Disconnect and quick-access buttons still belong to the separate WebSocket remote, not this HTTPS workspace.

### Read and apply several changes

1. Select your paired display and correct model/firmware/source/mode/signal annotations in **IP Remote · Preview**. Complete the one-step verification for each control you want to change, if not already verified in that context.
2. Open **Direct picture controls** and press **Refresh TV values**. This makes one `getTVStates` request and one `getVideoStates` request, showing actual values, reported input/mode, and a timestamp. Unreported or incorrectly typed values remain unavailable. Nothing is inferred from defaults.
3. Edit Contrast, Color, and/or Sharpness with the number inputs or −/+ buttons. These are local pending targets; the TV does not change yet. The 0–100 limit is a protocol envelope, **not a measured range for your display**. Use values you know are valid, and start with small adjustments.
4. Check that the TV/input, picture mode, external signal, and allowed target ranges match; tick the conditions checkbox. Editing a target preserves that checkbox. A new refresh or loading/staging previous values clears it so you can review the new plan.
5. Press **Apply N pending**. There is no additional browser permission popup. Only changed values are sent, in Contrast → Color → Sharpness order. All changed controls must have matching verification before the first request is allowed.
6. Follow **Latest batch**. Each row receives fresh input/mode/video preflight reads, one setting write, then independent input/mode/video readback. Every other reported video field must remain unchanged. Successful values stay on the TV. After completion, the last readback populates the workspace and clears the pending count.

A reading must be no more than two minutes old when Apply starts. Press **Refresh TV values** if it has expired. Pending targets survive a refresh in the same context; untouched inputs update to the newly reported values. Switching the display or reported input/picture mode clears the draft. Using the diagnostic page's reads/tests or changing a profile invalidates the workspace reading and requires another refresh. There is no periodic background polling.

### Stop, partial changes, and restoration

**A batch is not atomic.** Press **Stop** to cancel the current operation and prevent later requests. A command already delivered might still take effect. Nothing is retried or rolled back automatically. The first timeout, rejection, readback mismatch, context change, or storage failure stops the rest of the batch. Earlier confirmed changes stay applied.

All original values are saved to the private batch journal before sending a write. The progress table distinguishes confirmed changes, uncertain changes, and rows not sent. If a write is uncertain, use **Open read-first recovery** to check and restore only that control under the original conditions. A pending recovery blocks further editing/writes until resolved. Check the TV rather than assuming a network error means no change occurred.

**Stage previous values** loads the latest batch's originals into the target inputs; it sends nothing. After a successful batch you can use it immediately. After a stop or restart, resolve any recovery and refresh the original display/context first. Review the targets, confirm conditions, then Apply to restore them. This is a new checked batch and can itself be stopped. Ordinary **Undo last direct change** on the diagnostic page affects only the most recent individual setting, not the entire batch.

The latest batch's originals and progress survive restarts. They are historical, not current readings. Interrupted batches never resume automatically. Only the latest batch is retained; save a preset before another batch if you want a longer-lived copy of those settings. Unsaved target edits exist only in the current page and are lost when you leave/reload it.

### Save or load a JSON preset

Expand **JSON presets · save or load desired values** after a refresh:

- **Download TV values** saves the values from the displayed TV reading.
- **Download staged values** saves the target inputs, including unmodified reported values.
- **Load preset as pending** imports desired values into the inputs. It does **not** write the TV, replace actual readings, import credentials, or grant verification. Review and Apply explicitly.

These files may be stored anywhere your browser can download/upload them; no special application folder is required. Maximum size is 64 KiB. Model, firmware, annotated source/mode/signal, and TV-reported input/mode must match the current reading. Endpoint addresses and certificate/token data are omitted, so changing a display's network address does not by itself prevent loading. Each changed control must still have local verification for the selected endpoint/context.

This is a separate format from the existing menu-based calibration files. Example values below are illustrative, not recommended settings:

```json
{
  "format": "SamsungController.IPRemote.PicturePreset.v1",
  "name": "Example picture preset",
  "context": {
    "model": "Example model",
    "firmware": "Example firmware",
    "inputSource": "HDMI source",
    "pictureMode": "Filmmaker",
    "signal": "SDR RGB 8-bit",
    "reportedInput": "HDMI4",
    "reportedPictureMode": "FilmmakerMode"
  },
  "values": { "contrast": 44, "color": 24, "sharpness": 1 }
}
```

Use the app's download to obtain your exact context strings. A preset may include one, two, or all three supported fields. Unknown or duplicate properties, missing format/context, non-integer values, and mismatched contexts are rejected without partially staging values. Downloads omit connection identifiers but preserve free-text names/annotations; review those before sharing.

### Next real-TV checkpoint

1. Refresh and download **TV values** as a reference preset.
2. Make a small valid change to two or three verified controls. Apply once, and compare each displayed readback with the actual TV values.
3. Use **Stage previous values**, review/confirm, and Apply. Check that all changed controls return to their originals.
4. Edit a target and download **staged values**, then discard the draft. Load that preset and confirm that only the pending target changes—not the actual TV value. Discard it if you do not want to apply it.
5. During a small multi-control batch, test **Stop**. It may finish too quickly to interrupt; do not increase adjustment sizes merely to make it last longer. If interrupted, check the recorded outcomes, resolve any pending recovery, then refresh before proceeding. Never assume Stop restored earlier values.
6. Download the redacted diagnostic report from **IP Remote · Preview**. It now includes the workspace reading and latest batch history. Report any mismatch before expanding to additional controls.

## Files, privacy, and troubleshooting

For configurable picture limits, the corrected Sharpness rejection behavior, and the new command-testing workflow, see [IP Commands and out-of-range values](ip-remote-commands.md).

Files live in an `ip-remote` subdirectory of the same personal configuration root used by the web app, honoring `SamsungController:ConfigurationDirectory` if configured:

| File | Contents |
| --- | --- |
| `tokens.json` | IP Remote credentials, keyed by HTTPS endpoint; private, never share |
| `profiles.json` | Saved endpoint/trust options and user-entered context; no tokens |
| `command-tests.json` | Current prepared command/review and latest 100 reviewed trials, with exact parameters and before/after state; no token. Startup never resumes an action. |
| `diagnostics.ndjson` | Append-only timestamped, token-redacted observations; device identifiers retained |
| `contrast-test.json` | Historical filename retained for recovery across preview upgrades; the `Control` field identifies the latest Contrast, Color, or Sharpness verification or direct adjustment: original/target, context, readback/visual outcome, kept/undo state, and restart recovery; no token |
| `control-capabilities.json` | Independent control- and context-specific read/write evidence, tested value pair, and source test ID; no token |
| `picture-batch.json` | Latest batch context, every original/target, operation IDs and per-setting progress; no token. The single-operation journal remains authoritative for unresolved-write recovery. |

On Unix systems, the IP Remote directory is restricted to the user (0700) and final files to user read/write (0600). Windows uses the personal configuration folder's user ACL. Credentials are local plaintext files, **not OS-vault encrypted**. Do not commit or share that directory. Its normal repository path is ignored.

The page retains the last 100 observations in memory; a restart starts an empty page history but leaves the private log and saved token in place. Reports contain that in-memory history across tested contexts/displays, not the entire log. Token values are always scrubbed, including pairing replies and token echoes; malformed/non-JSON bodies are omitted to avoid leaking credentials. IP/MAC/UUID redaction is on by default for downloaded reports. Review annotations and model names before sharing; these are intentionally readable. This separate log policy does not change the older WebSocket Protocol page's logging behavior.

Certificate SHA-256 fingerprint redaction is also on by default, independently of IP/MAC/UUID redaction. It masks `CertificateSha256`, `NormalizedCertificatePin`, and `ObservedCertificateSha256` throughout the export, including saved-context copies, method summaries, embedded request/response JSON, and matching fingerprint echoes. This is export-only: it never changes the certificate pin used to connect or rewrites the private log. A fingerprint is a certificate identifier, not a private key; it is still useful to redact when sharing display diagnostics. Plain numeric firmware values, JSON-RPC versions, and text request IDs remain visible rather than being interpreted as abbreviated IPv4 addresses.

Reports also include saved control capabilities, the latest direct reading (if any), and the latest operation's original/target, context, outcomes, and recovery state. Saved evidence/operation summaries survive independently of in-memory request history; direct readings do not survive a restart. The same token, identifier, and fingerprint redactions apply to their nested profiles and baseline fields. These private records are not portable model-wide capability declarations or part of shared menu definitions. No personal evidence or downloaded report is committed to the repository.

If a read fails:

- **CertificateError:** review and save endpoint-specific trust settings; do not disable system-wide TLS validation.
- **Timeout / TransportError:** confirm TV power, IP Remote enabled, address/port, same-LAN reachability, macOS Local Network permission where applicable, and VPN routing/local-LAN exclusions. An old working WebSocket does not prove HTTPS IP Remote reachability. Do not change TV picture settings to fix a network failure.
- **Unauthorized:** watch for denied/missed approval, then pair explicitly when ready. Do not spam retries.
- **Unsupported:** retain the report with the exact model, firmware, and context. This is not a signal to send undocumented writes.
- **ProtocolError:** download the safe report. Unexpected JSON structure, response IDs, or field types need investigation; they are not successful readings.
- **StorageError / storage warning:** check access to the private data folder. A response may be visible even if its log cannot be written; download it before closing.

## Implementation and verification checklist

### Phase A — independent transport and diagnostics

- [x] Major-version preview on a working branch; existing workflows retained.
- [x] HTTPS JSON-RPC root endpoint, configurable port, explicit pairing, separately persisted token.
- [x] Exact two-getter read allowlist; the later picture experiments are separate explicit actions. No arbitrary methods, automatic retry, or fallback keys.
- [x] Scoped TLS policy, cancellation, timeouts, bounded response size, response correlation, classified errors.
- [x] Unknown/raw typed fields, missing-value handling, historical comparisons, redacted logs and reports.
- [x] Automated fake-response tests, including interactive page event handling and existing regression tests.
- [x] User-supplied S95F report confirms pairing plus successful `getTVStates` and `getVideoStates` replies in one annotated context (2026-09-07).
- [x] Confirm S95F token reuse after restart; corrected report preserves firmware 1301 (2026-09-07).
- [ ] Live pairing/readback/token reuse confirmed on Odyssey G9 with recorded firmware and context.

### Phase B — establish real mappings

- [ ] Baseline → one manual change → readback → manual restore, on each relevant display.
- [x] S95F contrast readback: 45 → 44 → 45 in the reported HDMI4 / FilmmakerMode / annotated RGB 8-bit context (2026-09-07).
- [ ] Confirm brightness/backlight/shadow-detail identities and supported values without assuming old ranges.
- [ ] Verify input, picture-mode, and signal effects; record missing/unsupported states distinctly.
- [x] Review the contrast evidence together and approve a single direct-write experiment (2026-09-07).

### Phase C — one reversible direct write, only after approval

- [x] Implement original-value reads, context/envelope checks, user confirmation of the TV's actual values, and a one-step-only change.
- [x] Implement write once → independent readback → visual pass/fail → original-value restoration → readback.
- [x] Implement cancellation/failure handling and a private restart-recovery record; never blindly retry an ambiguous write.
- [x] Exercise protocol, round-trip, false success, context drift, storage failures, cancellation, restart recovery, and page actions with simulated HTTP replies.
- [x] Verify the direct contrast write, visual result, and restoration on S95F firmware 1301; review the exported report (2026-09-07).

### Phase D — expand only demonstrated capabilities

- [x] Add verified direct contrast with independent read/write evidence, context keys, capability-gated UI, explicit Apply/readback, and optional checked Undo.
- [x] User confirms direct Contrast Apply works on the display (2026-09-07).
- [ ] Confirm optional direct Undo on the display separately.
- [x] Add allowlisted Color and Sharpness test/restore workflows, independent per-control evidence, and gated direct editors; preserve existing Contrast evidence and unresolved recovery.
- [x] Preserve the conditions checkbox on target edits and remove redundant Apply/Undo permission popups; exercise component events and simulated protocol/recovery paths.
- [x] User confirms both Color and Sharpness tests work on the current display (2026-09-07); no additional diagnostic capture reviewed at this checkpoint.
- [ ] Repeat capability verification for other display/context combinations; confirm optional direct Undo separately.
- [x] Combine verified controls into a read-on-demand workspace with multi-setting Apply, whole-batch serialization, per-write preflight/readback, Stop, durable originals/progress, and explicit staging of previous values.
- [x] Add bounded, context-checked JSON presets that download current/desired values and import desired targets only, without endpoint credentials or verification.
- [x] Exercise batch partial failure, cancellation, storage failure, restart recovery, preset parsing, and component interactions with simulated TV responses.
- [ ] User verifies the combined Apply/previous-values/preset/Stop workflow on the display using the section 9 checkpoint.
- [ ] Extend additional controls only after their own field mapping and reversible-write verification.
- [x] Add a closed, typed catalog for all 69 cataloged method families and an explicit prepare/send-once/review screen; retain separate established-picture gates.
- [x] Persist command review/evidence, distinguish acknowledgment/user observation/readback verification, and support typed device lists without invented IDs.
- [x] Handle explicit picture rejection with read-only unchanged-state checks; add persisted user-defined picture ranges and regression tests for the reported Sharpness 74 failure.
- [ ] Verify the newly exposed command families and experimental Brightness/Tint mapping on the real display; no new hardware compatibility is implied by protocol tests.
- [ ] Add bounded state polling only if useful and proven safe; do not assume subscriptions exist.
- [x] Map the 2020 command list and 2025 consumer-TV protocol notes into 69 typed command families, including white balance, gamma, custom color, motion/HDR/eco controls, display identity, rotation, app lists and Multi View.
- [x] Add dedicated getters, literal dotted parameters, partial two-point writes, interval/color/mode prerequisite checks, and private before/after evidence with no implicit selector changes.
- [ ] Verify advanced controls on the actual displays, including valid value strings, getter shapes, conditional support and deliberate restoration.
- [ ] Revisit integration with existing menu/calibration workflows using evidence, not implicit fallback.
- [ ] Owner explicitly approves merging the working branch into `main`.

Checked code milestones are **not hardware compatibility claims**. Automated tests use fake handlers and fixtures; no actual display was contacted during implementation.

## Protocol references

- [Samsung 2020 IP command list](https://www.hillresi.com/wp-content/uploads/2022/01/2020_IP_command_list.pdf): 52 command families, calibration ranges, model-era naming changes and deprecations. It does not spell out all JSON-RPC payloads.
- [TheFab21 consumer-TV firmware protocol notes](https://github.com/TheFab21/ha-samsungtv-smart/blob/8c7000522b4045b42ff26d129d8d5fe9daf280cb/notes/QN55LS03FAFXZA/IPCONTROL_DECOMPILED.md): exact advanced method/parameter spelling and scoped live observations on QN55LS03FAFXZA firmware 1296.8. Candidate values and failed setters are not promoted to hardware support here. See the [expanded command guide](ip-remote-commands.md).
- [Samsung TV IP command list v1.1 (manufacturer PDF)](https://s7d2.scene7.com/is/content/SamsungUS/samsungbusiness/products/tvs/tvci-8-21-17/resource-center/control-codes/TV_IP_CommandList_v.1.1_1Pager.pdf): historical getter names and command families, not proof of modern model ranges.
- [RTI Samsung IP Television integration](https://driverstore.rticontrol.com/driver/samsung-ip-television): endpoint/pairing setup and context-sensitive availability guidance.
- [py-samsungtv client source](https://github.com/iloveicedgreentea/py-samsungtv/blob/master/pysamsungtv/client.py) and [HTTPS transport source](https://github.com/iloveicedgreentea/py-samsungtv/blob/master/pysamsungtv/connection.py): implementation reference for `createAccessToken`, `params.AccessToken`, JSON-RPC envelopes, and root HTTPS POST. This is third-party protocol evidence, not a Samsung guarantee, and its transport security policy was not adopted.
