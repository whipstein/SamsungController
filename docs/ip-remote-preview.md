# IP Remote preview: setup and staged verification

Version: **1.0.0-alpha.1** · Branch: **feature/ip-remote-v1**

This is the first implementation checkpoint for direct Samsung display communication, not a replacement release. The stable v0.3.0 WebSocket remote and menu controls remain available. `main` must not be merged or replaced until the repository owner explicitly approves.

The supplied IP Remote handoff is design background. This repository is C#/.NET, so the Python examples informed the protocol shape, not the implementation language. Pairing, getter names, and transport conventions come from the sources below; support on your actual displays remains unverified until we collect their responses.

## What this checkpoint does

- Sends **one explicitly requested pairing call**: `createAccessToken`.
- Sends only **two explicitly requested read methods**: `getTVStates` and `getVideoStates`.
- Saves a separate credential per HTTPS host/port. Existing WebSocket tokens are never read or replaced by this client.
- Shows method outcomes, timestamps, raw JSON fields/types, and differences from the previous successful response in the same annotated context.
- Preserves unknown fields. Missing fields say **Not reported**, not zero, false, or a previous value. A successful empty result does not prove any control is available.
- Provides cancellation, finite timeouts, redacted request/response history, and a downloadable report.

It does **not** write settings, infer control mappings, discover TVs, scan ports, poll in the background, subscribe to picture changes, retrieve menu trees or screenshots, issue reset/calibration commands, or automatically fall back to remote keys. It does not alter menu definitions, verification sidecars, saved calibration values, or predicted menu position.

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
4. Download the diagnostic report, keeping **Redact IP, MAC, and UUID identifiers** checked unless those details are needed privately.
5. Send the report back for review before we implement writes. Include what the TV did, which port/trust option worked, and whether an approval prompt appeared.

Repeat for the other display using **New display** or the saved profile selector. Do not presume an S95F and an Odyssey support the same methods.

After a successful first read, restart the app and try reading the saved profile **without pairing again**. This checks that token persistence works on that display. A successful response is evidence for that method in that context, not proof of every field or a lasting availability guarantee.

## 5. Next checkpoint: verify what the fields mean

After reviewing initial replies, we will choose a small, reversible manual test:

1. Record the original TV setting value and capture a baseline response.
2. Change **one** non-destructive setting using the physical remote. Keep input, picture mode, and signal fixed.
3. Label the observation accurately (for example, **TV Brightness manually 20 → 21**) and read again.
4. Compare which field changed, including its raw type. No changed field is also a useful result; it does not justify guessing a setter.
5. Restore the original value manually, read again, and label the restoration.

Do not test resets, service-menu functions, or calibration-start commands. In particular, **TV Brightness/backlight and Shadow Detail must be distinguished empirically**. Historical API names are not a reliable mapping to modern menu labels. The preview does not map any returned field to your calibration controls or assume a value range. White balance, 20-point interval grids, custom color, and related read/write support remain unproven.

The comparison table unions field names from the latest response and previous successful response in the same saved context. A field that disappears is marked **Not reported**. When a new request fails, its card reports the failure and marks any prior success as historical/stale; old values are not presented as a current reading.

## Files, privacy, and troubleshooting

Files live in an `ip-remote` subdirectory of the same personal configuration root used by the web app, honoring `SamsungController:ConfigurationDirectory` if configured:

| File | Contents |
| --- | --- |
| `tokens.json` | IP Remote credentials, keyed by HTTPS endpoint; private, never share |
| `profiles.json` | Saved endpoint/trust options and user-entered context; no tokens |
| `diagnostics.ndjson` | Append-only timestamped, token-redacted observations; device identifiers retained |

On Unix systems, the IP Remote directory is restricted to the user (0700) and final files to user read/write (0600). Windows uses the personal configuration folder's user ACL. Credentials are local plaintext files, **not OS-vault encrypted**. Do not commit or share that directory. Its normal repository path is ignored.

The page retains the last 100 observations in memory; a restart starts an empty page history but leaves the private log and saved token in place. Reports contain that in-memory history across tested contexts/displays, not the entire log. Token values are always scrubbed, including pairing replies and token echoes; malformed/non-JSON bodies are omitted to avoid leaking credentials. IP/MAC/UUID redaction is on by default for downloaded reports. Review annotations and model names before sharing; these are intentionally readable. This separate log policy does not change the older WebSocket Protocol page's logging behavior.

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
- [x] Exact two-getter allowlist; no direct writes, arbitrary methods, automatic retry, or fallback keys.
- [x] Scoped TLS policy, cancellation, timeouts, bounded response size, response correlation, classified errors.
- [x] Unknown/raw typed fields, missing-value handling, historical comparisons, redacted logs and reports.
- [x] Automated fake-response tests, including interactive page event handling and existing regression tests.
- [ ] Live pairing/readback/token reuse confirmed on S95F with recorded firmware and context.
- [ ] Live pairing/readback/token reuse confirmed on Odyssey G9 with recorded firmware and context.

### Phase B — establish real mappings

- [ ] Baseline → one manual change → readback → manual restore, on each relevant display.
- [ ] Confirm brightness/backlight/shadow-detail identities and supported values without assuming old ranges.
- [ ] Verify input, picture-mode, and signal effects; record missing/unsupported states distinctly.
- [ ] Review evidence together and approve a single direct-write experiment.

### Phase C — one reversible direct write, only after approval

- [ ] Read original value, validate capability/context and bounds, show intended change, request confirmation.
- [ ] Write once, read back, verify visually, restore the original, and verify restoration.
- [ ] Define cancellation/failure handling that never claims success or blindly retries an ambiguous write.

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
