## SamsungController v1.0.2

### Changes in this patch

- Move **Quit app** to the very top, beside **Stop**, on every page. It remains available when disconnected; stop active TV operations before quitting.
- Allow the top-row controls to wrap on narrow windows and update the in-app help and user guides.
- Upgrade the release workflow to Node.js 24-native artifact upload/download actions, removing the deprecated Node.js 20 action warning on new runs.
- Clarify certificate-trust failures: they mean the display was reached. Configure trust under **Display → Edit display → Certificate trust and timeouts**; this release does not change certificate policy or pairing credentials automatically.

### Included from v1.0.1

- Correct macOS Local Network permission attribution: the native signed app remains alive while the server runs, instead of replacing itself with a detached .NET launcher. Published .NET executables now receive distinct Mach-O UUIDs before signing.
- Add the approved **TV + RGB sliders** icon to the Mac app, Windows executable, and Linux applications-menu shortcut.
- Show safe transport/socket error classifications so routing/permission failures are distinguishable from TV HTTP rejection. No credentials or raw exception messages are exposed.
- Preserve existing private profiles, pairing tokens, and settings. No re-pairing or TV-setting changes are performed by this update.

**Mac upgrade:** quit before replacing the application. In v1.0.1, use **Display → Quit app**; from v1.0.2 onward, use **Quit app** at the very top of any page. Copy the new app into Applications, open it there, and try Connect/Pair. Approve **SamsungController** if macOS asks for Local Network access. If already listed but disabled, enable it in System Settings → Privacy & Security → Local Network and quit/reopen the app. Loopback package tests do not verify LAN permission or compatibility with a particular display; those require a real installed-app connection test.

Direct IP control is now the default interface on **main**, replacing the v0 menu-traversal workflow.

### Highlights

- Read actual Samsung TV values and apply documented HTTPS IP settings directly.
- Picture, 2pt WB, 20pt WB, Color, Sound, and System sections, with fixed percentage/color calibration rows.
- Coalesced queued adjustments, optional pre-change queries, per-row Apply, readback checks, and Stop/recovery handling.
- A compact pinned toolbar, contextual Help, section scroll memory, light/dark themes, rearrangeable controls, and the slide-out remote.
- Communication logs with browsing, filtering, redaction, and export.
- Native launchable, self-contained apps for Windows, macOS, and Linux on x64 and Arm64. The local server runs in the background, without a terminal window. Reopen the app to reuse it; **Quit app** at the top stops it.
- Official macOS release apps are Developer ID signed, notarized, and stapled. Windows binaries are unsigned. Linux packages include an optional desktop-shortcut installer.

Start with the [beginner tutorial](https://github.com/whipstein/SamsungController/blob/v1.0.2/docs/getting-started.md).

### Upgrade notes

Quit the old server before installing this release. Back up your private configuration directory; it is separate from the app and is retained during updates.

v1 uses **HTTPS IP Remote**, usually port **1516**, and requires its own initial TV approval. Existing v1 preview tokens and settings are reused. v0 WebSocket pairing, menu definitions, verification, and macros are preserved but do not drive the new GUI. The optional `samsungctl` CLI remains available for its existing workflows.

Support varies by TV, firmware, input, and mode. Unreported values are not defaults. Apply input/picture-mode changes separately; keep the physical signal stable while reading/writing. Stop does not undo commands already delivered.

### Downloads

- macOS: extract the ZIP and copy **SamsungController.app** to Applications.
- Windows: extract the ZIP and open **SamsungController.App.exe**.
- Linux: extract the tar.gz and run **SamsungController.App**; optionally run `install-shortcut.sh`.
- arm64 is for Apple silicon/Arm64; x64 is for Intel/AMD.
- Verify archives against **SHA256SUMS.txt**. No Git clone or separate .NET runtime is required.

Closing the browser leaves the local server running. No automatic login/startup service is installed. Port 5050 is loopback-only.

### License

MIT + Commons Clause v1.0 with a Paid Services Exception. Paid calibration services and other business use are allowed; selling the tool or paid access to it is restricted. Source publication is not required. Read the complete included LICENSE; older MIT releases keep their original permissions.
