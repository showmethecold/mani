"""Prepare isolated, repeatable source variants from the fixed downloaded ZIP.

Does not download, overwrite modified source, change a lock, or install a DLL.
Existing trees must match the archive plus the explicitly generated patch.
"""
from pathlib import Path, PurePosixPath
import hashlib
import json
import subprocess
import zipfile

import prepare_patch

ROOT = Path(__file__).resolve().parents[2]
EXP = ROOT / "artifacts/m5/sup-engine-ab"
ARCHIVE_SHA = "fcfbb4eb4f39fe668f8c421c7faec154e8a7651dc67ce96f0e00c128d38ec0d2"


def prepare(archive, destination, changes=None):
    changes = changes or {}
    existed = destination.exists()
    count = 0
    for item in archive.infolist():
        if item.is_dir():
            continue
        parts = PurePosixPath(item.filename).parts
        if len(parts) < 2 or ".." in parts or PurePosixPath(item.filename).is_absolute():
            raise RuntimeError(f"Unsafe archive entry: {item.filename}")
        relative = "/".join(parts[1:])
        target = destination / relative
        contents = archive.read(item)
        if existed:
            expected = changes.get(relative, hashlib.sha256(contents).hexdigest())
            if not target.is_file() or hashlib.sha256(target.read_bytes()).hexdigest() != expected:
                raise RuntimeError(f"Existing source differs; preserved without overwrite: {target}")
        else:
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(contents)
        count += 1
    print(f"{'Verified' if existed else 'Extracted'} {count} files: {destination.name}")
    return existed


def main():
    archive_path = EXP / "downloads/ffmpeg.zip"
    if hashlib.sha256(archive_path.read_bytes()).hexdigest() != ARCHIVE_SHA:
        raise SystemExit("Archive identity differs from the recorded fixed source")
    with zipfile.ZipFile(archive_path) as archive:
        prepare(archive, EXP / "src/ffmpeg-baseline")
        prepare_patch.main()
        identity = json.loads((EXP / "patch-identity.json").read_text())
        changes = {i["file"]: i["patchedSha256"] for i in identity["files"]}
        already_patched = prepare(archive, EXP / "src/ffmpeg", changes)
        if not already_patched:
            for check in (["--check"], []):
                subprocess.run(["git", "-c", "core.autocrlf=false", "apply", *check,
                    "--directory=artifacts/m5/sup-engine-ab/src/ffmpeg",
                    "build/experimental-sup/ffmpeg-sup-keyframes-flush.patch"], cwd=ROOT, check=True)
    print("Source variants ready; product DLLs and lock records unchanged.")


if __name__ == "__main__":
    main()
