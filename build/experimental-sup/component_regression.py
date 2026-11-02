"""Exercise actual fixed FFmpeg APIs, separately from libmpv/UI acceptance."""
from pathlib import Path
import hashlib
import json
import struct
import subprocess

ROOT = Path(__file__).resolve().parents[2]
EXP = ROOT / "artifacts/m5/sup-engine-ab"
MEDIA = ROOT / "artifacts/m5/sup-seek-research/media"


def parts(data):
    output = []
    cursor = 0
    while cursor < len(data):
        assert data[cursor:cursor+2] == b"PG"
        pts, dts = struct.unpack(">II", data[cursor+2:cursor+10])
        kind = data[cursor+10]
        length = int.from_bytes(data[cursor+11:cursor+13], "big")
        body = data[cursor+13:cursor+13+length]
        assert len(body) == length
        output.append((pts, dts, kind, body))
        cursor += 13 + length
    return output


def stream(rows):
    return b"".join(b"PG" + struct.pack(">IIBH", pts, dts, kind, len(body)) + body
                    for pts, dts, kind, body in rows)


def fixtures():
    dest = EXP / "component-fixtures"
    dest.mkdir(exist_ok=True)
    original = (MEDIA / "original.sup").read_bytes()
    two = (MEDIA / "consistent-two-cues.sup").read_bytes()
    rows = parts(two)
    cases = {
        "two-clear.sup": two,
        "original.sup": original,
        "single-noclear.sup": stream([r for r in parts(original) if r[0] == 0]),
        "two-noclear.sup": stream([r for r in rows if r[0] in (90000, 360000)]),
        "two-clear.mks": (MEDIA / "consistent-two-cues.mks").read_bytes(),
    }
    delta = {0x16: 9000, 0x17: 8500, 0x14: 8000, 0x15: 7000, 0x80: 100}
    cases["distinct-dts.sup"] = stream([(p, p-delta[k], k, b) for p, d, k, b in rows])
    acquisition, normal, fragmented, multiple = [], [], [], []
    for pts, dts, kind, body in rows:
        acq = bytearray(body)
        if kind == 0x16 and acq[7] == 0x80:
            acq[7] = 0x40
        acquisition.append((pts, dts, kind, bytes(acq)))
        norm = bytearray(body)
        if pts == 360000 and kind == 0x16:
            norm[7] = 0
        if not (pts == 360000 and kind in (0x14, 0x15)):
            normal.append((pts, dts, kind, bytes(norm)))
        if kind == 0x15:
            split = 11 + (len(body)-11)//2
            first = bytearray(body[:split])
            first[3] = 0x80
            fragmented.extend([
                (pts, dts, kind, bytes(first)),
                (pts, dts, kind, body[:3] + b"\x40" + body[split:]),
            ])
        else:
            fragmented.append((pts, dts, kind, body))
        multi = bytearray(body)
        if kind == 0x16 and body[10] == 1:
            multi[10] = 2
            second = bytearray(body[11:19])
            second[:2] = b"\0\1"
            second[4:6] = (1150).to_bytes(2, "big")
            multi.extend(second)
        elif kind == 0x17:
            multi[6:8] = (510).to_bytes(2, "big")
        multiple.append((pts, dts, kind, bytes(multi)))
        if kind == 0x15:
            duplicate = bytearray(body)
            duplicate[:2] = b"\0\1"
            multiple.append((pts, dts, kind, bytes(duplicate)))
    cases.update({
        "acquisition.sup": stream(acquisition),
        "normal-reuse.sup": stream(normal),
        "fragmented-ods.sup": stream(fragmented),
        "multiple-objects.sup": stream(multiple),
        "short-pcs.sup": stream([(0, 0, 0x16, b"\0"*4)]),
        "truncated-ods.sup": original[:1000],
        "other-subtitle.srt": b"1\n00:00:01,000 --> 00:00:03,000\nFIRST\n\n2\n00:00:04,000 --> 00:00:07,000\nSECOND\n",
    })
    for name, data in cases.items():
        (dest / name).write_bytes(data)
    (dest / "manifest.json").write_text(json.dumps({
        name: hashlib.sha256(data).hexdigest() for name, data in cases.items()
    }, indent=2), encoding="utf-8")
    return dest, cases


