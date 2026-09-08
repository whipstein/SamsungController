#!/usr/bin/env python3
"""Build self-contained desktop packages. Never reads the user's application data."""
import argparse
import json
import os
from pathlib import Path
import plistlib
import shutil
import subprocess
import sys
import tarfile
import tempfile
import xml.etree.ElementTree as ET
import zipfile
from macos.apphost_identity import prepare_apphosts

ROOT = Path(__file__).resolve().parents[1]
PACKAGES = {"osx-arm64": "macos-arm64", "osx-x64": "macos-x64", "win-x64": "windows-x64", "win-arm64": "windows-arm64", "linux-x64": "linux-x64", "linux-arm64": "linux-arm64"}


def archive(folder, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.name.endswith(".tar.gz"):
        with tarfile.open(destination, "w:gz") as bundle:
            bundle.add(folder, arcname=folder.name)
    elif sys.platform == "darwin":
        subprocess.run(["ditto", "-c", "-k", "--sequesterRsrc", "--keepParent", str(folder), str(destination)], check=True)
    else:
        with zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
            for item in sorted(folder.rglob("*")):
                bundle.write(item, item.relative_to(folder.parent))


def build(rid, output):
    version = ET.parse(ROOT / "Directory.Build.props").findtext(".//Version")
    staging = ROOT / "artifacts" / "package-builds"
    staging.mkdir(parents=True, exist_ok=True)
    folder = Path(tempfile.mkdtemp(prefix=rid + "-", dir=staging)) / "SamsungController"
    folder.mkdir()
    app = folder / "SamsungController.app" if rid.startswith("osx-") else None
    payload = app / "Contents" / "Resources" / "server" if app else folder
    payload.mkdir(parents=True, exist_ok=True)
    for project in ["Web", "Cli", "App"]:
        subprocess.run(["dotnet", "publish", str(ROOT / "src" / ("SamsungController." + project)), "--configuration", "Release", "--runtime", rid,
                        "--self-contained", "true", "--disable-build-servers", "-m:1", "-p:DebugSymbols=false", "-p:DebugType=None", "--output", str(payload)], check=True, cwd=ROOT)
    for source, name in [("README.md", "README.md"), ("LICENSE", "LICENSE"), ("packaging/PACKAGE-README.md", "START-HERE.md"), ("packaging/RELEASE-NOTES.md", "RELEASE-NOTES.md")]:
        shutil.copy2(ROOT / source, folder / name)
    shutil.copytree(ROOT / "docs", folder / "docs")
    shutil.copytree(ROOT / "samples", folder / "samples")
    (folder / "VERSION.txt").write_text("v" + version + "\n", encoding="utf-8")
    if app:
        prepare_apphosts(payload)
        (app / "Contents" / "MacOS").mkdir()
        subprocess.run(["xcrun", "clang", "-arch", "arm64" if rid == "osx-arm64" else "x86_64", "-mmacosx-version-min=14.0",
                        "-fobjc-arc", "-Wall", "-Wextra", "-Werror", "-framework", "AppKit",
                        str(ROOT / "packaging/macos/launcher.m"), "-o", str(app / "Contents" / "MacOS" / "SamsungController")], check=True)
        shutil.copy2(ROOT / "LICENSE", app / "Contents" / "Resources" / "LICENSE")
        shutil.copy2(ROOT / "packaging/icons/SamsungController.icns", app / "Contents/Resources/SamsungController.icns")
        with (app / "Contents" / "Info.plist").open("wb") as target:
            plistlib.dump({"CFBundleName": "SamsungController", "CFBundleDisplayName": "SamsungController", "CFBundleIdentifier": "com.whipstein.samsungcontroller",
                          "CFBundleExecutable": "SamsungController", "CFBundlePackageType": "APPL", "CFBundleVersion": version, "LSMinimumSystemVersion": "14.0",
                          "CFBundleShortVersionString": version, "LSUIElement": True, "NSHighResolutionCapable": True,
                          "CFBundleIconFile": "SamsungController.icns",
                          "NSLocalNetworkUsageDescription": "SamsungController connects to your Samsung displays on your local network when you choose Connect."}, target)
    elif rid.startswith("linux-"):
        shutil.copy2(ROOT / "packaging/icons/SamsungController.png", folder / "SamsungController.png")
        shutil.copy2(ROOT / "packaging/linux/install-shortcut.sh", folder / "install-shortcut.sh")
        (folder / "install-shortcut.sh").chmod(0o755)
    extension = ".tar.gz" if rid.startswith("linux-") else ".zip"
    output.mkdir(parents=True, exist_ok=True)
    destination = output / ("SamsungController-v" + version + "-" + PACKAGES[rid] + extension)
    archive(folder, destination)
    manifest = {"rid": rid, "version": version, "folder": str(folder), "payload": str(payload), "app": str(app) if app else None, "archive": str(destination)}
    (output / ("build-" + rid + ".json")).write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps(manifest, indent=2))
    return manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", choices=PACKAGES, required=True)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/dist")
    options = parser.parse_args()
    build(options.rid, options.output.resolve())
