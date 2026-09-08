## SamsungController v1.0.0

Direct IP control is now the default interface on **main**, replacing the v0 menu-traversal workflow.

### Highlights

- Read actual Samsung TV values and apply documented HTTPS IP settings directly.
- Picture, 2pt WB, 20pt WB, Color, Sound, and System sections, with fixed percentage/color calibration rows.
- Coalesced queued adjustments, optional pre-change queries, per-row Apply, readback checks, and Stop/recovery handling.
- A compact pinned toolbar, contextual Help, section scroll memory, light/dark themes, rearrangeable controls, and the slide-out remote.
- Communication logs with browsing, filtering, redaction, and export.
- Native launchable, self-contained apps for Windows, macOS, and Linux on x64 and Arm64. The local server runs in the background, without a terminal window. Reopen the app to reuse it; **Display → Quit app** stops it.
- Official macOS release apps are Developer ID signed, notarized, and stapled. Windows binaries are unsigned. Linux packages include an optional desktop-shortcut installer.

Start with the [beginner tutorial](https://github.com/whipstein/SamsungController/blob/v1.0.0/docs/getting-started.md).

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
