#!/usr/bin/env python3
"""Convert the approved master PNG into platform icons (macOS tools, no AI edits)."""
from pathlib import Path
import struct
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parent
with tempfile.TemporaryDirectory(prefix="samsung-icons-") as temporary:
    iconset = Path(temporary) / "SamsungController.iconset"
    iconset.mkdir()
    for size in [16, 32, 128, 256, 512]:
        for scale in [1, 2]:
            name = f"icon_{size}x{size}" + ("@2x" if scale == 2 else "") + ".png"
            subprocess.run(["sips", "-z", str(size * scale), str(size * scale), str(ROOT / "master.png"),
                            "--out", str(iconset / name)], check=True, stdout=subprocess.DEVNULL)
    subprocess.run(["iconutil", "-c", "icns", str(iconset), "-o", str(ROOT / "SamsungController.icns")], check=True)
    pngs = []
    for size in [16, 24, 32, 48, 64, 128, 256]:
        png = Path(temporary) / f"{size}.png"
        subprocess.run(["sips", "-z", str(size), str(size), str(ROOT / "master.png"), "--out", str(png)],
                       check=True, stdout=subprocess.DEVNULL)
        pngs.append((size, png.read_bytes()))
    offset = 6 + 16 * len(pngs)
    directory = bytearray(struct.pack("<HHH", 0, 1, len(pngs)))
    for size, data in pngs:
        directory += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    (ROOT / "SamsungController.ico").write_bytes(directory + b"".join(data for _, data in pngs))
    subprocess.run(["sips", "-z", "512", "512", str(ROOT / "master.png"), "--out", str(ROOT / "SamsungController.png")],
                   check=True, stdout=subprocess.DEVNULL)
