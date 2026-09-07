# IP Remote preview: setup and staged verification

Version: **1.0.0-alpha.1** · Branch: **feature/ip-remote-v1**

This is the first implementation checkpoint for direct Samsung display communication, not a replacement release. The stable v0.3.0 WebSocket remote and menu controls remain available. `main` must not be merged or replaced until the repository owner explicitly approves.

The supplied IP Remote handoff is design background. This repository is C#/.NET, so the Python examples informed the protocol shape, not the implementation language. Pairing, getter names, and transport conventions come from the sources below; support on your actual displays remains unverified until we collect their responses.

## What this checkpoint does

- Sends **one explicitly requested pairing call**: `createAccessToken`.
- Sends **two explicitly requested read methods**: `getTVStates` and `getVideoStates`.
- Offers one **explicitly confirmed contrast experiment**: fresh reads → current contrast minus one → readback → visual pass/fail → original value → restoration readback. No arbitrary setter/value editor is provided.
- Saves a separate credential per HTTPS host/port. Existing WebSocket tokens are never read or replaced by this client.
- Shows method outcomes, timestamps, raw JSON fields/types, and differences from the previous successful response in the same annotated context.
- Preserves unknown fields. Missing fields say **Not reported**, not zero, false, or a previous value. A successful empty result does not prove any control is available.
- Provides cancellation, finite timeouts, redacted request/response history, and a downloadable report.

Outside the guarded contrast experiment it does **not** write settings. It does not infer other control mappings, discover TVs, scan ports, poll in the background, subscribe to picture changes, retrieve menu trees or screenshots, issue reset/calibration commands, or automatically fall back to remote keys. It does not alter menu definitions, verification sidecars, saved calibration values, or predicted menu position.

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
5. Send the report back for review before we implement writes. Include what the TV did, which port/trust option worked, and whether an approval prompt appeared.

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

## 6. Current checkpoint: one guarded contrast write

Evidence collected on 2026-09-07: the user confirmed saved-token reuse after restarting, and the S95F firmware **1301** capture showed `contrast` **45 → 44 → 45** during the manual change/restore test. The TV reported **HDMI4** and **FilmmakerMode**, with user annotations describing RGB 8-bit. This establishes contrast readback in that context, not setter support, HDR status, other settings, or Odyssey compatibility.

