# SamsungController

Control Samsung displays locally using direct HTTPS IP commands. The web interface reads current settings from the TV, then applies your changes directly—without recording menu paths or counting verification passes.

This is the **v1 working branch**, `feature/ip-remote-v1`, version **1.0.0-alpha.1**. The stable published release is still v0.3.0 and has a different interface. This branch is not merged into `main` until explicitly approved.

## Features

- Display profiles, explicit TV pairing, and saved-token reuse.
- Picture, Sound, and System controls. Picture has Expert settings, 2-point white balance, 20-point white balance, and Color tabs.
- TV-queried current values with compact sliders, number fields, switches, and selections.
- Staged Apply or remembered Apply immediately, per-setting readback, progress, and Stop.
- A right-side slide-out Remote available from every page, light/dark mode, and a live, searchable, exportable communication log.
- Private local data, redacted reports, and Windows/macOS/Linux support.

Documented settings require **no manual verification**. Support still varies by display, firmware, input, and mode: failed or missing queries are shown, not replaced with defaults. Numeric controls use documented limits; no range-discovery query was found in the protocol references. See [ranges and availability](docs/direct-ip-interface.md#ranges-and-availability).

The old menu builder, verification pages, and macro editor are hidden; their files are preserved. The catalog does not cover every on-screen TV setting. The command-testing pages have been replaced with the communication log; they no longer expose app/channel/power experiments. No service-menu/factory reset or firmware-update commands are provided.

## Before starting

1. Put the computer and TV on the same trusted local network and turn the TV on.
2. Find the TV's IP address. A DHCP reservation helps keep it stable. The app does not scan for TVs.
3. Enable **IP Remote** in the TV's network expert settings, if available. Menu placement varies by display.
4. Use a current browser on the computer running SamsungController.

Direct IP uses HTTPS, normally port **1516**; some older displays use **1515**. It is separate from WebSocket ports 8001/8002 and uses its own token. Do not expose the controller or TV endpoint to the internet.

## Install and run this v1 preview from source

Install [Git](https://git-scm.com/downloads) and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Choose Arm64 for Apple silicon or Windows/Linux on Arm; choose x64 for Intel/AMD. Microsoft provides SDK instructions for [Windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows), [macOS](https://learn.microsoft.com/en-us/dotnet/core/install/macos), and [Linux](https://learn.microsoft.com/en-us/dotnet/core/install/linux).

In macOS Terminal, Windows PowerShell, or a Linux terminal:

```sh
git clone --branch feature/ip-remote-v1 https://github.com/whipstein/SamsungController.git
cd SamsungController
dotnet restore SamsungController.sln
dotnet build SamsungController.sln --no-restore --disable-build-servers -m:1
dotnet run --project src/SamsungController.Web --no-build --no-restore
```

Open [http://127.0.0.1:5050](http://127.0.0.1:5050). Keep the terminal open; Ctrl+C stops the server. For an existing checkout, preserve uncommitted changes before switching branches. Stop the old server before rebuilding/restarting; browser refresh alone cannot replace running server assemblies.

Tests use simulated TVs, not real hardware:

```sh
dotnet test SamsungController.sln --no-restore --disable-build-servers -m:1
```

## Downloadable packages (no Git or .NET required)

The [Releases page](https://github.com/whipstein/SamsungController/releases) provides self-contained packages. **The stable v0.3.0 download does not contain this v1 interface**; use the source instructions above until a v1 package is published. For stable-v0 usage, follow the [v0 beginner guide](docs/getting-started.md).

| Computer | Archive | Launcher after extracting |
| --- | --- | --- |
| Apple silicon Mac | `SamsungController-*-macos-arm64.tar.gz` | `Start SamsungController.command` |
| Intel Mac | `SamsungController-*-macos-x64.tar.gz` | `Start SamsungController.command` |
| Intel/AMD Windows | `SamsungController-*-windows-x64.zip` | `Start SamsungController.cmd` |
| Windows on Arm | `SamsungController-*-windows-arm64.zip` | `Start SamsungController.cmd` |
| Intel/AMD Linux | `SamsungController-*-linux-x64.tar.gz` | `start-samsungcontroller.sh` |
| Arm64 Linux | `SamsungController-*-linux-arm64.tar.gz` | `start-samsungcontroller.sh` |

1. Download the correct archive and extract the **whole folder**. Do not run inside a ZIP or move individual binaries out of it.
2. Run its launcher. It starts the server and opens the browser; keep its terminal/command window open.
3. If the browser does not open, navigate to `http://127.0.0.1:5050`.
4. Close the launcher window or press Ctrl+C to stop.

Platform notes:

- **macOS:** Control-click the `.command` launcher and choose Open. Packages are unsigned/not notarized. If blocked, use System Settings → Privacy & Security → Open Anyway only for an official release archive. Terminal fallback for that trusted extracted folder: `xattr -dr com.apple.quarantine /path/to/SamsungController`.
- **Windows:** Use Extract All first. SmartScreen may flag unsigned packages; confirm the official source before More info → Run anyway.
- **Linux:** Enable execution in file properties, or run `chmod +x start-samsungcontroller.sh` then `./start-samsungcontroller.sh` from the extracted folder. Browser opening uses `xdg-open` or `gio`; otherwise enter the URL manually.

Releases include `SHA256SUMS.txt`. Optional integrity checks: macOS `shasum -a 256 <archive>`, Linux `sha256sum <archive>`, PowerShell `Get-FileHash <archive> -Algorithm SHA256`.

## First connection

1. On **Display**, choose a saved display. Existing IP Remote preview profiles and tokens are reused. For a new TV, choose **Add display**, enter its address/name and port, then **Save display**. Saving sends nothing to the TV.
2. While editing, open **Certificate trust and timeouts** if needed. Prefer a trusted SHA-256 fingerprint. Alternatively, explicitly allow an untrusted certificate for this endpoint on your trusted LAN. That weakens server authentication; it is not global. An entered fingerprint must still match.
3. Select **Pair with TV** for a new endpoint and accept its approval dialog. Successful pairing saves the token, reads the TV, and opens Menu.
4. For an already paired endpoint select **Connect and open Menu**. Do not pair again merely to reconnect.
5. The header shows TV-reported input and picture mode. Connect/Disconnect and Stop remain visible on every page.

Connect preloads every Menu section and the catalog's other documented read/list methods for the current input/picture mode. Already-enabled 20-point and Custom color grids load all rows and restore their selectors. Connect does not automatically enable inactive modes. Use the explicit Refresh buttons described below to also read 20-point values while Off. Loading progress and Stop are available; Menu's settings-load details report unavailable values. Unsupported fields are never filled with defaults.

The app requests HTTPS keep-alive and retains one pooled TCP/TLS connection for the selected endpoint, including while idle. Requests reuse your saved token; they do not pair again. The TV can still close its connection, requiring a new TCP/TLS connection on the next request. “Connected” means the latest check succeeded, not continuous TV-state monitoring. The header status tooltip reports whether the last query reused TLS or the TV requested closure. Transport/authentication failures clear Connected; a failed Connect leaves the normal Connect button available.

## Change settings

1. Open **Menu**. Connection-loaded values are ready without another scan when changing tabs. Use **Refresh TV values** after changing the HDMI signal (including 8-bit/10-bit), picture mode, or an incomplete load. It rereads all settings and available calibration rows using the saved connection, discards unsent edits, and reevaluates hidden/disabled controls. No reconnect or pairing is needed. For a quicker read of just the current tab, choose **Refresh section**. Opening tabs does not restart a stopped load.
2. Choose Picture, Sound, or System. Picture has separate calibration tabs; 2-point gains and offsets are grouped in two columns.
3. Adjust a slider, number, switch, or selection. With default **Wait for Apply**, the TV is unchanged; pending targets are distinct from queried current values.
4. Select **Apply N pending** at the top right. Fresh values/context are checked, settings are sent sequentially, and each result is queried. A mismatch stops later settings.
5. Expand **Update behavior** to enable **Apply immediately**. Apply or discard pending edits first. Sliders send on release, not every drag event. This preference survives restart.
6. Use **Stop** during an operation. Delivered commands are not undone. Last update retains originals and per-setting outcomes. For an uncertain write, inspect/refresh the TV and close its review explicitly before more writes.

Apply input/picture-mode and calibration-mode changes separately from other pending settings. The app does not assume a menu topology or external-signal setting bank.

In **Expert settings**, click and drag a box's heading or background and drop it at the highlighted edge to rearrange the grid—there are no separate move controls. Related controls move together: Gamma with its adjustment sliders, and Picture Clarity with its motion controls. Sliders, switches, selections, buttons, and text inputs still work normally. For keyboard ordering, Tab to a box and use **Alt + arrow keys** (Option on macOS). Your layout saves automatically across restarts, including the positions of temporarily hidden settings. **Reset layout** restores the default order. Moving boxes never sends TV commands, changes values, or clears pending edits.

For **20-point white balance**, all percentages from 5% through 100% appear together, each with RGB sliders, −/+, and number inputs. **Refresh TV values**, or **Load/Reload all rows / Refresh section** on the 20-point tab, reads these even when WB starts Off: it temporarily enables WB, reads each interval, restores the original interval, then restores and confirms Off. Read values remain visible but disabled until you turn WB On and Apply. Initial Connect and tab visits do not enable it automatically. For **Custom color**, select/apply Custom: Red, Green, Blue, Yellow, Cyan, and Magenta each have a fixed RGB row. There is no interval/color dropdown to manage. Loading visits each selector and restores the original selection; **no RGB values are changed**. Apply automatically addresses each edited row, checks its readback, and restores the selector on success. Grid reads reuse HTTPS connections when supported by the TV and share context checks between rows to reduce round trips. Progress, elapsed time on completion, and Stop remain available. See the [detailed walkthrough](docs/direct-ip-interface.md).

If a temporary WB read is stopped or fails, it may leave WB On. **Stop sends no further commands, including restoration.** Menu shows the saved original display, input, picture mode, and interval with **Restore 20-point WB to Off**, plus an option to confirm manual restoration. Keep the original physical signal active when restoring; nothing resumes after restart.

Direct settings do not navigate TV menus, so there is no return-to-video script or “stay on last adjusted item” option. Header Home, Back, and Exit menu buttons are explicit single keys, not a guaranteed video anchor.

## Slide-out remote

Select **‹ Remote** on the right edge of any page. The remote fills the window height, with a fixed close header and a scrolling button area, without navigating away; **Menu** remains a normal page. Use the directional pad, Home/Menu/Back/Exit, volume buttons, or the **More keys** selector. Close with **Close ×**, **Escape**, or a click outside the panel. Opening/closing sends no TV commands. **Remote key presses retain loaded settings and pending edits**—they do not refresh or invalidate them. Use Menu's Refresh buttons when you want to reread the TV; displayed values may otherwise be stale. Apply still checks a fresh baseline before writing. Remote keys are disabled while disconnected, while another request is running, or when an interrupted operation requires review. **Stop** is available inside the panel too.

## Diagnostics and troubleshooting

- **All-settings load incomplete / changed signal:** let the external signal settle, then select **Refresh TV values**. This starts a fresh all-settings read, even if the TV reports the same HDMI port and picture-mode name. Keep the signal unchanged while loading. Stop cancels it; tab visits never resume it automatically.
- **Connection fails:** check TV power, IP Remote, address/port, certificate policy, LAN permissions, VPN/firewall restrictions, and the actual error. Do not repeatedly re-pair unless authorization was rejected.
- **macOS receives no response:** check System Settings → Privacy & Security → Local Network for the app hosting the process (Terminal, your editor, or ChatGPT when started there). A correct IP and a working browser do not prove the server process has local-network permission. Restart the affected host app/server after changing permission.
- **Approval accepted but no connection:** verify the token was received/saved; inspect the pairing error. Connect reuses saved tokens.
- **Gray setting:** a prerequisite is unmet—for example Judder Reduction requires Picture Clarity / Auto Motion Plus set to Custom. Apply that prerequisite to refresh its dependent controls. Rejected/absent controls without an unmet prerequisite are hidden for the current context; Refresh checks them again. Defaults are never used as current values.
- **Out-of-range value:** correct the inline error; no command is sent for invalid local input. TV rejections stop the operation without retrying, except for the readback-confirmed 2-point case below.
- **2-point WB changed but TV reported `−32002`:** independent readback must confirm the target, all other five channels, and unchanged context before the row is marked **Applied with TV warning**. No retry is sent; the original error stays in Communication log. Incomplete or mismatched reads still stop. See [2-point white balance](docs/direct-ip-interface.md#2-point-white-balance).
- **Interrupted update:** the journal survives restart; nothing resumes automatically. Inspect its original/target values and the actual TV before closing review.
- **Need a log:** open **Communication log** in the sidebar. Choose Live session or Saved history, search or select Errors only, then expand a row for TX/RX JSON. Under Privacy and export, use **Export filtered log** to download every matching exchange, not just the current page. Tokens are always redacted; IP/MAC/UUID/serial and SHA-256 redaction default to on. Review exports before sharing.

See the [communication-log guide](docs/direct-ip-interface.md#communication-log) for history, export, and interrupted-operation recovery. The command reference and early preview test guide are development archives; their testing UI is no longer exposed.

## Private data, command line, and updates

IP profiles, credentials, preferences, and update journals live in the `ip-remote` subdirectory of your data folder:

| Platform | Default data folder |
| --- | --- |
| macOS | `~/Library/Application Support/SamsungController` |
| Windows | `%APPDATA%\SamsungController` |
| Linux | `$XDG_CONFIG_HOME/SamsungController`, or `~/.config/SamsungController` |

`SamsungController__ConfigurationDirectory` overrides the root. Existing menu definitions, verification sidecars, macros, and calibration files are preserved. The new UI does not interpret old calibration files as queried TV state or silently translate macros.

The CLI remains independent of the new web UI:

```sh
dotnet run --project src/SamsungController.Cli -- --help
```

Packaged binaries are `samsungctl` (macOS/Linux) and `samsungctl.exe` (Windows); run with `--help` in a terminal. The [v0 CLI guide](docs/v0-user-guide.md#use-the-command-line-interface) covers retained WebSocket/key/macro commands, not the direct-IP web settings API.

To update source: stop the server, preserve edits, `git pull --ff-only`, then restore/build/run again. To update packages: stop the old copy, extract the new version into a separate folder, and launch it. Back up private data before moving between major versions. Restart the server and refresh the browser. A working-branch commit does not create a release or merge into `main`.

For contributors: [development roadmap](docs/development-roadmap.md), [direct-interface design](docs/direct-ip-interface.md), and [preserved v0 menu-file format](docs/menu-definition-file-format.md).
