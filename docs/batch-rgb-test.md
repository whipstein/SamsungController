# Test batch requests and a three-color 20-point update

This diagnostic evaluates what **your display and current state** accept. It is not a production speed setting. Normal Menu uses its existing documented commands regardless of the outcome.

The experimental buttons have been removed from Menu and Communication log. This page is retained for historical diagnostics and unresolved-test recovery, not as a required setup or performance-tuning step. An unfinished RGB experiment still exposes its recovery link in the header.

**Known risk:** read-only JSON-RPC arrays have caused a display's IP connection to stall for the full request timeout rather than returning an unsupported-batch error. Do not repeat a failing batch. Both batch buttons are blocked for endpoints with a failed/interrupted batch in their private diagnostic history; this is recovered on app restart and is not cleared by reconnecting or pairing again. It is a safety block, not a claim that every model or firmware lacks support. The separate single-method RGB experiment remains experimental and is not a recovery procedure.

## Recover after a stalled batch, without requesting new pairing

1. Restart the updated server and refresh the page. Existing failed attempts are recognized from private saved history; you do not need to trigger another failure.
2. If a request is still running, click **Stop** and wait for it to finish.
3. On **Display** or **Batch / RGB test**, click **Reset connection (keep pairing)**.
4. The app discards its HTTPS pool and sends only `getTVStates` followed by `getVideoStates` on a fresh connection, using the existing token and unchanged certificate policy. It does not send `createAccessToken`, a batch, any setting or selector writes, or a full settings scan. No request is automatically replayed.
5. On success, open Menu and explicitly refresh the values when ready. If an RGB test was unfinished, restore/end it before loading other calibration rows; saved originals remain protected. Cached readings/unsent edits are cleared by connection reset.
6. If it fails, inspect/export **Communication log → Saved history**, including the failed batch and subsequent single-request attempts. A fresh TLS/authentication failure distinguishes a continuing TV/network problem from simply reusing the old session. A timeout alone is not evidence that the token was revoked. A false `NewTlsHandshake` on a failed exchange means no new handshake completed, not proof a healthy socket was reused.

Reset clears the application's connection, **not the TV's internal IP service**. If that service remains wedged or the TV truly rejects the saved token, this cannot guarantee recovery without TV-side intervention. It does not silently fall back to pairing. Failed transports are also discarded automatically; explicit Connect starts fresh. Experimental batches use an isolated connection which is discarded after completion, success or failure.

The two RGB experiments answer different questions:

| Test | Experimental write on the wire | What independent readback checks |
| --- | --- | --- |
| RGB batch | One HTTP POST containing three JSON-RPC objects: `WB20P.RedControl`, `WB20P.GreenControl`, `WB20P.BlueControl` | Whether all three targets applied to the selected percentage |
| RGB fields | One HTTP POST with a single `WB20P.RedControl` object and three flat parameters: `WB20P.Red`, `WB20P.Green`, `WB20P.Blue` | Whether the TV honors Green/Blue too, or ignores/rejects the extra parameters |

The second payload is deliberately **experimental, not documented as a combined RGB setter**. The diagnostic does not invent a method name or loosen normal command validation. Targets are absolute channel values, not relative deltas.

## Before starting

