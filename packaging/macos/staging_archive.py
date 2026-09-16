"""Archive disposable Mac build bundles so Launch Services cannot discover old apps."""
import hashlib
from pathlib import Path
import plistlib
import shutil
import subprocess
import zipfile


def validated_staging_app(app, repository):
    app, repository = Path(app).absolute(), Path(repository).resolve()
    # Never target Applications, user data, arbitrary apps, or a symlinked parent.
    if app.resolve() != app or app.name != "SamsungController.app":
        raise ValueError("Not an ordinary SamsungController staging bundle.")
    relative = app.relative_to(repository / "artifacts")
    if (len(relative.parts) != 4 or relative.parts[0] not in ("package-builds", "package-builds.noindex")
            or not relative.parts[1].startswith(("osx-arm64-", "osx-x64-"))
            or relative.parts[2] != "SamsungController"):
        raise ValueError("Not a known Mac packaging path.")
    info = plistlib.loads((app / "Contents/Info.plist").read_bytes())
    if info.get("CFBundleIdentifier") != "com.whipstein.samsungcontroller":
        raise ValueError("Unexpected staging bundle identity.")
    return app


def archive_staging_app(app, repository):
    app = validated_staging_app(app, repository)
    return _archive_verified_bundle(app)


def _archive_verified_bundle(app):
    # Caller must first validate the exact disposable build/test path.
    destination = app.with_suffix(".app.zip")
    if destination.exists():
        raise ValueError("Refusing to overwrite an existing recovery archive: " + str(destination))
    subprocess.run(["ditto", "-c", "-k", "--sequesterRsrc", "--keepParent",
                    str(app), str(destination)], check=True)
    # Validate every source file and symlink against the archive before removing
    # the unpacked, regenerable build copy. ditto retains signature/stapled data.
    with zipfile.ZipFile(destination) as archive:
        if archive.testzip() is not None:
            raise ValueError("The recovery ZIP did not pass its integrity check.")
        for item in app.rglob("*"):
            name = str(item.relative_to(app.parent))
            if item.is_symlink():
                if archive.read(name).decode() != str(item.readlink()):
                    raise ValueError("Archived symlink differs: " + name)
            elif item.is_file():
                with archive.open(name) as saved, item.open("rb") as original:
                    if hashlib.file_digest(saved, "sha256").digest() != hashlib.file_digest(original, "sha256").digest():
                        raise ValueError("Archived file differs: " + name)
    shutil.rmtree(app)
    return destination
