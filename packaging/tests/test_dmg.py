import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

MODULE = Path(__file__).resolve().parents[1] / "macos/dmg.py"
spec = importlib.util.spec_from_file_location("samsung_dmg", MODULE)
dmg = importlib.util.module_from_spec(spec)
spec.loader.exec_module(dmg)


class DmgTests(unittest.TestCase):
    @unittest.skipIf(os.name == "nt", "DMG staging uses macOS/POSIX symlinks; native Mac CI verifies the image")
    def test_builds_image_with_app_applications_link_and_simple_instructions(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            app = root / "SamsungController.app"
            app.mkdir()
            output = root / "result.dmg"
            calls = []

            def run(command, **kwargs):
                calls.append(command)
                self.assertTrue(kwargs["check"])
                if command[:2] == ["hdiutil", "create"]:
                    staging = Path(command[command.index("-srcfolder") + 1])
                    self.assertEqual((staging / "Applications").readlink(), Path("/Applications"))
                    instructions = (staging / "INSTALL.txt").read_text()
                    self.assertIn("Drag SamsungController.app", instructions)
                    self.assertIn("Eject", instructions)
                    self.assertIn("README", instructions)
                    self.assertEqual(set(item.name for item in staging.iterdir()), {"Applications", "INSTALL.txt"})

            with patch.object(dmg.subprocess, "run", side_effect=run):
                dmg.create_dmg(app, output)
            self.assertEqual(calls[0][:2], ["ditto", str(app)])
            self.assertEqual(calls[-1], ["hdiutil", "verify", str(output)])
            self.assertFalse(any("attach" in call for call in calls))

    def test_rejects_missing_or_wrong_app_and_wrong_extension(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with self.assertRaises(ValueError):
                dmg.create_dmg(root / "missing.app", root / "result.dmg")
            app = root / "SamsungController.app"
            app.mkdir()
            with self.assertRaises(ValueError):
                dmg.create_dmg(app, root / "result.zip")