def main():
    dest, cases = fixtures()
    logdir = EXP / "component-results"
    logdir.mkdir(exist_ok=True)
    results = {}
    binaries = {}
    for variant in ("baseline", "patched"):
        binary = EXP / ("component-" + variant) / "component-probe.exe"
        if not binary.exists():
            raise RuntimeError(f"Missing {binary}; compile both variants first")
        binaries[variant] = dict(path=str(binary.relative_to(ROOT)),
            sha256=hashlib.sha256(binary.read_bytes()).hexdigest())
        results[variant] = {}
        for name in cases:
            proc = subprocess.run([str(binary), str(dest / name)], capture_output=True,
                                  text=True, timeout=20)
            (logdir / (variant + "-" + name + ".jsonl")).write_text(proc.stdout, encoding="utf-8")
            (logdir / (variant + "-" + name + ".stderr")).write_text(proc.stderr, encoding="utf-8")
            results[variant][name] = dict(exit=proc.returncode,
                events=[json.loads(line) for line in proc.stdout.splitlines() if line.startswith("{")])
    checks = []

    def check(name, value):
        checks.append(dict(name=name, passed=bool(value)))
        print(("PASS " if value else "FAIL ") + name)

    def events(variant, case, event):
        return [e for e in results[variant][case]["events"] if e["event"] == event]

    for name in cases:
        if name in ("short-pcs.sup", "truncated-ods.sup"):
            check(name + ": malformed stream rejected",
                  results["patched"][name]["exit"] != 0 or bool(events("patched", name, "error")))
            continue
        check(name + ": probe completes", results["patched"][name]["exit"] == 0)
        check(name + ": no API/decode errors", not events("patched", name, "error"))
        if name.endswith(".sup"):
            pkts = events("patched", name, "packet")
            # Check the observable API result after the generic demux layer.
            check(name + ": END/objects/palette are not random access points",
                  bool(pkts) and all(not p["key"] for p in pkts if p["type"] != 0x16))
            expected_keys = 1 if name in ("original.sup", "single-noclear.sup", "normal-reuse.sup") else 2
            check(name + ": correct number of independent starts",
                  sum(p["key"] for p in pkts) == expected_keys)
            starts = events("patched", name, "seek-start")
            check(name + ": seeks restart at PCS", len(starts) == 5 and all(p["type"] == 0x16 for p in starts))
        if name == "other-subtitle.srt":
            check(name + ": unaffected format preserves baseline output",
                  results["baseline"][name] == results["patched"][name])
            continue
        initial = [e for e in events("baseline", name, "subtitle") if e["phase"] == "initial"]
        patched_initial = [e for e in events("patched", name, "subtitle") if e["phase"] == "initial"]
        check(name + ": continuous decoding preserves all baseline output", initial == patched_initial)
        for step, target in enumerate((2, 5, 2, 3.5, 7.5), 1):
            subs = [e for e in events("patched", name, "subtitle") if e["phase"] == "seek" and e["step"] == step]
            last = subs[-1] if subs else None
            single = name in ("original.sup", "single-noclear.sup")
            no_clear = name in ("single-noclear.sup", "two-noclear.sup")
            visible = no_clear or (target < 7 if single else (target < 3 or 4 <= target < 7))
            y = 800 if single or target < 3 or (no_clear and target < 4) else 300
            rects = 2 if name == "multiple-objects.sup" else 1
            check(f"{name}: seek {step} at {target} reconstructs correct state",
                  last is not None and (last["rects"] == rects and last["y"] == y
                                        if visible else last["rects"] == 0))
            expected_frames = [e for e in initial if e["pts"] <= target * 1000000]
            expected_frame = expected_frames[-1] if expected_frames else None
            check(f"{name}: seek {step} preserves decoded bitmap and palette",
                  last is not None and expected_frame is not None and
                  last["bitmapDigest"] == expected_frame["bitmapDigest"])
    for name in ("single-noclear.sup", "two-noclear.sup"):
        old = [e for e in events("baseline", name, "subtitle") if e["phase"] == "flush-end"]
        new = [e for e in events("patched", name, "subtitle") if e["phase"] == "flush-end"]
        check(name + ": old END reuses stale image", old and old[-1]["rects"] > 0)
        check(name + ": patched flush prevents stale image", new and new[-1]["rects"] == 0)
    check("baseline exposes the key-flag defect",
          any(p["key"] for p in events("baseline", "two-clear.sup", "packet") if p["type"] == 0x80))
    report = dict(scope="Actual FFmpeg components only; no libmpv cache/GPU/window claim",
                  binaries=binaries,
                  source=json.loads((EXP / "patch-identity.json").read_text()),
                  checks=checks, passed=sum(c["passed"] for c in checks), total=len(checks))
    (logdir / "summary.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    if not all(c["passed"] for c in checks):
        raise SystemExit(1)


if __name__ == "__main__":
    main()
