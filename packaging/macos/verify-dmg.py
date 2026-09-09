#!/usr/bin/env python3
"""Mount a DMG read-only, inspect it, and smoke-test an isolated installed copy; never contacts TVs."""
import argparse
import json
from pathlib import Path
import subprocess
import sys
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--manifest", type=Path, required=True)
parser.add_argument("--require-notarization", action="store_true")
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
    copied_manifest = root / "installed.json"
    copied_manifest.write_text(json.dumps({**manifest, "app": str(installed), "folder": str(installed.parent),
        "payload": str(installed / "Contents/Resources/server")}), encoding="utf-8")
    subprocess.run([sys.executable, str(Path(__file__).resolve().parents[1] / "smoke-test.py"), "--manifest", str(copied_manifest)], check=True)
print("PASS: read-only DMG, Applications shortcut, instructions, and isolated installed-copy smoke test.")
