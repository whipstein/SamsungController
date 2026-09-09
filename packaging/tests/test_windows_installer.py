import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

PACKAGING = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("windows_installer", PACKAGING / "windows/installer.py")
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)


class WindowsInstallerTests(unittest.TestCase):
    def test_renames_only_apphost_and_preserves_managed_payload(self):
        with tempfile.TemporaryDirectory() as temporary:
            payload = Path(temporary)
            for name in ["SamsungController.App.exe", "SamsungController.App.dll", "SamsungController.App.runtimeconfig.json", "SamsungController.Web.exe", "samsungctl.exe"]:
                (payload / name).write_bytes(name.encode())
            launcher = installer.prepare_launcher(payload)
            self.assertEqual(launcher.name, "00 - Start SamsungController Server.exe")
            self.assertEqual(launcher.read_bytes(), b"SamsungController.App.exe")
            self.assertFalse((payload / "SamsungController.App.exe").exists())
            self.assertEqual((payload / "SamsungController.App.dll").read_bytes(), b"SamsungController.App.dll")
            self.assertEqual(sorted(item.name for item in payload.iterdir())[0], launcher.name)

    def test_compiles_architecture_specific_installer_from_the_portable_payload(self):
        for rid, architecture in [("win-x64", "x64compatible"), ("win-arm64", "arm64")]:
            with self.subTest(rid=rid), tempfile.TemporaryDirectory(prefix="installer test ") as temporary:
                payload = Path(temporary) / "payload with spaces"
                payload.mkdir()
                (payload / installer.LAUNCHER).touch()
                output = Path(temporary)
                expected = output / f"SamsungController-v1.2.3-windows-{rid.removeprefix('win-')}-setup.exe"
                with patch.object(installer.subprocess, "run", side_effect=lambda *_args, **_kwargs: expected.touch()) as run:
                    actual = installer.build_installer(payload, output, "1.2.3", rid, "C:/Compiler with spaces/ISCC.exe")
                self.assertEqual(actual, expected)
                command = run.call_args.args[0]
                self.assertIn(f"/DPayloadDir={payload.resolve()}", command)
                self.assertIn(f"/DTargetArchitecture={architecture}", command)
                self.assertIn("/DAppVersion=1.2.3", command)
                self.assertTrue(run.call_args.kwargs["check"])

    def test_rejects_wrong_runtime_missing_launcher_and_missing_compiler(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            with self.assertRaises(ValueError):
                installer.build_installer(folder, folder, "1.2.3", "linux-x64")
            with self.assertRaises(ValueError):
                installer.build_installer(folder, folder, "1.2.3", "win-x64")
            (folder / installer.LAUNCHER).touch()
            with patch.dict(installer.os.environ, {}, clear=True), patch.object(installer.shutil, "which", return_value=None):
                with self.assertRaisesRegex(RuntimeError, "portable-only"):
                    installer.build_installer(folder, folder, "1.2.3", "win-x64")

    def test_missing_compiler_output_fails_the_build(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            (folder / installer.LAUNCHER).touch()
            with patch.object(installer.subprocess, "run"):
                with self.assertRaisesRegex(RuntimeError, "expected installer"):
                    installer.build_installer(folder, folder, "1.2.3", "win-x64", "ISCC.exe")

    def test_template_is_per_user_and_never_kills_servers_or_deletes_private_data(self):
        script = (PACKAGING / "windows/SamsungController.iss").read_text()
        self.assertIn("PrivilegesRequired=lowest", script)
        self.assertIn(r"DefaultDirName={localappdata}\Programs\SamsungController", script)
        self.assertIn("CloseApplications=no", script)
        self.assertIn("RestartApplications=no", script)
        self.assertIn(r"AppMutex=Local\SamsungController.Server.Running", script)
        self.assertIn("Use Quit app at the top of its webpage", script)
        self.assertNotIn("[UninstallDelete]", script)
        self.assertNotIn("[InstallDelete]", script)
        self.assertNotIn("[UninstallRun]", script)
        self.assertIn('Flags: nowait postinstall skipifsilent', script)
        self.assertIn('Flags: unchecked', script)
        source = (PACKAGING.parent / "src/SamsungController.Desktop/DesktopFiles.cs").read_text()
        self.assertIn(r'WindowsServerMutex = @"Local\SamsungController.Server.Running"', source)

    def test_ci_includes_installer_build_test_and_release_asset(self):
        workflow = (PACKAGING.parent / ".github/workflows/release.yml").read_text()
        self.assertIn("install-inno.ps1", workflow)
        self.assertIn("verify-installer.py", workflow)
        self.assertEqual(workflow.count("artifacts/dist/*-setup.exe"), 3)
