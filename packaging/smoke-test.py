#!/usr/bin/env python3
"""Exercise only the packaged local server; never pairs with or sends requests to a TV."""
import argparse
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--manifest", type=Path, required=True)
options = parser.parse_args()
manifest = json.loads(options.manifest.read_text())
payload = Path(manifest["payload"])
app = payload / ("SamsungController.App.exe" if os.name == "nt" else "SamsungController.App")
if manifest.get("app"):
    app = Path(manifest["app"]) / "Contents/MacOS/SamsungController"
cli = payload / ("samsungctl.exe" if os.name == "nt" else "samsungctl")
with socket.socket() as reservation:
    reservation.bind(("127.0.0.1", 0))
    port = reservation.getsockname()[1]
address = "http://127.0.0.1:" + str(port)
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def request(route, method="GET", headers=None):
    return opener.open(urllib.request.Request(address + route, method=method, headers=headers or {}), timeout=3)


with tempfile.TemporaryDirectory(prefix="samsung-desktop-smoke-") as temporary:
    env = {**os.environ, "SamsungController__ConfigurationDirectory": temporary}
    command = [str(app), "--port", str(port), "--no-browser"]
    try:
        subprocess.run(command, env=env, check=True, timeout=75, cwd=temporary)
        with request("/_app/status") as response:
            status = json.load(response)
        assert status["product"] == "SamsungController" and status["managed"] is True
        assert status["version"] == manifest["version"]
        with request("/") as response:
            html = response.read().decode()
        assert "SamsungController" in html and "Choose your display" in html
        with request("/direct.css") as response:
            assert "direct-page-help" in response.read().decode()
        # A second app launch opens the existing instance, rather than starting another.
        subprocess.run(command, env=env, check=True, timeout=10, cwd=temporary)
        with request("/_app/status") as response:
            assert json.load(response)["processId"] == status["processId"]
        for headers in [{}, {"Authorization": "Bearer wrong"}]:
            try:
                request("/_app/stop", "POST", headers)
                raise AssertionError("Unauthenticated shutdown was accepted")
            except urllib.error.HTTPError as error:
                assert error.code == 403
        saved = json.loads((Path(temporary) / "desktop" / ("server-" + str(port) + ".json")).read_text())
        assert saved["Instance"] == status["instance"]
        try:
            request("/_app/stop", "POST", {"Authorization": "Bearer " + saved["Token"], "Origin": "https://example.invalid"})
            raise AssertionError("Cross-origin shutdown was accepted")
        except urllib.error.HTTPError as error:
            assert error.code == 403
        subprocess.run([str(cli), "help"], env=env, check=True, timeout=15, stdout=subprocess.DEVNULL)
    finally:
        subprocess.run(command + ["--stop"], env=env, check=True, timeout=30, cwd=temporary)
    assert not (Path(temporary) / "desktop" / ("server-" + str(port) + ".json")).exists()
    # Restart cleanly after quitting; token/instance identity must rotate.
    subprocess.run(command, env=env, check=True, timeout=75, cwd=temporary)
    try:
        with request("/_app/status") as response:
            assert json.load(response)["instance"] != status["instance"]
    finally:
        subprocess.run(command + ["--stop"], env=env, check=True, timeout=30, cwd=temporary)
print("PASS: packaged startup, assets, duplicate launch, shutdown authorization, CLI, quit, and restart; no TV requests.")