The experiment uses `contrastControl` with `params.contrast` and the separate access token. The [Samsung command list](https://s7d2.scene7.com/is/content/SamsungUS/samsungbusiness/products/tvs/tvci-8-21-17/resource-center/control-codes/TV_IP_CommandList_v.1.1_1Pager.pdf) identifies this method; the [reference client implementation](https://github.com/iloveicedgreentea/py-samsungtv/blob/master/pysamsungtv/client.py) supplies the parameter shape. Their historical 0–100 envelope is **not** a claim about this display's actual slider range. Live setter verification is the next test, not an already completed milestone.

1. Rebuild and restart the working branch using section 1. Open **IP Remote · Preview**. Reuse the saved token; do not pair again unless authorization actually fails.
2. Save accurate model, firmware, input, picture-mode, and signal annotations. Keep the physical remote available for inspection or recovery. Do not run other adjustments simultaneously.
3. In **4. Guided contrast test**, choose **Prepare contrast test (read only)**. This reads `getTVStates` and `getVideoStates` and shows the original contrast, proposed one-step-lower target, and reported input/mode. It changes nothing. Missing input/mode, a non-integer contrast, or a zero contrast blocks the experiment.
4. Check the actual Contrast value on the TV. Confirm that both the original and target are valid on this display and that input, mode, and signal conditions will stay unchanged. Check the confirmation box. Preparation expires after two minutes; prepare again if needed.
5. Select **Apply one-step contrast test…**, then accept the explicit confirmation dialog. The app re-reads input/mode and video values before sending **one** contrast write. If the original or other reported video fields changed, it stops before writing. After an acknowledged write it reads again; the returned contrast must equal the target. The setter's own reply is not accepted as readback evidence.
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

## Files, privacy, and troubleshooting

Files live in an `ip-remote` subdirectory of the same personal configuration root used by the web app, honoring `SamsungController:ConfigurationDirectory` if configured:

| File | Contents |
| --- | --- |
| `tokens.json` | IP Remote credentials, keyed by HTTPS endpoint; private, never share |
| `profiles.json` | Saved endpoint/trust options and user-entered context; no tokens |
| `diagnostics.ndjson` | Append-only timestamped, token-redacted observations; device identifiers retained |
| `contrast-test.json` | Latest contrast experiment: original/target, context, readback/visual outcome, and restart-recovery state; no token |

On Unix systems, the IP Remote directory is restricted to the user (0700) and final files to user read/write (0600). Windows uses the personal configuration folder's user ACL. Credentials are local plaintext files, **not OS-vault encrypted**. Do not commit or share that directory. Its normal repository path is ignored.

The page retains the last 100 observations in memory; a restart starts an empty page history but leaves the private log and saved token in place. Reports contain that in-memory history across tested contexts/displays, not the entire log. Token values are always scrubbed, including pairing replies and token echoes; malformed/non-JSON bodies are omitted to avoid leaking credentials. IP/MAC/UUID redaction is on by default for downloaded reports. Review annotations and model names before sharing; these are intentionally readable. This separate log policy does not change the older WebSocket Protocol page's logging behavior.

Certificate SHA-256 fingerprint redaction is also on by default, independently of IP/MAC/UUID redaction. It masks `CertificateSha256`, `NormalizedCertificatePin`, and `ObservedCertificateSha256` throughout the export, including saved-context copies, method summaries, embedded request/response JSON, and matching fingerprint echoes. This is export-only: it never changes the certificate pin used to connect or rewrites the private log. A fingerprint is a certificate identifier, not a private key; it is still useful to redact when sharing display diagnostics. Plain numeric firmware values, JSON-RPC versions, and text request IDs remain visible rather than being interpreted as abbreviated IPv4 addresses.

Reports also include the latest contrast test's original/target, context, outcomes, and recovery state, including after a restart. That summary survives independently of the in-memory request history. The same token, identifier, and fingerprint redactions apply to its nested profile and baseline fields. The private recovery file is not a portable capability declaration or part of a shared menu definition.

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
- [x] Exact two-getter read allowlist; the later contrast experiment is a separate explicit action. No arbitrary methods, automatic retry, or fallback keys.
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
- [ ] Verify the direct contrast write, visual result, and restoration on the S95F; review the exported report before expanding scope.

### Phase D — expand only demonstrated capabilities

- [ ] Add verified controls with separate read/write support records, context keys, and capability-gated UI.
- [ ] Add bounded state polling only if useful and proven safe; do not assume subscriptions exist.
- [ ] Investigate advanced white balance and custom-color methods independently, including any special mode/authorization requirements.
- [ ] Revisit integration with existing menu/calibration workflows using evidence, not implicit fallback.
- [ ] Owner explicitly approves merging the working branch into `main`.

Checked code milestones are **not hardware compatibility claims**. Automated tests use fake handlers and fixtures; no actual display was contacted during implementation.

## Protocol references

- [Samsung TV IP command list v1.1 (manufacturer PDF)](https://s7d2.scene7.com/is/content/SamsungUS/samsungbusiness/products/tvs/tvci-8-21-17/resource-center/control-codes/TV_IP_CommandList_v.1.1_1Pager.pdf): historical getter names and command families, not proof of modern model ranges.
- [RTI Samsung IP Television integration](https://driverstore.rticontrol.com/driver/samsung-ip-television): endpoint/pairing setup and context-sensitive availability guidance.
- [py-samsungtv client source](https://github.com/iloveicedgreentea/py-samsungtv/blob/master/pysamsungtv/client.py) and [HTTPS transport source](https://github.com/iloveicedgreentea/py-samsungtv/blob/master/pysamsungtv/connection.py): implementation reference for `createAccessToken`, `params.AccessToken`, JSON-RPC envelopes, and root HTTPS POST. This is third-party protocol evidence, not a Samsung guarantee, and its transport security policy was not adopted.
