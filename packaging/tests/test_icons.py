from pathlib import Path
import struct
import unittest

ROOT = Path(__file__).resolve().parents[1] / "icons"


class PlatformIconTests(unittest.TestCase):
    def test_linux_png_is_square_and_has_alpha(self):
        data = (ROOT / "SamsungController.png").read_bytes()
        self.assertEqual(b"\x89PNG\r\n\x1a\n", data[:8])
        self.assertEqual((512, 512), struct.unpack_from(">II", data, 16))
        self.assertEqual(6, data[25])  # RGBA

    def test_windows_icon_contains_all_requested_png_sizes(self):
        data = (ROOT / "SamsungController.ico").read_bytes()
        self.assertEqual((0, 1, 7), struct.unpack_from("<HHH", data))
        for index, size in enumerate([16, 24, 32, 48, 64, 128, 256]):
            width, height, _, _, planes, depth, length, offset = struct.unpack_from("<BBBBHHII", data, 6 + index * 16)
            self.assertEqual((size % 256, size % 256, 1, 32), (width, height, planes, depth))
            self.assertLessEqual(offset + length, len(data))
            self.assertEqual(b"\x89PNG\r\n\x1a\n", data[offset:offset + 8])
            self.assertEqual((size, size), struct.unpack_from(">II", data, offset + 16))

    def test_mac_icon_is_a_complete_icns_container(self):
        data = (ROOT / "SamsungController.icns").read_bytes()
        self.assertEqual(b"icns", data[:4])
        self.assertEqual(len(data), struct.unpack_from(">I", data, 4)[0])
        offset, entries = 8, set()
        while offset < len(data):
            kind, length = struct.unpack_from(">4sI", data, offset)
            self.assertGreater(length, 8)
            self.assertLessEqual(offset + length, len(data))
            entries.add(kind)
            offset += length
        self.assertEqual(len(data), offset)
        self.assertIn(b"ic10", entries)  # 1024px Retina representation
