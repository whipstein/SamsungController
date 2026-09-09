"""Create a drag-to-Applications disk image without mounting it or using Finder automation."""
from pathlib import Path
import subprocess
import tempfile


def create_dmg(app: Path, destination: Path):
    if not app.is_dir() or app.is_symlink() or app.name != "SamsungController.app":
        raise ValueError("An existing SamsungController.app bundle is required.")
    if destination.suffix != ".dmg":
        raise ValueError("The disk-image destination must end in .dmg.")
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="samsung-dmg-") as temporary:
        staging = Path(temporary)
        # ditto preserves code signatures, symlinks, and the stapled app ticket.
        subprocess.run(["ditto", str(app), str(staging / app.name)], check=True)
        (staging / "Applications").symlink_to("/Applications", target_is_directory=True)
        (staging / "INSTALL.txt").write_text(
            "SamsungController\n\n"
            "1. Quit any running SamsungController app.\n"
            "2. Drag SamsungController.app onto Applications.\n"
            "3. Eject this disk image.\n"
            "4. Open SamsungController from Applications. Allow Local Network access.\n"
            "5. Enable IP Remote and Power On with Mobile on your display, then pair.\n\n"
            "The app starts its server in the background and opens your browser.\n"
            "Use README beside Help for the complete, offline user guide.\n"
            "No .NET installation is required. Closing the browser leaves the server\n"
            "running; use Quit app at the top of the page to stop it.\n"
            "Your profiles and credentials are stored separately from the app.\n",
            encoding="utf-8")
        subprocess.run(["hdiutil", "create", "-volname", "SamsungController", "-fs", "HFS+",
                        "-format", "UDZO", "-srcfolder", str(staging), "-ov", str(destination)], check=True)
    subprocess.run(["hdiutil", "verify", str(destination)], check=True)
