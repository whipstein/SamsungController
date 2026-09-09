"""Build a per-user Windows installer from the same staged files as the portable ZIP."""
import os
from pathlib import Path
import shutil
import subprocess

LAUNCHER = "00 - Start SamsungController Server.exe"


def prepare_launcher(payload):
    original = payload / "SamsungController.App.exe"
    destination = payload / LAUNCHER
    # The apphost embeds SamsungController.App.dll; renaming only the host keeps
    # its runtime/dependency lookup intact. Native smoke tests exercise this name.
    original.rename(destination)
    return destination


def build_installer(folder, output, version, rid, compiler=None):
    if rid not in {"win-x64", "win-arm64"}:
        raise ValueError("A Windows runtime identifier is required.")
    if not (folder / LAUNCHER).is_file():
        raise ValueError("The staged Windows launcher is missing.")
    compiler = compiler or os.environ.get("SAMSUNG_INNO_COMPILER") or shutil.which("ISCC.exe")
    if not compiler:
        raise RuntimeError("Install Inno Setup 6.7.3+ and set SAMSUNG_INNO_COMPILER to ISCC.exe, or use --portable-only for a cross-build.")
    name = f"SamsungController-v{version}-windows-{rid.removeprefix('win-')}-setup"
    command = [str(compiler), "/Qp", f"/DPayloadDir={folder.resolve()}", f"/DAppVersion={version}",
               f"/DTargetArchitecture={'arm64' if rid == 'win-arm64' else 'x64compatible'}",
               f"/DOutputName={name}", f"/O{output.resolve()}", str(Path(__file__).with_name("SamsungController.iss"))]
    subprocess.run(command, check=True)
    destination = output / (name + ".exe")
    if not destination.is_file():
        raise RuntimeError("Inno Setup did not create the expected installer.")
    return destination
