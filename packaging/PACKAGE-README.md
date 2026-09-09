# SamsungController

See `VERSION.txt` for this package's version.

This package includes the desktop launcher, local web server, optional CLI, and .NET runtime. No clone, SDK, or terminal window is required.

Read [README.md](README.md) or the [step-by-step tutorial](docs/getting-started.md) for first pairing, adjustments, troubleshooting, and updates.

## Requirements

- A supported Samsung display with **HTTPS IP Remote** enabled, powered on and on the same trusted LAN as this computer. Not every Samsung monitor/TV supports it. The app requires no Samsung SDK, SmartThings API key, or manually copied token.
- A current browser with JavaScript enabled. Allow Local Network access for the app and LAN access through your VPN/firewall. Never expose the server to the internet.
- The correct x64/Arm64 package for your OS/processor. macOS 14 or newer; Windows/Linux must meet [.NET 10 OS requirements](https://learn.microsoft.com/dotnet/core/install/). Linux needs its [native OS libraries](https://learn.microsoft.com/dotnet/core/install/linux), even though the .NET runtime is bundled. `xdg-open` is optional for opening the browser automatically.
- **No .NET SDK/runtime installation, Git, Python, Node.js, or IDE is needed for this package.** If you downloaded source code instead, you need the **.NET 10 SDK**, not just the runtime; follow [source-build directions](README.md#install-and-run-from-source).

## Start the app

- **macOS:** Extract the ZIP, then drag **SamsungController.app** to Applications. Double-click it. Official release Mac apps are Developer ID signed, notarized, and stapled; temporary CI artifacts are not. Allow Local Network access when prompted.
- **Windows:** Extract the entire ZIP and double-click **SamsungController.App.exe**. Keep all extracted files together. Windows binaries are not Authenticode-signed; SmartScreen may warn. Only allow an archive downloaded from the official release.
- **Linux:** Extract the entire tar.gz and run **SamsungController.App**. If needed, allow executing it in file properties. Run `./install-shortcut.sh` once for an optional applications-menu shortcut; keep the extracted folder at that location.

Choose arm64 for Apple silicon/Arm64 computers or x64 for Intel/AMD. Linux requires the normal .NET 10 native OS dependencies and a graphical browser; `xdg-open` opens the browser automatically.

The app starts its server at **http://127.0.0.1:5050** and opens your browser. It runs in the background with no terminal window. Opening the app again reuses the same running instance. **Closing the browser does not stop the server.** Use **Quit app** at the very top, beside **Stop**, on any page when done; stop any active TV operation first. No login service or automatic startup is installed.

If the browser does not open, enter the URL manually. Background startup errors are recorded in your private configuration directory under `desktop/last-launch-error.txt`; server output is in `desktop/server.log`. Port 5050 must not be occupied by an old foreground server or another application.

## Pair and use

1. Turn on the TV, put both devices on the same trusted LAN, and enable the TV's **IP Remote** setting.
2. Add its address on Display. Direct HTTPS IP Remote normally uses **1516**, not WebSocket ports 8001/8002.
3. **Save display**, leaving advanced certificate settings unchanged. Select **Trust this display and pair**: review the address and SHA-256 fingerprint, check the confirmation box, then **Confirm trust and pair**. Accept the separate approval dialog **on the TV**. This pins the certificate, turns Allow untrusted Off, saves the token, reads settings, and opens Menu. Later use **Connect and open Menu**, not Pair again.
4. Choose a Menu tab and edit queried values. **On Apply** stages targets; **Immediately** sends committed adjustments.
5. Use **Refresh state** after outside changes. Help beside it explains the current page. Stop cancels remaining commands, not changes already delivered.
6. When finished, **Quit app** at the very top beside Stop. Closing the browser alone does not stop the server. Reopen the desktop app to return to it.

Already paired using Allow untrusted? **Trust this display and connect** pins the certificate and reuses your saved token without new TV approval. Certificate inspection sends no HTTP request, token, or TV command. It is **trust on first use**: check the address and trusted LAN, and independently compare the fingerprint if available. A changed certificate is never trusted automatically; it requires explicit replacement approval. Canceling the review saves nothing. A failed pairing leaves the pin saved so you can retry with Pair with TV. See [trust details](README.md#certificate-trust-and-pairing).

## Stop or use the command line

On Windows, use PowerShell in the extracted folder:

```powershell
.\SamsungController.App.exe --stop
.\samsungctl.exe help
.\SamsungController.Web.exe
```

On Linux:

```sh
./SamsungController.App --stop
./samsungctl help
./SamsungController.Web
```

On macOS (after copying to Applications):

```sh
/Applications/SamsungController.app/Contents/Resources/server/SamsungController.App --stop
/Applications/SamsungController.app/Contents/Resources/server/samsungctl help
cd /Applications/SamsungController.app/Contents/Resources/server
./SamsungController.Web
```

Running **SamsungController.Web** directly is the optional foreground-server mode; keep that terminal open and use Ctrl+C to stop it. The separate **samsungctl** CLI retains its menu-key/WebSocket workflows and is not the new direct-IP GUI.

## Update and privacy

Quit the app before replacing it. Install the new archive/app; do not overwrite a running installation. Profiles, credentials, preferences, and logs remain in the normal per-user configuration directory outside the package. Back up that directory before major updates.

The server binds to this computer only. Never expose it or the TV's control service to the internet. Do not distribute personal tokens, profiles, or raw logs. Use the Communication log export's redaction controls.

This version uses **MIT + Commons Clause v1.0 with a Paid Services Exception**, not MIT alone. Business use and paid calibration services are allowed; selling the tool or paid access to it is restricted. Preserve the full included [LICENSE](LICENSE) when redistributing. See [licensing details](docs/licensing.md). Earlier MIT releases keep their original terms.
