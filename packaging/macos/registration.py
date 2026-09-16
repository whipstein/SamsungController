"""Remove only this DMG test's temporary Launch Services records, never OS consent."""
from pathlib import Path
import re
import subprocess

LSREGISTER = "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"


def unregister_test_copy(app, temporary_root):
    root = Path(temporary_root).resolve()
    app = Path(app).resolve()
    # Canonical /private/var paths matter: macOS may register a different path
    # spelling than the /var/folders alias returned by tempfile.
    relative = app.relative_to(root)
    if relative.parts not in (("mounted", "SamsungController.app"), ("installed", "SamsungController.app")):
        raise ValueError("Refusing to unregister a path outside this test.")
    result = subprocess.run([LSREGISTER, "-u", str(app)], capture_output=True, text=True)
    # -10814 also occurs for a real app that was never registered (managed-only
    # smoke test). Confirm absence in the database rather than guessing from disk.
    if result.returncode and "-10814" not in result.stdout + result.stderr:
        raise subprocess.CalledProcessError(result.returncode, result.args, result.stdout, result.stderr)
    listing = subprocess.check_output([LSREGISTER, "-dump"], text=True)
    paths = re.findall(r"^path:\s+(.+?) \(0x[0-9a-f]+\)$", listing, re.MULTILINE)
    if str(app) in paths:
        raise RuntimeError("Temporary app registration remains: " + str(app))

