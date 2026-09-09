#!/usr/bin/env python3
"""Sign a staged Mac app using Keychain; optionally notarize, staple, and rearchive."""
import argparse
import json
import plistlib
from pathlib import Path
import subprocess
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from build import archive
from macos.dmg import create_dmg

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--manifest", type=Path, required=True)
parser.add_argument("--identity", required=True, help="Developer ID Application identity name or SHA-1 fingerprint (not a private key)")
parser.add_argument("--keychain-profile", help="Existing notarytool Keychain profile; never pass passwords to this script")
options = parser.parse_args()
manifest = json.loads(options.manifest.read_text())
app = Path(manifest["app"])
if not app.is_dir() or app.suffix != ".app":
    raise SystemExit("Manifest does not identify an existing app bundle.")
entitlements = Path(__file__).with_name("entitlements.plist")
with (app / "Contents/Info.plist").open("rb") as file:
    entrypoint = app / "Contents/MacOS" / plistlib.load(file)["CFBundleExecutable"]
for item in sorted(app.rglob("*")):
    if not item.is_file() or item.is_symlink() or item == entrypoint:
        continue
    kind = subprocess.check_output(["file", "-b", str(item)], text=True)
    if "Mach-O" not in kind:
        continue
    command = ["codesign", "--force", "--timestamp", "--options", "runtime", "--sign", options.identity]
    if "executable" in kind:
        command += ["--entitlements", str(entitlements)]
    subprocess.run(command + [str(item)], check=True)
subprocess.run(["codesign", "--force", "--timestamp", "--options", "runtime", "--sign", options.identity, str(app)], check=True)
subprocess.run(["codesign", "--verify", "--deep", "--strict", "--verbose=2", str(app)], check=True)
if options.keychain_profile:
    with tempfile.TemporaryDirectory(prefix="samsung-notarize-") as temporary:
        upload = Path(temporary) / "SamsungController.zip"
        subprocess.run(["ditto", "-c", "-k", "--keepParent", str(app), str(upload)], check=True)
        result = subprocess.run(["xcrun", "notarytool", "submit", str(upload), "--keychain-profile", options.keychain_profile,
                                 "--wait", "--timeout", "30m", "--output-format", "json"], text=True, capture_output=True)
        print(result.stdout, flush=True)
        if result.returncode:
            print(result.stderr, file=sys.stderr)
            raise SystemExit(result.returncode)
        submission = json.loads(result.stdout)
        if submission.get("status") != "Accepted":
            raise SystemExit("Notarization was not accepted. Use notarytool log with the printed submission ID; do not publish this artifact.")
        subprocess.run(["xcrun", "stapler", "staple", str(app)], check=True)
        subprocess.run(["xcrun", "stapler", "validate", str(app)], check=True)
        subprocess.run(["spctl", "--assess", "--type", "execute", "--verbose=2", str(app)], check=True)
destination = Path(manifest["archive"])
if destination.suffix == ".dmg":
    # The app ticket is already stapled. Build the actual distribution from
    # that app, sign/notarize the container too, and retain both offline tickets.
    create_dmg(app, destination)
    subprocess.run(["codesign", "--force", "--timestamp", "--sign", options.identity,
                    "--identifier", "com.whipstein.samsungcontroller.installer", str(destination)], check=True)
    subprocess.run(["codesign", "--verify", "--verbose=2", str(destination)], check=True)
    if options.keychain_profile:
        result = subprocess.run(["xcrun", "notarytool", "submit", str(destination), "--keychain-profile", options.keychain_profile,
                                 "--wait", "--timeout", "30m", "--output-format", "json"], text=True, capture_output=True)
        print(result.stdout, flush=True)
        if result.returncode:
            print(result.stderr, file=sys.stderr)
            raise SystemExit(result.returncode)
        if json.loads(result.stdout).get("status") != "Accepted":
            raise SystemExit("Disk-image notarization was not accepted. Do not publish this artifact.")
        subprocess.run(["xcrun", "stapler", "staple", str(destination)], check=True)
        subprocess.run(["xcrun", "stapler", "validate", str(destination)], check=True)
        subprocess.run(["spctl", "--assess", "--type", "open", "--context", "context:primary-signature", "--verbose=2", str(destination)], check=True)
else:
    archive(Path(manifest["folder"]), destination)
print("Signed" + (", notarized and stapled" if options.keychain_profile else " only — notarization is still required") + ": " + manifest["archive"])