1. Rebuild/restart the updated server and connect normally. Open **Menu → Picture → 20-point white balance** and enable 20-point mode if necessary.
2. Finish or discard pending Menu edits. Keep the same display, physical HDMI signal, bit depth, and picture mode throughout a test and its restoration. Do not operate another remote/controller during the test.
3. For an explicit diagnostic or unfinished recovery, use `/diagnostics/batch-rgb` on your local controller (or the header's recovery link, when shown). Opening the page sends nothing; normal Menu and Communication log no longer advertise it.

## A. Check read-only batch support

1. Click **Run read-only batch test**.
2. This sends `getTVStates` and `getVideoStates` together in one HTTP request. No mode, selector, or RGB setting is written.
3. “Read batch accepted” requires both valid replies with matching unique IDs. Reordered replies and exact decimal-text IDs are supported. Missing, duplicate, unexpected, malformed, or failed replies are not success.
4. A whole-array error such as `-32004` is reported without retrying the calls individually. If rejected, you can still try experiment C; read-only batch success is required only for experiment B.

Read acceptance does not prove write support, performance improvement, or atomic execution. The JSON-RPC specification permits [batch operations to run in any order](https://www.jsonrpc.org/specification#batch), so a percentage selector is never included in the RGB batch.

## B. Test three RGB methods in one HTTP request

1. Choose one **Percentage**, for example 50%, then click **Prepare RGB test**.
2. Preparation queries the current input, picture mode, WB mode, and original percentage. It selects your test percentage using the ordinary checked selector command, then independently reads its Red, Green, and Blue originals. It does not change RGB or enable WB automatically.
3. Check the displayed percentage/context and saved originals. Each target is one step higher than its original, or one lower when the original is already +50. All three must differ so ignored fields are detectable. Missing/out-of-range readings prevent the test; no originals are guessed.
4. Tick the explicit display/signal confirmation, then click **Test RGB batch — one HTTP request** within two minutes of preparation. It rechecks the originals/context, then sends exactly one experimental POST. The test percentage stays selected.
5. Read the results table. Setter acknowledgment alone is insufficient: the app queries all three channels separately. A partial change is shown as, for example, “Not verified: 2/3 targets matched.” Check the actual TV as well.
6. Click **Restore originals and original percentage**. This is an explicit recovery action using known individual channel commands, only for channels that need restoring, with independent readback. It is not a hidden fallback to complete a failed experiment.
7. Click **Download batch / RGB report** before preparing another experiment. Save this report to share with the developer.

## C. Test one method containing all three RGB fields

1. Finish restoration from B, or start here if the read batch was unsupported.
2. Prepare the percentage again and check the new saved originals/targets. Each preparation permits only one experimental send; it cannot be reused.
3. Tick the confirmation and click **Test RGB fields — one method**.
4. Examine all three independent readbacks. If only Red changes, that is **not combined RGB support**, even if the reply acknowledged success.
5. Restore the originals/percentage and download a second report. Send both reports and any visual observations.

## Stop, disconnect, or unexpected values

- **Stop** cancels the current operation. It cannot retract a batch already delivered, and it does not send a late automatic restore. Some channels may already have changed.
- The pending test and originals are saved in your private data directory as `ip-remote/rgb-probe.json`. Startup never resumes it or sends TV commands. Normal writes, remote keys, and selector-changing grid loads stay blocked while it needs recovery.
- Reconnect to the original display, keeping its original physical signal/mode. An unfinished test suppresses the usual connection preload, allowing you to return directly to this diagnostic and explicitly restore.
- If values are unavailable, the input/mode changed, or a channel differs from both the original and test target, restoration stops rather than overwriting an unexpected change. Restore/check the TV manually, then use **Manual recovery** and explicitly confirm closure. Manual closure sends nothing and grants no protocol verification.
- Preparation may move a percentage before a later query fails. If no RGB request was attempted, only the original percentage needs restoring.
- Review/download the report before preparing another test: the per-channel summary and recovery file describe the latest experiment. The Communication log retains earlier request/reply traffic.

## What to send back

Download one report after each experiment. Include the TV model/firmware, physical signal/bit depth, picture mode, tested percentage, and whether the actual TV values matched the report. Reports redact tokens, device identifiers, and certificate SHA fingerprints. Review any user-entered labels before sharing.

Connection checks, preparation, independent readback, and restoration are intentionally separate requests. To assess batching, inspect the single experimental write in **Communication log** (search `batch:WB20P.RGB` or the label `RGB test`), not the total number of diagnostic exchanges. A combined write reduces write-request count only; these safety reads are not a speed benchmark.
