#!/usr/bin/env python3
"""Mount a DMG read-only, inspect it, and smoke-test an isolated installed copy; never contacts TVs."""
import argparse
import json
from pathlib import Path
import subprocess
import sys
import tempfile

LSREGISTER = "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"


def unregister_test_copy(app, temporary_root):
    # Only the exact temporary app paths created by this test. Never reset the
    # database, touch the installed app, or change any privacy preference.
    relative = app.relative_to(temporary_root)
    if relative.parts not in (("mounted", "SamsungController.app"), ("installed", "SamsungController.app")):
        raise ValueError("Refusing to unregister a path outside this test.")
    result = subprocess.run([LSREGISTER, "-u", str(app)], capture_output=True, text=True)
    # macOS can already have discarded the registration when the volume was
    # detached. -10814 means no application was found, not a cleanup failure.
    already_gone = not (app / "Contents/Info.plist").exists() and "-10814" in result.stdout + result.stderr
    if result.returncode and not already_gone:
        raise subprocess.CalledProcessError(result.returncode, result.args, result.stdout, result.stderr)

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--manifest", type=Path, required=True)
parser.add_argument("--require-notarization", action="store_true")
parser.add_argument("--native-app", action="store_true", help="Exercise GUI app lifetime on disposable CI/VMs only")
options = parser.parse_args()
manifest = json.loads(options.manifest.read_text())
image = Path(manifest["archive"])
if image.suffix != ".dmg" or not image.is_file():
    raise SystemExit("An existing DMG manifest is required.")
subprocess.run(["hdiutil", "verify", str(image)], check=True)
if options.require_notarization:
    subprocess.run(["codesign", "--verify", "--verbose=2", str(image)], check=True)
    subprocess.run(["xcrun", "stapler", "validate", str(image)], check=True)
    subprocess.run(["spctl", "--assess", "--type", "open", "--context", "context:primary-signature", "--verbose=2", str(image)], check=True)
with tempfile.TemporaryDirectory(prefix="samsung-dmg-test-") as temporary:
    root = Path(temporary)
    mount = root / "mounted"
    mount.mkdir()
    subprocess.run(["hdiutil", "attach", "-readonly", "-nobrowse", "-mountpoint", str(mount), str(image)], check=True)
    try:
        app = mount / "SamsungController.app"
        assert app.is_dir() and not app.is_symlink()
        assert (mount / "Applications").is_symlink() and (mount / "Applications").readlink() == Path("/Applications")
        assert "Drag SamsungController.app" in (mount / "INSTALL.txt").read_text()
        if options.require_notarization:
            subprocess.run(["codesign", "--verify", "--deep", "--strict", str(app)], check=True)
            subprocess.run(["xcrun", "stapler", "validate", str(app)], check=True)
            subprocess.run(["spctl", "--assess", "--type", "execute", "--verbose=2", str(app)], check=True)
        installed = root / "installed" / app.name
        installed.parent.mkdir()
        subprocess.run(["ditto", str(app), str(installed)], check=True)
    finally:
        subprocess.run(["hdiutil", "detach", str(mount)], check=True)
        unregister_test_copy(mount / "SamsungController.app", root)
    copied_manifest = root / "installed.json"
    copied_manifest.write_text(json.dumps({**manifest, "app": str(installed), "folder": str(installed.parent),
        "payload": str(installed / "Contents/Resources/server")}), encoding="utf-8")
    try:
        subprocess.run([sys.executable, str(Path(__file__).resolve().parents[1] / "smoke-test.py"), "--manifest", str(copied_manifest)]
                       + (["--native-app"] if options.native_app else []), check=True)
    finally:
        unregister_test_copy(installed, root)
print("PASS: read-only DMG, Applications shortcut, instructions, and isolated installed-copy smoke test.")
