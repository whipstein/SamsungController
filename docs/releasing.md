# Building and releasing desktop apps

The release tooling produces self-contained native launchers and servers for six OS/processor combinations. User data is never included. The optional CLI is retained. Update `Directory.Build.props`, release notes, and user guides before building.

## Build and test

```sh
dotnet test SamsungController.sln --disable-build-servers -m:1
python3 packaging/build.py --rid osx-arm64
python3 packaging/smoke-test.py --manifest artifacts/dist/build-osx-arm64.json
```

Use `osx-x64`, `win-x64`, `win-arm64`, `linux-x64`, or `linux-arm64` for other packages. Smoke tests require a matching OS/architecture. They start only an isolated loopback server with temporary configuration, check static assets, duplicate launch, unauthorized shutdown, CLI, quit, and restart; they never contact a TV or open the browser.

The **Release packages** GitHub workflow builds and smoke-tests on native runners. A tag run creates an **unpublished draft**. Its macOS artifacts are not yet signed/notarized; CI never publishes them automatically. Private Apple credentials remain on the signing Mac, not in the repository or GitHub artifacts.

## Mac signing and notarization

Install the Developer ID Application certificate/private key in your Keychain and configure a notarization profile locally. Do not paste passwords into chat, scripts, logs, or command arguments:

```sh
xcrun notarytool store-credentials SamsungController
```

The interactive prompts request your developer Apple ID, team ID, and app-specific password, or the corresponding API-key setup. Use an existing profile if one is already configured. A normal Apple account password is not an app-specific password.

For each Mac architecture:

```sh
python3 packaging/macos/sign-notarize.py \
  --manifest artifacts/dist/build-osx-arm64.json \
  --identity 'Developer ID Application: Your Name (TEAMID)' \
  --keychain-profile SamsungController
```

The script signs every nested Mach-O, then the app bundle, with hardened runtime and the .NET JIT entitlement. It verifies the signature, submits through the existing Keychain profile, requires an **Accepted** result, staples/validates the ticket, performs Gatekeeper assessment, and recreates the final ZIP. Omitting the profile performs signing only; **that output is not ready for publication**. Credentials/private keys are never exported.

Run the smoke test again on the signed/stapled package and, where available, test opening a quarantined download in Finder. Keep the notarization submission IDs for audit/troubleshooting. Do not remove quarantine as a substitute for notarization.

References: [Microsoft's macOS deployment requirements](https://learn.microsoft.com/dotnet/core/deploying/macos), [Apple's notarization workflow](https://developer.apple.com/documentation/security/customizing-the-notarization-workflow), and [GitHub native runner labels](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).

### Mac local-network identity and icons

The native AppKit entry point must remain the bundle's running process. It launches the managed desktop launcher with `--wait-for-exit`, which releases its startup lock before waiting for the server. Reopening the app opens the existing UI; stopping the server ends the launcher and native host. Do not replace the entry point with `exec()` or detach the bundled server into another session. Source/CLI launches retain their normal behavior.

`build.py` gives each of our three .NET apphosts a distinct Mach-O `LC_UUID` before signing. The SDK's shared apphost UUID must not be reused across these executables. Unit tests cover this transformation; Mac package smoke tests verify distinct IDs, the usage description, icon, host lifetime, duplicate startup, and clean exit.

This is a hardened-runtime app, **not an App Sandbox app**. `com.apple.security.network.client`/`server` are sandbox entitlements, not Local Network consent. Keep `NSLocalNetworkUsageDescription` in the main bundle and a stable Developer ID signing identity. Do not enable App Sandbox or add unrelated entitlements as a workaround. See [Apple TN3179](https://developer.apple.com/documentation/technotes/tn3179-understanding-local-network-privacy).

Local-network permission is **not covered by loopback smoke tests**. Test a Finder-launched installation on a real LAN, preferably in a fresh macOS user account, and verify that SamsungController is named in the consent prompt/System Settings. Do not reset system privacy databases or bypass consent. Multiple installed copies can confuse permission testing; close old builds and use one installation in Applications.

The approved icon source and generated platform assets are in `packaging/icons`. Regenerate with `python3 packaging/icons/generate.py` on macOS; CI uses the committed assets.

## Publish

1. Verify the tested commit is on main and tag it `v<version>`.
2. Wait for all six package smoke tests and normal CI to succeed.
3. Replace both Mac draft-release ZIP assets with the signed, notarized, stapled ZIPs. Never publish the unsigned CI Mac archives.
4. Generate `SHA256SUMS.txt` from the **final** six archives and upload it.
5. Verify release notes, signatures, notarization, architecture labels, and checksums; publish the draft as the latest stable release.

The background server listens only on `127.0.0.1`. Launcher `--stop` uses a random per-instance token in an owner-private file; it never terminates a process based only on a PID. Foreground servers and unrelated listeners are not stopped. Source execution via `SamsungController.Web` stays foreground. The launcher does not install a boot/login service.
