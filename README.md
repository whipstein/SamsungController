# SamsungController

Control Samsung displays locally using direct HTTPS IP commands. The web interface reads current settings from the TV, then applies your changes directly—without recording menu paths or counting verification passes.

**v1 direct IP control is the default on `main`.** It replaces the v0 menu-traversal GUI. Start with the [step-by-step beginner tutorial](docs/getting-started.md).

> **IP Remote must be enabled on the display before pairing or connecting.** Also turn **Power On with Mobile** on in the same TV menu. Keep the display on and its physical remote nearby.

## Requirements

| Requirement | Downloaded desktop app | Running from source |
| --- | --- | --- |
| .NET | Included; **no SDK or runtime installation needed** | **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)**; the runtime alone is insufficient |
| Git | Not needed | Needed only for cloning/pulling; a downloaded source ZIP also works |
| Browser | A current Safari, Edge, Chrome, or Firefox with JavaScript enabled | Same |
| Python / Node.js / IDE | Not needed | Not needed to build/run the web app; Python 3 is used only by the optional packaging scripts |
| Display/network | Samsung display supporting **HTTPS IP Remote**, powered on, with IP Remote enabled; computer and display on the same trusted LAN | Same |

Choose the package/SDK for your computer's processor: Arm64 or Intel/AMD x64.
macOS requires **14 or newer**. Windows and Linux must support .NET 10;
Linux self-contained apps still need the distribution's native dependencies
(including its supported OpenSSL and ICU libraries). Check Microsoft's
[Windows](https://learn.microsoft.com/dotnet/core/install/windows),
[macOS](https://learn.microsoft.com/dotnet/core/install/macos), and
[Linux distribution requirements](https://learn.microsoft.com/dotnet/core/install/linux).
`xdg-open` is optional on Linux for automatically opening the browser; without it,
open the local URL yourself. No Samsung SDK, SmartThings account/API key, or
manually copied pairing token is required by this app. Not every Samsung TV or
monitor supports this protocol, and supported settings vary by model/firmware.

Allow the app (or hosting Terminal/editor for source builds) to access your local
network. Check VPN **Allow LAN connections** and firewall restrictions. Do not
publish the local server or TV control ports to the internet.

## Quick start: launch, use, stop

1. **Install:** download your OS/processor's app from [Releases](https://github.com/whipstein/SamsungController/releases/latest). On Mac, open the **DMG**, drag the app onto **Applications**, then eject the DMG. On Windows, run the **setup.exe** installer or extract the whole portable ZIP. On Linux, extract the whole archive.
2. **Launch:** open **SamsungController** from Applications (Mac) or the Start menu (installed Windows), `00 - Start SamsungController Server.exe` (portable Windows), or `SamsungController.App` (Linux). Allow Local Network access if asked. Your browser opens automatically.
3. **Pair once:** follow [First connection](#first-connection) below. **IP Remote must be enabled on the display.**
4. **Use:** open a Menu tab, edit a value, then click **Apply**. Use **Refresh state** after changes made elsewhere.
5. **Stop:** click **Stop** to cancel unsent commands. Click **Quit app** at the top to stop the background server. Closing the browser alone does not stop it. Source/foreground users press **Ctrl+C** in their terminal instead.

**Help** explains the current page. **README**, beside Help, opens this guide in a
new tab and works offline, including the tutorial and approval picture. External
vendor links still require internet. If the browser does not open, visit
[http://127.0.0.1:5050](http://127.0.0.1:5050). For source builds, use
[Install and run from source](#install-and-run-from-source).

## License

This revision uses [MIT + Commons Clause v1.0 with a Paid Services Exception](LICENSE),
not MIT alone. Personal and business use are allowed, including paid TV-calibration
services. Selling the tool itself, merely rebranding it for sale, or charging for
access to it as a hosted application is restricted. Publishing your modifications
is not required; preserve the entire combined license when redistributing.

This is **source-available**, not OSI-approved open-source licensing. See the
[licensing guide](docs/licensing.md) for examples, the value-added-product boundary,
and the exception for paid services. Previously published MIT versions, including
v0.3.0, retain their original permissions. The full `LICENSE` controls.

Bundled third-party components and illustrations retain their own terms; see
[third-party notices](docs/third-party-notices.md).

## Features

- Display profiles, guided certificate trust, explicit TV pairing, and saved-token reuse.
- Six peer sections: Picture, 2pt WB, 20pt WB, Color, Sound, and System. Picture, Sound, and System support saved whole-box drag-and-drop layouts.
- TV-queried current values with compact sliders, number fields, switches, and selections.
- Staged Apply or remembered Apply immediately, per-setting readback, progress, and Stop.
- A right-side slide-out Remote available from every page, light/dark mode, and a live, searchable, exportable communication log.
- Private local data, redacted reports, and Windows/macOS/Linux support.

Documented settings require **no manual verification**. Support still varies by display, firmware, input, and mode: failed or missing queries are shown, not replaced with defaults. Numeric controls use documented limits; no range-discovery query was found in the protocol references. See [ranges and availability](docs/direct-ip-interface.md#ranges-and-availability).

The old menu builder, verification pages, and macro editor are hidden; their files are preserved. The catalog does not cover every on-screen TV setting. The command-testing pages have been replaced with the communication log; they no longer expose app/channel/power experiments. No service-menu/factory reset or firmware-update commands are provided.

## Before starting

1. Put the computer and TV on the same trusted local network and turn the TV on.
2. Find the TV's IP address. A DHCP reservation helps keep it stable. The app does not scan for TVs.
3. **Enable IP Remote on the display. It is required.** Turn **Power On with Mobile** on too. See the [model-specific menu paths](#where-to-enable-ip-remote).
4. Use a current browser on the computer running SamsungController.

Direct IP uses HTTPS, normally port **1516**; some older displays use **1515**. It is separate from WebSocket ports 8001/8002 and uses its own token. Do not expose the controller or TV endpoint to the internet.

### Where to enable IP Remote

Use the **display's physical remote**, not the app. Open Settings / All Settings,
then follow the row that matches your display. In **Network → Expert Settings**,
turn **Power On with Mobile** on, then **IP Remote** on. Accept the TV's enable
warning if one appears.

| Display / menu generation | Path on the display |
| --- | --- |
| **S95F (2025)** | Settings → All Settings → **Connections** → Network → Expert Settings → IP Remote |
| **Odyssey OLED G9 G95SC**, including **LS49CG954SNXZA** | Menu → Settings → All Settings → **Connection** → Network → Expert Settings → IP Remote |
| Other recent compatible Samsung TVs, including supported OLED/QLED/The Frame models with the Connection menu | Settings → All Settings → **Connection** (or Connections) → Network → Expert Settings → IP Remote |
| Compatible **2021 and older** Samsung smart TVs | Settings → **General** → Network → Expert Settings → IP Remote |

Samsung documents the newer and older layouts in its [IP-control worksheet](https://image-us.samsung.com/SamsungUS/samsungbusiness/tv-ci-resources/Samsung-IP-Control.pdf#page=1).
The model-specific rows use Samsung's [S95F e-manual](https://downloadcenter.samsung.com/content/UM/202511/20251114042045001/BN81-27185A-670_EUG_ROPATSCF_NA_ENG-US_251022.0.pdf)
and [G95SC support/e-manual downloads](https://www.samsung.com/ca/support/model/LS49CG954SNXZA/).
Menu names can change with firmware and region; use your display's e-Manual
search for **IP Remote** if its layout differs. Do not confuse it with **Cable
Box IP Remote**, Remote Access, or screen mirroring. Some non-smart Odyssey
models and other Samsung displays do not offer this protocol. If IP Remote is
missing, check the exact model's manual; do not use service-menu codes or assume
that sharing a model-family name guarantees support.

## Install and run from source

Install [Git](https://git-scm.com/downloads) and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), not just the runtime. Check `dotnet --list-sdks`: it must include `10.0.xxx` (10.0.100 or later; see `global.json`). Choose Arm64 for Apple silicon or Windows/Linux on Arm; choose x64 for Intel/AMD. Microsoft provides SDK instructions for [Windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows), [macOS](https://learn.microsoft.com/en-us/dotnet/core/install/macos), and [Linux](https://learn.microsoft.com/en-us/dotnet/core/install/linux). If using GitHub's source ZIP, extract it and open a terminal in its root; skip the clone and `cd` commands. Internet access is needed for the initial NuGet restore. A separate .NET/ASP.NET runtime install, Python, Node.js, and Visual Studio are not required.

In macOS Terminal, Windows PowerShell, or a Linux terminal:

```sh
git clone https://github.com/whipstein/SamsungController.git
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

The [Releases page](https://github.com/whipstein/SamsungController/releases/latest) provides self-contained desktop apps. Follow the [beginner tutorial](docs/getting-started.md) from download through pairing, calibration, troubleshooting, and updates.

| Computer | Download suffix | Start the app |
| --- | --- | --- |
| Apple silicon Mac | `macos-arm64.dmg` | `SamsungController.app` |
| Intel Mac | `macos-x64.dmg` | `SamsungController.app` |
| Intel/AMD Windows | `windows-x64-setup.exe` or `windows-x64.zip` | Start-menu **SamsungController**, or portable `00 - Start SamsungController Server.exe` |
| Windows on Arm | `windows-arm64-setup.exe` or `windows-arm64.zip` | Start-menu **SamsungController**, or portable `00 - Start SamsungController Server.exe` |
| Intel/AMD Linux | `linux-x64.tar.gz` | `SamsungController.App` |
| Arm64 Linux | `linux-arm64.tar.gz` | `SamsungController.App` |

Windows installers and the clearly named portable launcher are available starting with **v1.0.6**. In v1.0.5 and earlier, the portable launcher is named `SamsungController.App.exe`.

1. Download the correct package. On Mac, open the DMG, drag **SamsungController.app** onto the **Applications** shortcut, and eject the DMG. Launch from Applications, not the mounted image. On Windows, run **setup.exe** or extract the portable ZIP completely. On Linux, extract completely. Keep portable-package files together.
2. Double-click the app. It starts the local server **in the background**, waits for it, and opens the browser. No terminal window is needed.
3. If the browser does not open, visit `http://127.0.0.1:5050`. Reopening the app reuses the running instance.
4. **Closing the browser leaves the server running.** Use **Quit app** at the very top, beside **Stop**, on any page; stop any active TV operation first. No login/startup service is installed.

Platform notes:

- **macOS:** Official release apps are Developer ID signed, notarized, and stapled. Allow Local Network access when prompted. CI artifacts are unsigned until the maintainer completes signing; use the published release for normal installation.
- **Windows installer:** Run the matching `windows-x64-setup.exe` or `windows-arm64-setup.exe`. It installs for your user in `%LOCALAPPDATA%\Programs\SamsungController`, adds a Start-menu entry, and optionally adds a desktop shortcut. Leave **Start SamsungController and open the webpage** checked to launch after installation. No administrator rights, .NET installation, service, or login-startup entry is needed. Quit the app before upgrading or uninstalling. Use Windows **Settings → Apps → Installed apps → SamsungController → Uninstall**; private profiles and settings are retained.
- **Windows portable:** Use **Extract All**, then double-click **00 - Start SamsungController Server.exe**. The `00` prefix places it first among files when sorting by name. It starts the background server and opens your default browser once ready. Do not double-click `SamsungController.Web.exe` for everyday use; that is the optional foreground server. Portable here means no installer, not that credentials travel with the folder: both distributions share your normal private data directory.
- **Windows security:** The installer and app are not Authenticode-signed; SmartScreen may warn. Verify the official source and checksum before allowing execution.
- **Linux:** Enable execution in file properties if needed. Run `./install-shortcut.sh` for an optional applications-menu entry. A graphical browser and the normal [.NET native Linux dependencies](https://learn.microsoft.com/dotnet/core/install/linux) are required. `xdg-open` opens the browser automatically; otherwise enter the local URL yourself.

Port 5050 is loopback-only. Stop any old foreground server occupying it before launching the app. Background logs and startup errors live under `desktop/` in the [private data folder](#private-data-command-line-and-updates). The launcher does not kill unrelated processes.

For an optional terminal shutdown on Windows, run `& '.\00 - Start SamsungController Server.exe' --stop` in PowerShell from the installed/extracted folder. On Linux, use `./SamsungController.App --stop`. On macOS the executable is inside `/Applications/SamsungController.app/Contents/Resources/server/`. Add `--no-browser` when deliberately running headless. Running `SamsungController.Web` directly preserves foreground-server mode; Ctrl+C stops it. The separate `samsungctl` CLI remains available.

Releases include `SHA256SUMS.txt`. Optional integrity checks: macOS `shasum -a 256 <archive>`, Linux `sha256sum <archive>`, PowerShell `Get-FileHash <archive> -Algorithm SHA256`.

## First connection

1. **On the TV:** turn on **Power On with Mobile** and **IP Remote** ([where to find them](#where-to-enable-ip-remote)).
2. **In the app:** click **Display → Add display**.
3. Enter the display name and IP address. Leave the port at **1516** unless your model requires another.
4. Click **Save display**. Leave the advanced certificate fields unchanged.
5. Click **Trust this display and pair**.
6. Check the address and certificate, tick the confirmation box, then click **Confirm trust and pair**.
7. **On the TV:** select **Allow** using the physical remote's Select/Enter button.
8. Wait for Menu to open and current values to load.

![Samsung IP Remote approval dialog with Allow selected](docs/images/samsung-ip-remote-allow.png)

Actual example from Samsung's [IP-control worksheet, page 2](https://image-us.samsung.com/SamsungUS/samsungbusiness/tv-ci-resources/Samsung-IP-Control.pdf#page=2).
Your model's appearance may differ. Choose **Allow**, not Deny or Close.

**Missed the prompt?** Wait for the request to finish, then click **Pair with TV**
and watch the TV. **Already paired?** Use **Connect and open Menu** next time.
For a saved display using Allow untrusted, **Trust this display and connect**
keeps its token. Trust is first-use confirmation, not independent proof of the
display's identity; see [certificate trust](#certificate-trust-and-pairing).

Connect preloads every Menu section and the catalog's other documented read/list methods for the current input/picture mode. Already-enabled 20-point and Custom color grids load all rows and restore their selectors. Connect does not automatically enable inactive modes. Use the explicit Refresh buttons described below to also read 20-point values while Off. Loading progress and Stop are available; Menu's settings-load details report unavailable values. Unsupported fields are never filled with defaults.

The app requests HTTPS keep-alive and retains one pooled TCP/TLS connection for the selected endpoint, including while idle. Requests reuse your saved token; they do not pair again. The TV can still close its connection, requiring a new TCP/TLS connection on the next request. “Connected” means the latest check succeeded, not continuous TV-state monitoring. The header status tooltip reports whether the last query reused TLS or the TV requested closure. Transport/authentication failures clear Connected; a failed Connect leaves the normal Connect button available.

If the connection stalls, use **Reset connection (keep pairing)** on Display. Stop any running request first. Reset discards the local HTTPS pool and makes only two ordinary state queries with your saved token—no pairing prompt, batch, setting/selector write, or full calibration scan. It clears cached readings and unsent drafts but preserves unfinished-operation originals. After success, refresh Menu values when ready. Normal Connect also opens a fresh connection. A timeout does not prove a bad token; if Reset still fails, export the recent Communication log before requesting another pairing. This action cannot restart a wedged TV-side IP service or restore a permission the TV actually revoked.

### Certificate trust and pairing

These are separate: the **certificate pin** checks that the app is talking to
the same display; the **pairing token** lets that display recognize the app.
Both are saved privately per HTTPS address/port. You do not copy a token into
the TV, install a certificate in the operating system, or disable system-wide
certificate checking.

The guided certificate check is **trust on first use**, not independent proof
of the TV's identity. Confirm the IP belongs to your display on a trusted LAN;
compare the SHA-256 fingerprint through an independent trusted source if one is
available. The confirmation is tied to the checked profile and fingerprint and
expires after five minutes. Canceling sends no pairing request and saves no pin.
Confirmation closes any older permissive connection, saves the exact pin, then
pairs/connects using strict pin checking on a fresh TLS connection.

Already paired with **Allow untrusted**? Choose **Trust this display and connect**
and confirm the certificate. It turns Allow untrusted Off and reuses your token;
no TV approval is requested unless you explicitly pair again after a rejection.
A saved pin mismatch stops requests. The review shows both old and new hashes
and a separate replacement warning/confirmation. Investigate a changed address,
reset, or untrusted network before approving it; there is no silent replacement.
Manual pins and timeouts remain under **Edit display → Certificate trust and
timeouts**. An entered pin takes precedence even if Allow untrusted is checked.

## Change settings

Use **Help** beside **Refresh state** for instructions tailored to Display, the current Menu section, or Communication log. Close the pop-up with **Close**, **Escape**, or a click outside. Usage explanations live there instead of above the controls; operational warnings and recovery actions remain on the page. Menu has one section heading, with no duplicate RGB-adjustment heading. **Discard** appears beside the section heading when edits are pending.

1. Open **Menu**. Connection-loaded values are ready without another scan when changing tabs. Use **Refresh state** beside Home, Back, and Exit menu in the global header after changing the HDMI signal (including 8-bit/10-bit), picture mode, or an incomplete load. It rereads all settings and available calibration rows using the saved connection, discards unsent edits, and reevaluates hidden/disabled controls. No reconnect or pairing is needed. For a quicker read of just the current tab, choose **Refresh section** beside Apply in the pinned toolbar. **Reload all rows** is beside it, enabled only in 20pt WB and Color. Opening tabs does not restart a stopped load.
2. Choose **Picture**, **2pt WB**, **20pt WB**, **Color**, **Sound**, or **System** from the single row of tabs. Picture contains the general picture controls; white balance and color have their own sections. 2-point gains and offsets are grouped in two columns.
3. Adjust a slider, number, switch, or selection. With default **On Apply** (wait for Apply), the TV is unchanged; pending targets are distinct from queried current values.
4. Select **Apply (N)** at the top right. In **20-point white balance**, each percentage block also has an **Apply** button in On Apply mode: it sends only that block's pending RGB settings, leaving all other edits pending. Fresh values/context are checked, settings are sent sequentially, and each result is queried. A mismatch stops later settings.
5. Choose **On Apply** or **Immediately** using the radio buttons at the very top of Menu. The compact toolbar keeps these, Query first, Refresh section/Reload all rows, Apply/Stop, and all six section tabs pinned below the connection header while scrolling. There is no separate Expert tab; its controls are directly under **Picture**. Hover over the abbreviated WB tabs for their full names. Each tab remembers its last scroll position; returning to Menu also restores the last selected section. First visits start at the section heading. This browser-tab-only memory survives page reloads and never changes TV settings or triggers a refresh. Apply or discard pending edits before enabling immediate mode. Slider drags, typed values, and **+ / −** remain usable while earlier adjustments run. Unsent edits to a setting combine into its latest target; an in-flight command is not changed. For example, five − clicks while the first write is in flight send that first −1, then the combined remaining −4. Pending RGB changes within a percentage block share one selection, using the latest target for each channel. Releasing a slider or committing a number adds an edit; dragging and typing alone send nothing. The number shows your latest edit or queued target without being overwritten by older replies; **TV:** shows the last queried value. This preference survives restart.
6. Use **Stop** during an operation. Delivered commands are not undone. Last update retains originals and per-setting outcomes. For an uncertain write, inspect/refresh the TV and close its review explicitly before more writes.

Stop, a failed adjustment, or a changed TV context discards unsent queued adjustments. Queues are memory-only and never resume after restart; earlier confirmed changes remain on the TV. Other TV actions are blocked while the queue runs. There is at most one unsent target per control, plus any in-flight write; the queue has a safety limit of 256 outstanding targets. Clicks beyond min/max, duplicate targets, and changes canceled out before sending do not add writes. Invalid typed values show an error without entering the queue. RGB grouping shares the percentage/color selector; each channel still uses its own protocol command and readback, not an atomic three-channel write.

**Query first** (Query before changes) is also in the pinned toolbar and defaults to On. Turn it Off for faster adjustments using the last successfully read values, including untouched 2-point/RGB channels; the toolbar shows **Cached** as a reminder. No pre-change values or context are reread; a moved selector is still confirmed, and post-change readback/context checks remain enabled. Missing values never become defaults. Off assumes no other changes: refresh after using a remote, changing the signal, or making outside adjustments. Whether selectors are isolated between controllers is unverified. Saved recovery originals are labeled as last known values when pre-change queries were Off. Both preferences survive restart; neither changes the TV when toggled.

Each 20pt percentage block and Custom color block has **RGB together − / +** buttons to adjust that block's Red, Green, and Blue by one while preserving their differences. They follow **On Apply / Immediately**, use the latest pending/queued values, and combine unsent clicks. Both buttons require all three channels to be available with valid numbers. If any channel reaches a limit, that direction is disabled for the group instead of clamping channels separately. Other blocks are unchanged.

Use **Reset all** in the pinned toolbar on **20pt WB** or **Color** to reset the entire section's RGB values: **0** at every white-balance percentage (60 values), or **50** for every Custom color channel (18 values). These are nominal targets, not display-specific factory calibration queried from the TV. A confirmation shows the scope: **Stage reset** in On Apply mode changes only pending edits; **Reset now** in Immediately mode sends the section's reset with normal readback checks. Other settings and pending edits are preserved, and the WB switch/color-space mode is not changed. All rows must be loaded and enabled first. Stop or a failure during an immediate reset discards its remaining unsent targets without undoing delivered changes. This does not invoke a full picture/factory reset.

Number boxes preserve partial edits such as `-` while you type a negative value.
Complete the whole number before leaving the field to commit it. Invalid or
out-of-range text stays editable and sends nothing to the TV.

20-point WB and Custom color updates leave the last percentage/color selected. Later edits reuse it, sending a selector command only when a different selection is needed. With pre-change queries On, all three RGB originals are freshly read; with Off they come from the queried cache. All three channels and context are checked at the end. If a group stops before that check, **Last update → Saved RGB originals** retains the baseline for manual recovery; nothing is automatically retried or undone. See [RGB update details](docs/direct-ip-interface.md#20-point-white-balance).

Apply input/picture-mode and calibration-mode changes separately from other pending settings. The app does not assume a menu topology or external-signal setting bank.

In **Picture**, **Sound**, or **System**, click and drag a box's heading or background and drop it at the highlighted edge to rearrange the grid—there are no separate move controls. Related controls move together: Gamma with its adjustment sliders, and Picture Clarity with its motion controls. Sliders, switches, selections, buttons, and text inputs still work normally. For keyboard ordering, Tab to a box and use **Alt + arrow keys** (Option on macOS). Each section's layout saves independently across restarts, including the positions of temporarily hidden settings. Your existing Picture layout is preserved. **Reset layout** restores only the current section's default order. Moving boxes never sends TV commands, changes values, or clears pending edits.

For **2-point white balance**, editing one gain/offset sends all six channels in one request, preserving the other five from a fresh TV query. Nothing is zeroed or filled with defaults. Incomplete readings prevent sending; readback checks the target and unchanged peers. This avoids single-channel requests that some TVs reject or only partly apply.

For **20-point white balance**, all percentages from 5% through 100% appear together, each with RGB sliders, −/+, and number inputs. **Refresh state**, or **Reload all rows / Refresh section** in the pinned toolbar on the 20pt WB tab, reads these even when WB starts Off: it temporarily enables WB, reads each interval, restores the original interval, then restores and confirms Off. Read values remain visible but disabled until you turn WB On and Apply. Initial Connect and tab visits do not enable it automatically. For **Custom color**, select/apply Custom: Red, Green, Blue, Yellow, Cyan, and Magenta each have a fixed RGB row. There is no interval/color dropdown to manage. Loading visits each selector and restores the selection that was active before loading; **no RGB values are changed**. Unlike loading, Apply leaves the last edited row selected. Startup/refresh scans share expensive context checks across at most four rows, verify the selector on every row, and publish readings only after their group's context check. They always query fresh values, independent of the pre-change preference. Progress, elapsed time on completion, and Stop remain available. See the [detailed walkthrough](docs/direct-ip-interface.md).

If a temporary WB read is stopped or fails, it may leave WB On. **Stop sends no further commands, including restoration.** Menu shows the saved original display, input, picture mode, and interval with **Restore 20-point WB to Off**, plus an option to confirm manual restoration. Keep the original physical signal active when restoring; nothing resumes after restart.

Direct settings do not navigate TV menus, so there is no return-to-video script or “stay on last adjusted item” option. Header Home, Back, and Exit menu buttons are explicit single keys, not a guaranteed video anchor.

### Optional Compact layout

Select **Standard / Compact** at the top beside the light/dark button. **Standard is the default** and keeps the original slider cards. Compact reduces navigation, cards, dialogs, and spacing throughout the app. The choice is remembered in this browser only; it does not change the TV, pending edits, or theme.

In Compact, **20pt WB** is a table of all 20 percentages: Red/Green/Blue number fields with −/+ buttons, **RGB ±** for grouped changes, and a per-row **✓** Apply button in On Apply mode. **Color** uses the same arrangement. Hover over a channel for its last-read TV value, limits, and availability. Number entry, negative values, queued adjustments, Stop, and readback checks work exactly as in Standard. Switch back for full slider tracks.

Aim for a window around **480–640 pixels wide**, with at least **800 pixels of browser page height at 100% zoom**, and leave optional details closed to see all 20 rows together. Smaller heights, larger text/zoom, or expanded warnings remain scrollable; no controls are clipped. Menu sections remember their scroll positions separately for each layout.

## Save, recall, and delete settings

Open **Menu → Saved states**, just below the pinned toolbar (v1.0.7 onward).

1. Connect and let the readings finish. If settings changed outside the app, use **Refresh state** first. Apply or discard pending edits.
2. Enter a **State name** and choose **Save current settings**. This saves last-read TV values locally without sending any commands. Use different names to keep several calibrations.
3. To recall, choose a **Saved state** from the list and review its values and omissions. Select the matching display, HDMI input, picture mode and signal first, then refresh. Recall does not change input or picture-mode/calibration-bank selectors.
4. Choose **Recall settings…**, confirm the physical HDMI signal/bit depth and display context, then **Apply saved state now**. This explicit action sends changes regardless of the On Apply / Immediately preference. It queries current values and uses the normal readback-checked updates. Unavailable settings are skipped and listed, not filled with defaults.
5. To remove a state, select it, choose **Delete state**, then confirm **Delete saved state permanently**. Cancel leaves it intact. Deletion never changes the TV.

States include usable reported Picture, Sound, System, 2pt WB, and loaded 20pt WB/Custom color rows. Missing rows are listed as omissions. Before saving, **Refresh state** can load 20pt WB while Off; Custom color must be enabled and its rows loaded to include them. Raw interval/color selector positions and reset actions are not saved. Recall can temporarily enable a calibration mode to restore its RGB rows, then returns the mode to its saved value. Controls unavailable in the resulting context remain unchanged and are listed in the recall result.

**Stop** cancels remaining commands, not changes already sent. If recall is interrupted, check the display, including the requested calibration modes shown in Saved states. Close any interrupted **Last update** review, then close the recall review. Nothing resumes automatically at startup.

States are private JSON files in `ip-remote/saved-states` under the [data folder](#private-data-command-line-and-updates). They survive app updates, contain no pairing token, and are not included in the repository or installers. Back up this folder if you want a separate copy; deleting a state is permanent. Names do not become file paths. Duplicate names in the same context are rejected to avoid silently replacing a calibration.

## Slide-out remote

Select the small **double-chevron** button on the right edge of any page (tooltip: **Open the TV remote without leaving this page**). The remote fills the window height, with a fixed close header and a scrolling button area, without navigating away; **Menu** remains a normal page. Use the directional pad, Home/Menu/Back/Exit, volume buttons, or the **More keys** selector. Close with **Close ×**, **Escape**, or a click outside the panel. Opening/closing sends no TV commands. **Remote key presses retain loaded settings and pending edits**—they do not refresh or invalidate them. Use **Refresh state** in the global header or Menu's section/row refresh buttons when you want to reread the TV; displayed values may otherwise be stale. Apply still checks a fresh baseline before writing. Remote keys are disabled while disconnected, while another request is running, or when an interrupted operation requires review. **Stop** is available inside the panel too.

## Diagnostics and troubleshooting

- **All-settings load incomplete / changed signal:** let the external signal settle, then select **Refresh state**. This starts a fresh all-settings read, even if the TV reports the same HDMI port and picture-mode name. Keep the signal unchanged while loading. Stop cancels it; tab visits never resume it automatically.
- **Connection fails:** try **Reset connection (keep pairing)**, then check TV power, IP Remote, address/port, certificate policy, LAN permissions, VPN/firewall restrictions, and the actual error. No automatic re-pairing occurs. Even after an authorization rejection, Reset can explicitly recheck the saved token on fresh TLS; only successful state replies clear the rejection. If the TV genuinely rejects that token again, renewed approval may still be needed.
- **macOS receives no response:** for the installed desktop app, check System Settings → Privacy & Security → Local Network for **SamsungController**. For source execution, check its hosting Terminal/editor instead. A correct IP and a working browser do not prove the server process has permission. Quit and reopen the affected app/server after changing permission. If the v1.0.0 app never appears in the list, install v1.0.1 or newer: it corrects the native app lifetime and duplicate .NET executable UUIDs used for permission attribution. Install one copy in Applications, launch it there, and attempt Connect/Pair to request access. You must approve the macOS prompt yourself; signing/notarization does not grant permission. This is separate from the TV's pairing approval.
- **Approval accepted but no connection:** verify the token was received/saved; inspect the pairing error. Connect reuses saved tokens.
- **Gray setting:** a prerequisite is unmet—for example Judder Reduction requires Picture Clarity / Auto Motion Plus set to Custom. Apply that prerequisite to refresh its dependent controls. Rejected/absent controls without an unmet prerequisite are hidden for the current context; Refresh checks them again. Defaults are never used as current values.
- **Out-of-range value:** correct the inline error; no command is sent for invalid local input. TV rejections stop the operation without retrying, except for the readback-confirmed 2-point case below.
- **2-point WB changed but TV reported `−32002`:** independent readback must confirm the target, all other five channels, and unchanged context before the row is marked **Applied with TV warning**. No retry is sent; the original error stays in Communication log. Incomplete or mismatched reads still stop. See [2-point white balance](docs/direct-ip-interface.md#2-point-white-balance).
- **Rejected unchanged:** the TV did not apply the target; its actual value remains in the message. This does not require recovery or disconnect you. Correct the pending value, or select **Update behavior → Discard pending changes** before trying another setting.
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

Packaged binaries are `samsungctl` (macOS/Linux) and `samsungctl.exe` (Windows); run with `--help` in a terminal. The Mac binary is inside `/Applications/SamsungController.app/Contents/Resources/server/`. The [v0 CLI guide](docs/v0-user-guide.md#use-the-command-line-interface) covers retained WebSocket/key/macro commands, not the direct-IP web settings API.

To update source: stop the server, preserve edits, `git pull --ff-only`, then restore/build/run again. To update packages: stop the old copy, run the new Windows installer, replace the Mac app, or extract a new portable package into a separate folder, then launch it. Back up private data before moving between major versions. Restart the server and refresh the browser. The source default is now `main`; see the [release-maintainer guide](docs/releasing.md) for packaging, signing, notarization, and publication.

For contributors: [development roadmap](docs/development-roadmap.md), [direct-interface design](docs/direct-ip-interface.md), and [preserved v0 menu-file format](docs/menu-definition-file-format.md).
