import importlib.util
from pathlib import Path
import struct
import unittest

spec = importlib.util.spec_from_file_location("identity", Path(__file__).resolve().parents[1] / "macos/apphost_identity.py")
identity = importlib.util.module_from_spec(spec)
spec.loader.exec_module(identity)


class ApphostIdentityTests(unittest.TestCase):
    def image(self):
        return struct.pack("<8I", 0xFEEDFACF, 0x0100000C, 0, 2, 1, 24, 0, 0) + struct.pack("<II", 0x1B, 24) + bytes(16) + b"host payload"

    def test_distinct_executables_and_changed_assemblies_have_distinct_ids(self):
        data = self.image()
        offset = identity.uuid_offset(data)
        variants = [identity.unique_apphost(data, name, assembly) for name, assembly in
                    [("App", b"one"), ("Web", b"one"), ("App", b"two")]]
        self.assertEqual(3, len({bytes(item[offset:offset + 16]) for item in variants}))
        for item in variants:
            self.assertEqual(data[:offset], item[:offset])
            self.assertEqual(data[offset + 16:], item[offset + 16:])

    def test_identity_is_repeatable_and_ignores_previous_uuid(self):
        first = identity.unique_apphost(self.image(), "App", b"assembly")
        self.assertEqual(first, identity.unique_apphost(first, "App", b"assembly"))

    def test_rejects_invalid_non_executable_and_missing_uuid_images(self):
        bad = bytearray(self.image())
        struct.pack_into("<I", bad, 12, 6)
        for data in [b"", b"not Mach-O", self.image()[:40], bad,
                     struct.pack("<8I", 0xFEEDFACF, 0, 0, 2, 0, 0, 0, 0)]:
            with self.subTest(data=data), self.assertRaises(ValueError):
                identity.uuid_offset(data)
