import importlib.util
from pathlib import Path
import plistlib
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("signing_identity", Path(__file__).resolve().parents[1] / "macos/signing_identity.py")
identity = importlib.util.module_from_spec(spec)
spec.loader.exec_module(identity)


class SigningIdentityTests(unittest.TestCase):
    def test_identifiers_are_distinct_stable_and_independent_of_staging_path(self):
        identifiers = []
        for name, expected in identity.APPHOST_IDS.items():
            for root in [Path("/one/v1/app"), Path("/two/v2/app")]:
                actual = identity.identifier_for(root / "Contents/Resources/server" / name, root)
                self.assertEqual(expected, actual)
            identifiers.append(actual)
        self.assertEqual(len(identifiers), len(set(identifiers)))
        self.assertNotIn(identity.BUNDLE_ID, identifiers)

    def test_library_identifiers_are_per_file_not_temp_path(self):
        for filename in ["libhostfxr.dylib", "libcoreclr.dylib"]:
            self.assertEqual(identity.identifier_for(Path("/a/Contents/Resources/server") / filename, "/a"),
                             identity.identifier_for(Path("/b/Contents/Resources/server") / filename, "/b"))
        self.assertNotEqual(identity.identifier_for("/a/one/lib.dylib", "/a"),
                            identity.identifier_for("/a/two/lib.dylib", "/a"))
        with self.assertRaises(ValueError):
            identity.identifier_for("/outside/file", "/app")

    def bundle(self, root):
        app = Path(root) / "SamsungController.app"
        (app / "Contents").mkdir(parents=True)
        (app / "Contents/Info.plist").write_bytes(plistlib.dumps({"CFBundleIdentifier": identity.BUNDLE_ID}))
        return app

    def test_upgrade_checks_both_designated_requirements(self):
        with tempfile.TemporaryDirectory() as root:
            app = self.bundle(root)
            previous = Path("/Applications/SamsungController.app")
            def inspect(path):
                name = identity.APPHOST_IDS.get(path.name, identity.BUNDLE_ID)
                return {"Identifier": name, "TeamIdentifier": identity.TEAM_ID}
            with patch.object(identity, "inspect_signature", side_effect=inspect), \
                 patch.object(identity, "designated_requirement", side_effect=lambda path: "old" if path == previous else "new"), \
                 patch.object(identity.subprocess, "run") as run:
                report = identity.verify_identity(app, previous_app=previous)
            self.assertEqual(identity.TEAM_ID, report["teamIdentifier"])
            self.assertEqual([
                ["codesign", "--verify", "--strict", "-R", "=old", str(app)],
                ["codesign", "--verify", "--strict", "-R", "=new", str(previous)],
            ], [call.args[0] for call in run.call_args_list])

    def test_changed_team_identifier_or_bundle_is_rejected(self):
        with tempfile.TemporaryDirectory() as root:
            app = self.bundle(root)
            for actual in [
                {"Identifier": identity.BUNDLE_ID, "TeamIdentifier": "OTHER"},
                {"Identifier": "com.other.app", "TeamIdentifier": identity.TEAM_ID},
                {"Identifier": identity.BUNDLE_ID, "TeamIdentifier": "not set"},
            ]:
                with self.subTest(actual=actual), patch.object(identity, "inspect_signature", return_value=actual):
                    with self.assertRaises(ValueError):
                        identity.verify_identity(app)
            (app / "Contents/Info.plist").write_bytes(plistlib.dumps({"CFBundleIdentifier": "changed"}))
            with self.assertRaises(ValueError):
                identity.verify_identity(app)

    def test_codesign_output_parser_requires_identity(self):
        result = subprocess.CompletedProcess([], 0, "", "Identifier=app\nTeamIdentifier=TEAM\ndesignated => anchor apple\n")
        with patch.object(identity.subprocess, "run", return_value=result):
            self.assertEqual({"Identifier": "app", "TeamIdentifier": "TEAM"}, identity.inspect_signature("/app"))
            self.assertEqual("anchor apple", identity.designated_requirement("/app"))
        with patch.object(identity.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "designated => anchor apple\n", "Executable=/app\n")):
            self.assertEqual("anchor apple", identity.designated_requirement("/app"))
        with patch.object(identity.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "", "")):
            with self.assertRaises(ValueError):
                identity.inspect_signature("/app")
            with self.assertRaises(ValueError):
                identity.designated_requirement("/app")
