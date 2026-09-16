import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("registration", Path(__file__).resolve().parents[1] / "macos/registration.py")
registration = importlib.util.module_from_spec(spec)
spec.loader.exec_module(registration)


class TestRegistrationCleanup(unittest.TestCase):
    def test_canonical_path_is_used_and_installed_app_record_is_preserved(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            app = root / "installed/SamsungController.app"
            with patch.object(registration.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "", "")) as run, \
                 patch.object(registration.subprocess, "check_output", return_value="path: /Applications/SamsungController.app (0x123)\n"):
                registration.unregister_test_copy(app, root)
            self.assertEqual([registration.LSREGISTER, "-u", str(app.resolve())], run.call_args.args[0])

    def test_not_registered_is_ok_even_when_temporary_app_exists(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            app = root / "installed/SamsungController.app"
            app.mkdir(parents=True)
            with patch.object(registration.subprocess, "run", return_value=subprocess.CompletedProcess([], 1, "failed to scan: -10814", "")), \
                 patch.object(registration.subprocess, "check_output", return_value=""):
                registration.unregister_test_copy(app, root)

    def test_does_not_claim_success_if_record_is_still_present(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            app = root / "mounted/SamsungController.app"
            with patch.object(registration.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "", "")), \
                 patch.object(registration.subprocess, "check_output", return_value="path: " + str(app) + " (0xabcd)\n"), \
                 self.assertRaises(RuntimeError):
                registration.unregister_test_copy(app, root)

    def test_unknown_errors_are_not_suppressed(self):
        with patch.object(registration.subprocess, "run", return_value=subprocess.CompletedProcess([], 1, "", "permission denied")), \
             self.assertRaises(subprocess.CalledProcessError):
            registration.unregister_test_copy("/private/tmp/test/installed/SamsungController.app", "/private/tmp/test")

    def test_rejects_unrelated_paths_without_running_a_command(self):
        with patch.object(registration.subprocess, "run") as run:
            for app in ["/Applications/SamsungController.app", "/private/tmp/test/other/SamsungController.app",
                        "/private/tmp/test/installed/Other.app"]:
                with self.subTest(app=app), self.assertRaises(ValueError):
                    registration.unregister_test_copy(app, "/private/tmp/test")
            run.assert_not_called()

