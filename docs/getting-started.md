# SamsungController: installation to everyday use

This guide describes the **v1 direct-IP application**, now the default on main. For historical menu-key workflows, see [the v0 guide](v0-user-guide.md).

## Requirements and quick start

For a **downloaded app**, the .NET runtime is included: no .NET SDK/runtime install,
Git, Python, Node.js, Samsung SDK, or IDE is required. For **source builds**, install
the **.NET 10 SDK** (not the runtime alone); see [source setup](../README.md#install-and-run-from-source).
Both need a current browser with JavaScript and a Samsung display supporting HTTPS
IP Remote, on the same trusted local network. Allow Local Network/VPN LAN access.
macOS needs 14 or newer; see the [requirements table](../README.md#requirements)
for Windows/Linux dependencies and processor selection.

**Everyday workflow:** launch the app → Display → select/add a display → trust
and pair once → Connect and open Menu → edit values → Apply → Stop if needed →
Quit app. Closing the browser only hides the interface. A source server instead
runs in its terminal and stops with Ctrl+C.

The guided **Trust this display** buttons are in the current source; the published
v1.0.2 package predates them. Older packages still use the advanced manual
certificate fields until a newer package is published.

## 1. Choose your download

Open the [official Releases page](https://github.com/whipstein/SamsungController/releases/latest).

| Computer | Package suffix |
| --- | --- |
| Apple silicon Mac | macos-arm64.zip |
| Intel Mac | macos-x64.zip |
| Intel/AMD Windows | windows-x64.zip |
| Windows on Arm | windows-arm64.zip |
| Intel/AMD Linux | linux-x64.tar.gz |
| Arm64 Linux | linux-arm64.tar.gz |

The runtime is included. You do not need Git or a .NET installation. Linux still requires its normal native OS libraries: see [Microsoft's .NET Linux requirements](https://learn.microsoft.com/dotnet/core/install/linux).

Optional integrity checks against the release's SHA256SUMS.txt:

- macOS: `shasum -a 256 <archive>`
- Windows PowerShell: `Get-FileHash <archive> -Algorithm SHA256`
- Linux: `sha256sum <archive>`

## 2. Install and launch

### macOS

1. Double-click the downloaded ZIP to extract it.
2. Drag **SamsungController.app** into Applications. Do not separate its bundle contents.
3. Double-click the app. It starts the local server and opens the default browser.
4. Allow **Local Network** access when prompted. Official release Mac apps are Developer ID signed, notarized, and stapled; unsigned CI artifacts are for developers, not normal installation.
5. If it cannot contact your TV, check **System Settings → Privacy & Security → Local Network** for SamsungController. A source-launched server instead needs permission for its hosting Terminal/editor/ChatGPT app.

If SamsungController never appears in that list with v1.0.0, quit the old app and update to **v1.0.1 or later**. Install a single copy in Applications and open it there, then attempt Connect/Pair. The patch corrects permission attribution to the native app and its background server. You still need to click **Allow** in the macOS prompt; Developer ID signing does not grant Local Network permission. Your saved pairing credentials are retained. A separate TV approval may still be required for a display that has not yet paired.

### Windows

1. Right-click the ZIP and choose **Extract All**.
2. Put the extracted SamsungController folder in a stable location.
3. Double-click **SamsungController.App.exe**. Do not run inside ZIP preview or move the executable away from its companion files.
4. Windows binaries are unsigned. If SmartScreen warns, verify the download came from the official repository before allowing it.

### Linux

1. Extract the whole tar.gz into a stable folder.
2. Run **SamsungController.App**; if your file manager asks, allow execution in file properties.
3. For an applications-menu entry, run `./install-shortcut.sh` once from that folder. It creates only a per-user shortcut, with no root access or login service.
4. A graphical browser and `xdg-open` are used to open the interface. You can also type the local URL manually.

For all platforms, the interface is **http://127.0.0.1:5050**. The server runs in the background, without a terminal window. Reopening the app reuses the existing instance.

## 3. Understand start, close, and quit

- Closing a browser tab does **not** stop the server.
- Open SamsungController again to return to the interface.
- To stop the server, use **Quit app** at the very top, beside **Stop**, on any page.
- If a TV operation is running, press **Stop** and review any unfinished-operation notice before quitting.
- The app does not start automatically at login. Your saved data is retained when you quit.
- An old foreground server may already occupy port 5050. Stop it in its original terminal before starting the packaged app; the launcher will not kill unrelated processes.

## 4. Prepare the TV

1. Turn the TV on and put it on the same trusted LAN as your computer.
2. Find its IP address in the network settings. A DHCP reservation avoids address changes.
3. Enable **IP Remote** in the TV's network expert settings, if supported.
4. Use the direct HTTPS port, normally **1516** (some older displays use 1515). This is not the older WebSocket interface on ports 8001/8002.
5. Do not expose either the TV service or SamsungController to the internet.

## 5. Save and pair a display

1. On **Display**, select **Add display**.
2. Enter a useful name/model, TV address, and HTTPS IP Remote port.
3. Leave **Certificate trust and timeouts** unchanged for guided setup; manual pin entry is still available there for advanced use.
4. Save the profile. Saving alone sends no TV commands.
5. Select **Trust this display and pair**. The app retrieves the certificate using a separate TLS-only connection: no HTTP request, saved token, or TV command is sent. Review the displayed address and SHA-256 fingerprint, check the confirmation box, and select **Confirm trust and pair**. The review expires after five minutes; editing/selecting a profile or Cancel clears it.
6. Accept the separate approval dialog **on the TV**. The app pins the certificate and turns Allow untrusted Off **before** pairing. It saves the returned token, connects, reads settings, and opens Menu. If approval times out, the pin remains saved: use **Pair with TV** to retry and watch the TV screen.
7. On later visits, choose the saved display and **Connect and open Menu**. Do not pair again simply to reconnect.

If you used the v1 preview, its profiles/tokens are reused. A v0 WebSocket token is not a v1 HTTPS token.

**Already paired with Allow untrusted?** Select **Trust this display and connect**,
review and confirm. The app keeps the existing token and does not ask the TV to
approve again. On future visits simply Connect.

**What am I trusting?** This is trust on first use, not independently verified
identity. Check that the IP belongs to your display on a trusted LAN; compare its
fingerprint through another trusted source if available. The pin identifies the
display to the app; the token identifies the app to the display. Neither is an
OS permission, and no certificate/token needs to be entered on the TV manually.
Future certificate mismatches stop requests. Review shows the old/new fingerprints
with a distinct replacement confirmation. Investigate before approving; the app
never replaces a pin silently. Cancel leaves the old trust intact. Fingerprints
are visible during this review; diagnostic exports redact them by default.

## 6. Let the initial read finish

The connection reads all documented setting groups for the current signal. Already-enabled 20pt WB and Custom color modes load their rows. Unsupported or inactive values stay unreported, not zero or assumed defaults.

**Settings load** details and **Communication log** explain missing values. Stop cancels the remaining work. Opening a tab never silently resumes an incomplete scan; use an explicit refresh.

## 7. Navigate and adjust

Choose **Picture**, **2pt WB**, **20pt WB**, **Color**, **Sound**, or **System**.

- The tabs and Apply controls stay pinned while scrolling.
- Each tab remembers its position, including after leaving Menu or reloading in the same browser tab.
- **Help**, beside **Refresh state**, explains the current page/section.
- Picture, Sound, and System boxes can be dragged by their background/heading. Linked controls move together. Reset layout affects only the current section.
- The right-edge double chevron opens the remote without leaving the page.

### Safest first adjustment

1. Leave **On Apply** selected and **Query first** checked.
2. Change a slider or type a number. Finish the number and leave the field to commit it locally.
3. Review the pending target. **TV:** shows the last queried value.
4. Select **Apply**. The app sends the change, reads it back, and checks the result.
5. Use **Discard** beside the section heading to discard unsent targets without changing the TV.

**Immediately** sends committed changes without a separate Apply. You can keep editing while earlier changes run; unsent targets combine rather than building an endless queue. Invalid or out-of-range entries send nothing.

### Calibration rows

- 2pt WB groups RGB gains and offsets into two columns.
- 20pt WB presents all percentage blocks together. Enable its switch and Apply before editing. On Apply mode includes an Apply button for each percentage.
- Color presents fixed RGB controls for every color. Choose Custom in Color space and Apply to enable them.
- Apply leaves the last used percentage/color selected. Loading rows restores the original selector.
- Do not apply an input/picture-mode change together with dependent calibration changes.

## 8. Refresh deliberately

- **Refresh state** reads all settings for the current signal and discards unsent targets.
- **Refresh section** rereads only the current section.
- **Reload all rows** is enabled only in 20pt WB and Color.
- Explicit 20pt WB refresh can temporarily enable WB to read values while Off, then restores its original interval and Off. No RGB value is changed.
- Remote keys keep cached readings unchanged. Refresh after using another controller or changing the HDMI signal/picture mode.
- Keep the physical signal stable during reads and writes.

Turning **Query first** Off is faster but assumes the last queried values are still correct and no other controller changed the TV. Post-change checks remain enabled.

## 9. Stop, recover, and diagnose

**Stop** cancels unsent work; delivered changes are not undone. If a write or temporary WB read is interrupted, follow the visible recovery controls. Do not continue on an uncertain baseline.

For connection stalls, try **Reset connection (keep pairing)** on Display. It refreshes the local HTTPS connection without requesting new TV approval. It cannot restart a failed TV-side IP service.

Check TV power/address/port, IP Remote, certificate trust, Local Network permission, VPN LAN-access settings, and firewall rules. In Proton VPN, check **Allow LAN connections**. Restart the hosting app/server after permission changes.

**Communication log** shows requests and replies. Filter/search, expand TX/RX, and export relevant records. Tokens are always redacted; leave device-identifier and SHA redaction enabled before sharing. Review free-text labels.

If the desktop app fails before the browser opens, inspect `desktop/last-launch-error.txt` and `desktop/server.log` within the private configuration folder shown below.

## 10. Update without losing data

1. Stop TV operations, then **Quit app**. Stop any old foreground server with Ctrl+C.
2. Back up the private configuration folder.
3. Download the new official package for your OS/processor.
4. Replace the macOS app, or extract Windows/Linux into a new stable folder. Do not overwrite a running installation.
5. Reopen the app and connect to the saved display.
6. If using the Linux menu shortcut, rerun the shortcut installer from the new folder.
7. Hard-refresh the browser if old styling remains cached.

Private data normally lives in:

- macOS: `~/Library/Application Support/SamsungController/`
- Windows: `%APPDATA%\SamsungController\`
- Linux: `${XDG_CONFIG_HOME:-~/.config}/SamsungController/`

Direct-IP profiles, tokens, preferences, and logs are under `ip-remote/`. Background-server metadata/logs are under `desktop/`. Neither folder belongs in a distributable package.

For source builds and the optional CLI, see the [README](../README.md). The CLI retains its existing WebSocket/menu-key workflows; it is separate from the v1 direct-IP web interface.
