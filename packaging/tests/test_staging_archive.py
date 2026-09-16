import importlib.util
import os
from pathlib import Path
import plistlib
import tempfile
import unittest
from unittest.mock import patch
import zipfile

spec = importlib.util.spec_from_file_location("staging_archive", Path(__file__).resolve().parents[1] / "macos/staging_archive.py")
staging = importlib.util.module_from_spec(spec)
spec.loader.exec_module(staging)


class StagingArchiveTests(unittest.TestCase):
    def bundle(self, root, parent="package-builds.noindex"):
        app = root / "artifacts" / parent / "osx-arm64-test" / "SamsungController/SamsungController.app"
        (app / "Contents").mkdir(parents=True)
        (app / "Contents/Info.plist").write_bytes(plistlib.dumps({"CFBundleIdentifier": "com.whipstein.samsungcontroller"}))
        (app / "Contents/payload").write_bytes(b"preserve me")
        return app

    def archive_command(self, command, **kwargs):
        source, destination = Path(command[-2]), Path(command[-1])
        with zipfile.ZipFile(destination, "w") as saved:
            for item in source.rglob("*"):
                if item.is_file():
                    saved.write(item, str(item.relative_to(source.parent)))

    def test_archive_is_verified_and_keeps_recoverable_app_bytes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            for parent in ["package-builds", "package-builds.noindex"]:
                app = self.bundle(root, parent)
                with patch.object(staging.subprocess, "run", side_effect=self.archive_command):
                    result = staging.archive_staging_app(app, root)
                self.assertFalse(app.exists())
                with zipfile.ZipFile(result) as saved:
                    self.assertEqual(b"preserve me", saved.read("SamsungController.app/Contents/payload"))

    def test_bad_or_incomplete_archive_never_removes_app(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            app = self.bundle(root)
            def incomplete(command, **kwargs):
                with zipfile.ZipFile(command[-1], "w") as saved:
                    saved.writestr("wrong", "wrong")
            with patch.object(staging.subprocess, "run", side_effect=incomplete), self.assertRaises(KeyError):
                staging.archive_staging_app(app, root)
            self.assertTrue(app.exists())

    def test_existing_archive_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            app = self.bundle(root)
            app.with_suffix(".app.zip").write_bytes(b"old archive")
            with self.assertRaises(ValueError):
                staging.archive_staging_app(app, root)
            self.assertTrue(app.exists())
            self.assertEqual(b"old archive", app.with_suffix(".app.zip").read_bytes())

    def test_changed_bytes_in_valid_zip_never_remove_original(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            app = self.bundle(root)
            def corrupt(command, **kwargs):
                with zipfile.ZipFile(command[-1], "w") as saved:
                    for item in app.rglob("*"):
                        if item.is_file():
                            saved.writestr(str(item.relative_to(app.parent)), b"different bytes")
            with patch.object(staging.subprocess, "run", side_effect=corrupt), self.assertRaises(ValueError):
                staging.archive_staging_app(app, root)
            self.assertEqual(b"preserve me", (app / "Contents/payload").read_bytes())

    @unittest.skipIf(os.name == "nt", "POSIX symlink guard")
    def test_symlinked_staging_directory_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            app = self.bundle(root)
            alias = root / "artifacts/package-builds"
            alias.symlink_to(root / "artifacts/package-builds.noindex", target_is_directory=True)
            with self.assertRaises(ValueError):
                staging.validated_staging_app(alias / app.relative_to(root / "artifacts/package-builds.noindex"), root)

    def test_rejects_installed_apps_other_paths_and_identities(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            app = self.bundle(root)
            for path in [Path("/Applications/SamsungController.app"), root, root / "SamsungController.app",
                         root / "artifacts/other/osx-arm64-test/SamsungController/SamsungController.app"]:
                with self.subTest(path=path), self.assertRaises(ValueError):
                    staging.validated_staging_app(path, root)
            (app / "Contents/Info.plist").write_bytes(plistlib.dumps({"CFBundleIdentifier": "other"}))
            with self.assertRaises(ValueError):
                staging.validated_staging_app(app, root)
