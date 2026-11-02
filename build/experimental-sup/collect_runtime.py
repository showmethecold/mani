"""Collect the complete PE import closure for one experimental engine.

Only writes a new experiment runtime directory. System DLLs remain OS supplied.
All non-system DLLs come from this build prefix or the locked UCRT64 packages.
"""
from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[2]
EXP = ROOT / "artifacts/m5/sup-engine-integration"
UCRT = ROOT / "artifacts/m5/sup-engine-ab/tools/msys64/ucrt64"


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("variant", choices=("baseline", "patched"))
    args = parser.parse_args()
    prefix = EXP / ("prefix-" + args.variant)
    output = EXP / ("runtime-" + args.variant)
    if output.exists():
        raise SystemExit("Existing runtime preserved; choose a new experiment revision for replacements")
    bins = [prefix / "bin", UCRT / "bin"]
    lookup = {}
    for folder in reversed(bins):
        lookup.update({p.name.lower(): p for p in folder.iterdir() if p.is_file()})
    seeds = [prefix / "bin/libmpv-2.dll", prefix / "bin/mpv.exe"]
    # ANGLE is explicitly enabled and loaded dynamically; it may not appear in
    # libmpv's PE imports. Include it and its own imports as a feature dependency.
    for name in ("libEGL.dll", "libGLESv2.dll"):
        seeds.append(lookup[name.lower()])
    system = Path(os.environ["SystemRoot"]) / "System32"
    collected, os_imports, imports = {}, set(), {}
    while seeds:
        source = seeds.pop()
        key = source.name.lower()
        if key in collected:
            continue
        if not source.is_file():
            raise RuntimeError(f"Missing built input: {source}")
        result = subprocess.run([str(UCRT / "bin/objdump.exe"), "-p", str(source)],
            capture_output=True, text=True, check=True)
        names = re.findall(r"DLL Name:\s*(\S+)", result.stdout)
        imports[source.name] = names
        collected[key] = source
        for name in names:
            if name.lower() in lookup:
                seeds.append(lookup[name.lower()])
            elif (system / name).exists() or name.lower().startswith(("api-ms-win-", "ext-ms-")):
                os_imports.add(name)
            else:
                raise RuntimeError(f"Unresolved import {name} in {source.name}")
    output.mkdir()
    records = []
    for name, source in sorted(collected.items()):
        destination = output / source.name
        shutil.copy2(source, destination)
        records.append(dict(file=source.name, sha256=sha(destination), bytes=destination.stat().st_size,
            source=str(source.relative_to(ROOT)), builtFromSource=source.is_relative_to(prefix)))
    manifest = dict(variant=args.variant, scope="isolated experimental engine",
        dependencyLockSha256=sha(ROOT / "build/experimental-sup/dependencies.lock.json"),
        sourceIdentity=json.loads((EXP / "source-identity.json").read_text()),
        buildRecipeSha256=sha(ROOT / "build/experimental-sup/build-full-engine.sh"),
        files=records, systemImports=sorted(os_imports), imports=imports)
    (output / "runtime-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    (output / "mpv-runtime.json").write_text(json.dumps(dict(dll="libmpv-2.dll",
        sha256=sha(output / "libmpv-2.dll"), version="mpv v0.41.0-1205-g01e2533f3-mani-experiment",
        source_commit=manifest["sourceIdentity"]["mpvCommit"], experimental=True,
        variant=args.variant), indent=2), encoding="utf-8")
    print(f"Collected {len(records)} files into {output.name}")
    print("libmpv SHA256:", sha(output / "libmpv-2.dll"))


if __name__ == "__main__":
    main()
