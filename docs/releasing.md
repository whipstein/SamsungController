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

## Publish

1. Verify the tested commit is on main and tag it `v<version>`.
2. Wait for all six package smoke tests and normal CI to succeed.
3. Replace both Mac draft-release ZIP assets with the signed, notarized, stapled ZIPs. Never publish the unsigned CI Mac archives.
4. Generate `SHA256SUMS.txt` from the **final** six archives and upload it.
5. Verify release notes, signatures, notarization, architecture labels, and checksums; publish the draft as the latest stable release.

The background server listens only on `127.0.0.1`. Launcher `--stop` uses a random per-instance token in an owner-private file; it never terminates a process based only on a PID. Foreground servers and unrelated listeners are not stopped. Source execution via `SamsungController.Web` stays foreground. The launcher does not install a boot/login service.
