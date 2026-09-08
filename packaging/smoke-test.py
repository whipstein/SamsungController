#!/usr/bin/env python3
"""Exercise only the packaged local server; never pairs with or sends requests to a TV."""
import argparse
import json
import os
import plistlib
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
if manifest.get("app"):
    bundle = Path(manifest["app"])
    info = plistlib.loads((bundle / "Contents/Info.plist").read_bytes())
    assert info["NSLocalNetworkUsageDescription"] and info["CFBundleIdentifier"] == "com.whipstein.samsungcontroller"
    assert (bundle / "Contents/Resources" / info["CFBundleIconFile"]).stat().st_size > 1000
    from macos.apphost_identity import uuid_offset
    identifiers = []
    for executable in [app, payload / "SamsungController.App", payload / "SamsungController.Web", cli]:
        data = executable.read_bytes()
        offset = uuid_offset(data)
        identifiers.append(data[offset:offset + 16])
    assert len(set(identifiers)) == 4, "Local Network identities must not collide"
elif os.name != "nt":
    assert (payload / "SamsungController.png").stat().st_size > 1000


def request(route, method="GET", headers=None):
    return opener.open(urllib.request.Request(address + route, method=method, headers=headers or {}), timeout=3)


with tempfile.TemporaryDirectory(prefix="samsung-desktop-smoke-") as temporary:
    env = {**os.environ, "SamsungController__ConfigurationDirectory": temporary}
    command = [str(app), "--port", str(port), "--no-browser"]
    owner = None

    def start():
        if not manifest.get("app"):
            subprocess.run(command, env=env, check=True, timeout=75, cwd=temporary)
            return None
        process = subprocess.Popen(command, env=env, cwd=temporary)
        deadline = time.monotonic() + 75
        while time.monotonic() < deadline:
            assert process.poll() is None, "The responsible Mac app exited before server readiness"
            try:
                with request("/_app/status") as response:
                    if json.load(response)["managed"]:
                        return process
            except (urllib.error.URLError, TimeoutError):
                time.sleep(0.15)
        raise AssertionError("Mac app did not start its server")

    def stop():
        subprocess.run(command + ["--stop"], env=env, check=True, timeout=30, cwd=temporary)
        if owner is not None:
            assert owner.wait(timeout=15) == 0, "The responsible Mac app did not exit with its server"

    try:
        owner = start()
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
        if owner is not None:
            assert owner.poll() is None, "Duplicate launch must not replace/close the responsible app"
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
        stop()
    assert not (Path(temporary) / "desktop" / ("server-" + str(port) + ".json")).exists()
    # Restart cleanly after quitting; token/instance identity must rotate.
    owner = start()
    try:
        with request("/_app/status") as response:
            assert json.load(response)["instance"] != status["instance"]
    finally:
        stop()
print("PASS: packaged startup, icons/identity, app lifetime, assets, duplicate launch, shutdown authorization, CLI, quit, and restart; no TV requests.")
