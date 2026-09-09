## SamsungController v1.0.6

### Changes in this patch

- Windows now includes **per-user setup.exe installers** for x64 and Arm64 alongside the portable ZIPs. Setup adds a Start-menu entry, offers a desktop shortcut, and can launch the app when finished. Uninstall keeps saved profiles, pairing credentials, and settings.
- The portable Windows launcher is now **00 - Start SamsungController Server.exe**, first among files when sorted by name. It starts the server in the background and automatically opens the default browser after readiness. The optional foreground server and CLI remain available.
- Setup and Uninstall refuse while the new-version launcher/server is running; no adjustment is interrupted or process forcibly terminated. Quit older versions manually before upgrading.
- Added automated coverage for browser opening, portable launcher naming, and native Windows install/upgrade/uninstall. Updated setup and usage guidance. No separate .NET runtime or administrator privileges are required for the Windows packages.

### Included from v1.0.5

- **Reset all** in the pinned toolbar on **20pt WB** and **Color** resets the entire section's RGB values to nominal: **0** at every white-balance percentage (60 values), or **50** for every Custom color channel (18 values). These are explicit nominal targets, not display-specific factory calibration or a full picture/factory reset.
- The confirmation offers **Stage reset** in **On Apply** mode (no TV writes until Apply) or **Reset now** in **Immediately** mode. It replaces the selected section's unsent edits while preserving unrelated settings and pending edits. The white-balance switch and color-space mode are not changed.
- All rows must be loaded and enabled before resetting. Immediate resets use the existing verified RGB update/readback path; **Stop** or a failure discards remaining unsent reset targets without undoing delivered changes.
- Updated contextual Help, README, and beginner tutorial. Added 22 regression cases covering nominal targets, staging, immediate writes, confirmation/cancel, incomplete rows, failure, and Stop.
- Rebuilt self-contained packages for Windows, macOS, and Linux on x64 and Arm64. Both Mac apps and DMGs are signed, notarized, and stapled.

### Included from v1.0.4

- **RGB together − / +** in every 20pt white-balance percentage and Custom color block adjusts that block's Red, Green, and Blue by one while preserving their differences. Other blocks are unchanged.
- Group adjustments follow **On Apply / Immediately**, use the latest pending/queued values, and combine unsent clicks through the existing shared-selector update path. An in-flight command is never changed; protocol writes/readbacks remain per channel.
- All three channels must be available and contain valid numbers. If any channel reaches a limit, that direction is disabled for the group rather than clamping individual channels. Stop discards unsent changes without undoing delivered ones.
- Updated contextual Help, README, and beginner tutorial. The new controls passed automated regression tests and user verification in a local signed/notarized Mac test build before this release.
- Rebuilt self-contained packages for Windows, macOS, and Linux on x64 and Arm64. Both Mac apps and DMGs are signed, notarized, and stapled.

### Included from v1.0.3

- **IP Remote must be enabled on the display**, along with Power On with Mobile. Setup now makes this prerequisite explicit.
- Short, one-action-per-step pairing instructions with model-specific paths for S95F, smart Odyssey G95SC, and compatible recent/older Samsung TVs.
- Actual **Allow** dialog photo from Samsung's IP-control worksheet, with attribution; also available on the pairing page. This third-party illustration is not relicensed under the software license.
- **Trust this display and pair** inspects the certificate before sending a token or command. Confirmation pins it and pairs. Already-paired displays can pin/connect without new approval; changed certificates require explicit replacement approval. Trust is first-use confirmation, not independent identity verification.
- **README** beside Help opens the included guide in a new tab. README, tutorial, and approval photo work offline. Packaged apps include .NET; source builds require the .NET 10 SDK.
- macOS now uses drag-to-Applications **DMG installers**, for Apple silicon and Intel. Both app and disk image are signed, notarized, and stapled; Windows/Linux keep their self-contained ZIP/tar.gz formats.

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

Start with the [beginner tutorial](https://github.com/whipstein/SamsungController/blob/v1.0.6/docs/getting-started.md).

### Upgrade notes

Quit the old server before installing this release. Back up your private configuration directory; it is separate from the app and is retained during updates.

v1 uses **HTTPS IP Remote**, usually port **1516**, and requires its own initial TV approval. Existing v1 preview tokens and settings are reused. v0 WebSocket pairing, menu definitions, verification, and macros are preserved but do not drive the new GUI. The optional `samsungctl` CLI remains available for its existing workflows.

Support varies by TV, firmware, input, and mode. Unreported values are not defaults. Apply input/picture-mode changes separately; keep the physical signal stable while reading/writing. Stop does not undo commands already delivered.

### Downloads

- macOS: open the DMG, drag **SamsungController.app** onto **Applications**, eject, then launch from Applications. Do not run from the mounted image.
- Windows: run the **setup.exe** installer and use the Start-menu shortcut, or extract the portable ZIP and open **00 - Start SamsungController Server.exe**.
- Linux: extract the tar.gz and run **SamsungController.App**; optionally run `install-shortcut.sh`.
- arm64 is for Apple silicon/Arm64; x64 is for Intel/AMD.
- Verify archives against **SHA256SUMS.txt**. No Git clone or separate .NET runtime is required.

Closing the browser leaves the local server running. No automatic login/startup service is installed. Port 5050 is loopback-only.

### License

MIT + Commons Clause v1.0 with a Paid Services Exception. Paid calibration services and other business use are allowed; selling the tool or paid access to it is restricted. Source publication is not required. Read the complete included LICENSE; older MIT releases keep their original permissions.
