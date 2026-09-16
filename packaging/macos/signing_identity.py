"""Stable Developer ID identities; build UUIDs are deliberately NOT permission IDs."""
from pathlib import Path
import plistlib
import re
import subprocess

BUNDLE_ID = "com.whipstein.samsungcontroller"
TEAM_ID = "EESHX57W67"
APPHOST_IDS = {
    "SamsungController.App": BUNDLE_ID + ".launcher",
    "SamsungController.Web": BUNDLE_ID + ".server",
    "samsungctl": BUNDLE_ID + ".cli",
}


def identifier_for(item, app):
    relative = Path(item).relative_to(Path(app))
    if relative.parent == Path("Contents/Resources/server") and relative.name in APPHOST_IDS:
        return APPHOST_IDS[relative.name]
    # Libraries also get deterministic, per-file identifiers, independent of
    # temp staging paths, version, architecture and the current certificate hash.
    return BUNDLE_ID + ".code." + str(relative).replace("/", ".")


def inspect_signature(path):
    result = subprocess.run(["codesign", "-d", "--verbose=4", str(path)],
                            check=True, text=True, capture_output=True)
    details = dict(re.findall(r"^(Identifier|TeamIdentifier)=(.*)$", result.stderr, re.MULTILINE))
    if not all(details.get(key) for key in ("Identifier", "TeamIdentifier")):
        raise ValueError("Missing signed identifier or team: " + str(path))
    return details


def designated_requirement(path):
    result = subprocess.run(["codesign", "-d", "-r-", str(path)],
                            check=True, text=True, capture_output=True)
    match = re.search(r"^designated => (.+)$", result.stdout + "\n" + result.stderr, re.MULTILINE)
    if not match:
        raise ValueError("Missing designated requirement: " + str(path))
    return match[1]


def verify_identity(app, expected_team=TEAM_ID, previous_app=None):
    app = Path(app)
    info = plistlib.loads((app / "Contents/Info.plist").read_bytes())
    if info.get("CFBundleIdentifier") != BUNDLE_ID:
        raise ValueError("The release bundle identifier changed.")
    report = {}
    for path, identifier in [(app, BUNDLE_ID)] + [
        (app / "Contents/Resources/server" / name, value) for name, value in APPHOST_IDS.items()
    ]:
        actual = inspect_signature(path)
        if actual != {"Identifier": identifier, "TeamIdentifier": expected_team}:
            raise ValueError("Unexpected release signing identity for " + str(path) + ": " + str(actual))
        report[identifier] = actual
    requirement = designated_requirement(app)
    if previous_app:
        previous = Path(previous_app)
        previous_identity = inspect_signature(previous)
        if previous_identity != report[BUNDLE_ID]:
            raise ValueError("The installed app and update do not have the same bundle/signing team.")
        # Mutual requirement checks prove identity continuity, without pinning
        # certificate serials, CDHashes or UUIDs that legitimately change.
        old_requirement = designated_requirement(previous)
        for target, required in [(app, old_requirement), (previous, requirement)]:
            subprocess.run(["codesign", "--verify", "--strict", "-R", "=" + required, str(target)], check=True)
    return {"bundleIdentifier": BUNDLE_ID, "teamIdentifier": expected_team,
            "designatedRequirement": requirement, "executables": report}
