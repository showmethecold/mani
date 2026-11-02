"""Record/verify the isolated MSYS2 package baseline; never change project locks."""
from pathlib import Path
import argparse
import hashlib
import json
import tarfile

ROOT = Path(__file__).resolve().parents[2]
EXP = ROOT / "artifacts/m5/sup-engine-integration"
MSYS = ROOT / "artifacts/m5/sup-engine-ab/tools/msys64"
LOCK = ROOT / "build/experimental-sup/dependencies.lock.json"


def fields(text):
    result = {}
    for chunk in text.strip().split("\n\n"):
        lines = chunk.splitlines()
        if lines and lines[0].startswith("%"):
            result[lines[0].strip("%")] = lines[1:]
    return result


def installed():
    return sorted((fields(p.read_text(encoding="utf-8")) for p in
        (MSYS / "var/lib/pacman/local").glob("*/desc")), key=lambda p: p["NAME"][0])


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--create", action="store_true")
    parser.add_argument("--baseline-id", default="mani-sup-msys2-ucrt64-20261101-r1")
    args = parser.parse_args()
    current = installed()
    if args.create:
        if LOCK.exists():
            raise SystemExit("Existing experiment lock preserved; do not silently re-resolve dependencies")
        repos = {}
        for name in ("ucrt64", "msys"):
            with tarfile.open(MSYS / f"var/lib/pacman/sync/{name}.db") as archive:
                for item in archive:
                    if item.name.endswith("/desc"):
                        record = fields(archive.extractfile(item).read().decode())
                        repos[(record["NAME"][0], record["VERSION"][0])] = (name, record)
        packages = []
        for item in current:
            name, version = item["NAME"][0], item["VERSION"][0]
            entry = dict(name=name, version=version, depends=item.get("DEPENDS", []))
            repo, record = repos.get((name, version), (None, None))
            cached = MSYS / "var/cache/pacman/pkg" / record["FILENAME"][0] if record else None
            if cached and cached.exists():
                sha = digest(cached)
                if sha != record["SHA256SUM"][0]:
                    raise RuntimeError(f"Archive differs from repository digest: {cached.name}")
                urlbase = "https://mirror.msys2.org/mingw/ucrt64/" if repo == "ucrt64" else "https://mirror.msys2.org/msys/x86_64/"
                entry.update(archive=str(cached.relative_to(ROOT)), sha256=sha,
                    url=urlbase+cached.name, bytes=cached.stat().st_size)
            elif name.startswith("mingw-w64-ucrt-"):
                raise RuntimeError(f"Native dependency archive unavailable: {name} {version}")
            else:
                entry["source"] = "verified portable MSYS2 base archive"
            packages.append(entry)
        value = dict(schema=1, baseline=args.baseline_id,
            scope="Experimental engines only; not the Mani runtime lock",
            sources=json.loads((EXP / "source-identity.json").read_text()),
            msysBase=json.loads((ROOT / "artifacts/m5/sup-engine-ab/downloads/msys2-tool-identity.json").read_text()),
            packages=packages)
        LOCK.write_text(json.dumps(value, indent=2), encoding="utf-8")
    value = json.loads(LOCK.read_text())
    base = ROOT / "artifacts/m5/sup-engine-ab/downloads/msys2-base.tar.xz"
    if digest(base) != value['msysBase']['sha256']:
        raise SystemExit('Portable MSYS2 base archive differs from the experimental lock')
    actual = [(i["NAME"][0], i["VERSION"][0]) for i in current]
    expected = [(i["name"], i["version"]) for i in value["packages"]]
    if actual != expected:
        raise SystemExit("Installed package versions differ from the experimental lock")
    for item in value["packages"]:
        if "archive" in item and digest(ROOT / item["archive"]) != item["sha256"]:
            raise SystemExit(f"Locked archive differs: {item['name']}")
    print(f"Verified {len(expected)} individually locked packages and their cached archives")


if __name__ == "__main__":
    main()
