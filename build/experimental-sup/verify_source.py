"""Fail closed if a component build would use the wrong A/B source variant."""
from pathlib import Path
import hashlib
import json
import sys

root = Path(__file__).resolve().parents[2] / "artifacts/m5/sup-engine-ab"
variant = sys.argv[1]
if variant not in ("baseline", "patched"):
    raise SystemExit("Expected baseline or patched")
source = root / ("src/ffmpeg-baseline" if variant == "baseline" else "src/ffmpeg")
build_link = root / ("component-" + variant) / "src"
if build_link.exists() and build_link.resolve() != source.resolve():
    raise SystemExit(f"Stale build source link; preserve this build and use a fresh directory: {build_link}")
identity = json.loads((root / "patch-identity.json").read_text())
for item in identity["files"]:
    actual = hashlib.sha256((source / item["file"]).read_bytes()).hexdigest()
    expected = item["originalSha256" if variant == "baseline" else "patchedSha256"]
    if actual != expected:
        raise SystemExit(f"{variant} source mismatch: {item['file']}")
print(f"Verified {variant} source: {identity['ffmpegCommit']}")
