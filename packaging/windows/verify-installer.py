"""CI-only install/upgrade/uninstall checks in temporary folders; no TV requests."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time
import urllib.request

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from windows.installer import LAUNCHER

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--manifest", required=True, type=Path)
options = parser.parse_args()
if os.name != "nt" or os.environ.get("GITHUB_ACTIONS") != "true":
    raise SystemExit("This installer/uninstaller integration test is restricted to disposable Windows CI runners.")
import winreg

manifest = json.loads(options.manifest.read_text())
installer = Path(manifest["installer"])
source = Path(manifest["payload"])
registry_path = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\{D459328D-1B96-441E-A03B-431691A317A9}_is1"


def registered():
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, registry_path, access=winreg.KEY_READ | winreg.KEY_WOW64_64KEY):
            return True
    except FileNotFoundError:
        return False


def shell_folder(name):
    return Path(subprocess.check_output(["powershell.exe", "-NoProfile", "-Command",
        f"[Environment]::GetFolderPath('{name}')"], text=True).strip())


start_link = shell_folder("Programs") / "SamsungController/SamsungController.lnk"
desktop_link = shell_folder("DesktopDirectory") / "SamsungController.lnk"
assert not registered() and not start_link.exists() and not desktop_link.exists(), "Existing installation/shortcuts must never be touched by this test"
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

with tempfile.TemporaryDirectory(prefix="samsung-installer-test-") as temporary:
    root = Path(temporary)
    installed = root / "Installed app with spaces"
    private = root / "private-settings"
    private.mkdir()
    saved = private / "keep-settings.txt"
    saved.write_text("Existing settings must survive install, upgrade, and uninstall.")
    environment = {**os.environ, "SamsungController__ConfigurationDirectory": str(private)}
    common = ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-"]

    def setup(label, succeeds=True):
        result = subprocess.run([str(installer), *common, f"/DIR={installed}", "/TASKS=desktopicon", f"/LOG={root / (label + '.log')}"],
                                env=environment, timeout=150)
        if succeeds:
            assert result.returncode == 0, (root / (label + ".log")).read_text(errors="replace")[-6000:]
        else:
            assert result.returncode != 0, "Setup must refuse to modify a running application"

    def uninstall(succeeds=True):
        # Inno's uninstaller launches a temporary copy and can return before that
        # child releases its files/log. PowerShell -Wait waits for the whole tree.
        script = "$p=Start-Process -FilePath $env:SAMSUNG_TEST_UNINSTALLER -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/LOG=\"'+$env:SAMSUNG_TEST_UNINSTALL_LOG+'\"')) -Wait -PassThru; exit $p.ExitCode"
        result = subprocess.run(["powershell.exe", "-NoProfile", "-Command", script],
            env={**environment, "SAMSUNG_TEST_UNINSTALLER": str(installed / "unins000.exe"),
                 "SAMSUNG_TEST_UNINSTALL_LOG": str(root / "uninstall.log")}, timeout=90)
        assert (result.returncode == 0) == succeeds, "Uninstall must refuse while the app is running and succeed after Quit"
        if succeeds:
            deadline = time.monotonic() + 20
            while (installed / "unins000.exe").exists() and time.monotonic() < deadline:
                time.sleep(0.1)
            assert not (installed / "unins000.exe").exists(), "Uninstaller did not finish cleaning up"

    def verify_files():
        for original in source.rglob("*"):
            if original.is_file():
                copy = installed / original.relative_to(source)
                assert copy.is_file(), copy
                assert hashlib.sha256(copy.read_bytes()).digest() == hashlib.sha256(original.read_bytes()).digest(), copy
        assert registered() and start_link.is_file() and desktop_link.is_file()
        for shortcut in [start_link, desktop_link]:
            script = "$s=(New-Object -ComObject WScript.Shell).CreateShortcut($env:SAMSUNG_TEST_SHORTCUT); $s.TargetPath"
            target = subprocess.check_output(["powershell.exe", "-NoProfile", "-Command", script], text=True,
                env={**environment, "SAMSUNG_TEST_SHORTCUT": str(shortcut)}).strip()
            # Windows resolves RUNNER~1 in TEMP to its long name in .lnk targets.
            # Compare actual files, not equivalent but differently spelled paths.
            assert Path(target).samefile(installed / LAUNCHER), f"Shortcut must use the browser-opening launcher: {target}"

    setup("install")
    try:
        verify_files()
        installed_manifest = root / "installed.json"
        installed_manifest.write_text(json.dumps({**manifest, "folder": str(installed), "payload": str(installed)}))
        subprocess.run([sys.executable, str(Path(__file__).resolve().parents[1] / "smoke-test.py"), "--manifest", str(installed_manifest)],
                       env=environment, check=True, timeout=240)
        # A live app must not be killed during upgrade or uninstall (any port).
        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            port = reservation.getsockname()[1]
        command = [str(installed / LAUNCHER), "--no-browser", "--port", str(port)]
        subprocess.run(command, env=environment, check=True, timeout=75)
        try:
            with opener.open(f"http://127.0.0.1:{port}/_app/status", timeout=5) as reply:
                original_status = json.load(reply)
            setup("blocked-upgrade", succeeds=False)
            uninstall(succeeds=False)
            with opener.open(f"http://127.0.0.1:{port}/_app/status", timeout=5) as reply:
                assert json.load(reply)["instance"] == original_status["instance"]
            verify_files()
        finally:
            subprocess.run(command + ["--stop"], env=environment, check=True, timeout=40)
        setup("upgrade")
        verify_files()
        # A user-created file in the install directory is not installer-owned.
        user_file = installed / "keep-my-file.txt"
        user_file.write_text("Not installed by Setup")
    finally:
        if (installed / "unins000.exe").exists():
            uninstall()
    deadline = time.monotonic() + 20
    while registered() and time.monotonic() < deadline:
        time.sleep(0.1)
    assert not registered() and not start_link.exists() and not desktop_link.exists()
    assert not (installed / LAUNCHER).exists()
    assert saved.read_text() == "Existing settings must survive install, upgrade, and uninstall."
    assert user_file.read_text() == "Not installed by Setup"
print("PASS: per-user install, shortcut targets, installed startup/quit, upgrade, running-app protection, uninstall, and retained settings; no TV requests.")
