"""Give published .NET apphosts distinct Mach-O UUIDs before final signing.

The SDK copies a common apphost template, including LC_UUID. macOS Local
Network privacy uses this UUID; sharing it between applications is unsafe.
Only our three known, thin 64-bit executable outputs are processed.
"""
import hashlib
from pathlib import Path
import struct
import subprocess
import uuid


def uuid_offset(data):
    if len(data) < 32 or struct.unpack_from("<I", data)[0] != 0xFEEDFACF:
        raise ValueError("Expected a thin little-endian 64-bit Mach-O")
    if struct.unpack_from("<I", data, 12)[0] != 2:
        raise ValueError("Expected MH_EXECUTE, not a library")
    count, size = struct.unpack_from("<II", data, 16)
    end = 32 + size
    if end > len(data):
        raise ValueError("Truncated load commands")
    offset, found = 32, []
    for _ in range(count):
        if offset + 8 > end:
            raise ValueError("Truncated load command")
        command, length = struct.unpack_from("<II", data, offset)
        if length < 8 or offset + length > end:
            raise ValueError("Invalid load command length")
        if command == 0x1B:  # LC_UUID
            if length != 24:
                raise ValueError("Invalid LC_UUID size")
            found.append(offset + 8)
        offset += length
    if offset != end or len(found) != 1:
        raise ValueError("Expected exactly one LC_UUID")
    return found[0]


def unique_apphost(data, identity, assembly):
    data = bytearray(data)
    offset = uuid_offset(data)
    data[offset:offset + 16] = bytes(16)
    digest = hashlib.sha256(data + assembly).hexdigest()
    identifier = uuid.uuid5(uuid.NAMESPACE_URL, "https://github.com/whipstein/SamsungController/" + identity + "/" + digest)
    data[offset:offset + 16] = identifier.bytes
    return data


def prepare_apphosts(payload):
    for name, assembly in [("SamsungController.App", "SamsungController.App.dll"),
                           ("SamsungController.Web", "SamsungController.Web.dll"),
                           ("samsungctl", "samsungctl.dll")]:
        executable = Path(payload) / name
        subprocess.run(["codesign", "--remove-signature", str(executable)], check=True)
        executable.write_bytes(unique_apphost(executable.read_bytes(), name, (Path(payload) / assembly).read_bytes()))
        # Required for local ARM64 smoke testing. The release signing pass replaces
        # this ad-hoc signature with Developer ID and the runtime entitlements.
        subprocess.run(["codesign", "--force", "--sign", "-", str(executable)], check=True)
