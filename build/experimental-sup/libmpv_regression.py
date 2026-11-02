"""C1-C6 GPU regression harness, adapted from the preserved root-cause probe.
Direct libmpv API and D3D11 captures; does not test Mani's WinUI window.
Every run requires an explicit DLL hash and uses its own output directory.
No user configuration, network media, or audio output is used.
"""
from pathlib import Path
import argparse
import ctypes as C
import datetime
import hashlib
import json
import struct
import time
import zlib

ROOT = Path(__file__).resolve().parents[2]
EXP = ROOT / "artifacts/m5/sup-engine-ab"
LOCKED_DLL = "82bae3b6bf7bb737afc9b52d590b477983c715f3c63c1a361f9ee06523dc5dc2"

# ---------- SUP fixture helpers ----------
def segments(data):
    i, out = 0, []
    while i < len(data):
        assert data[i:i + 2] == b"PG"
        pts, dts = struct.unpack(">II", data[i + 2:i + 10])
        kind = data[i + 10]
        length = struct.unpack(">H", data[i + 11:i + 13])[0]
        out.append(dict(pts=pts, dts=dts, kind=kind, body=data[i + 10:i + 13 + length]))
        i += 13 + length
    return out


def build(segs):
    return b"".join(b"PG" + struct.pack(">II", s["pts"], s["dts"]) + s["body"] for s in segs)


def make_fixtures(media, out):
    original = segments((media / "original.sup").read_bytes())
    two = segments((media / "consistent-two-cues.sup").read_bytes())
    fixtures = {}
    # V1: one display set at pts 0, no clearing display set.
    fixtures["noclear-single.sup"] = build([s for s in original if s["pts"] == 0])
    # V2: display sets at 1 s (bottom, y=800) and 4 s (top, y=300); clears removed.
    fixtures["noclear-two.sup"] = build([s for s in two if s["pts"] in (90000, 360000)])
    # V3: same as consistent-two-cues but realistic distinct, non-zero DTS.
    offsets = {0x16: 9000, 0x17: 8500, 0x14: 8000, 0x15: 7000, 0x80: 100}
    fixtures["realdts-two.sup"] = build([dict(s, dts=s["pts"] - offsets[s["kind"]]) for s in two])
    for name, data in fixtures.items():
        (out / name).write_bytes(data)
    return {name: hashlib.sha256(data).hexdigest() for name, data in fixtures.items()}


# ---------- minimal PNG reader (8-bit RGB/RGBA, non-interlaced) ----------
def read_png(path):
    data = path.read_bytes()
    assert data[:8] == b"\x89PNG\r\n\x1a\n"
    i, idat, width = 8, b"", None
    while i < len(data):
        length = struct.unpack(">I", data[i:i + 4])[0]
        kind = data[i + 4:i + 8]
        body = data[i + 8:i + 8 + length]
        if kind == b"IHDR":
            width, height, depth, color, _, _, interlace = struct.unpack(">IIBBBBB", body)
            assert depth == 8 and color in (2, 6) and interlace == 0
            bpp = 3 if color == 2 else 4
        elif kind == b"IDAT":
            idat += body
        i += 12 + length
    raw = zlib.decompress(idat)
    stride = width * bpp
    rows, prev, pos = [], bytearray(stride), 0
    for _ in range(height):
        f = raw[pos]; line = bytearray(raw[pos + 1:pos + 1 + stride]); pos += 1 + stride
        for x in range(stride):
            a = line[x - bpp] if x >= bpp else 0
            b = prev[x]
            c = prev[x - bpp] if x >= bpp else 0
            if f == 1: line[x] = (line[x] + a) & 255
            elif f == 2: line[x] = (line[x] + b) & 255
            elif f == 3: line[x] = (line[x] + ((a + b) >> 1)) & 255
            elif f == 4:
                p = a + b - c; pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[x] = (line[x] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        rows.append(bytes(line)); prev = line
    return width, height, bpp, rows


def compare(shown, hidden):
    w, h, bpp, a = read_png(shown)
    w2, h2, bpp2, b = read_png(hidden)
    assert (w, h, bpp) == (w2, h2, bpp2)
    count, ys = 0, []
    for y in range(h):
        ra, rb = a[y], b[y]
        if ra == rb:
            continue
        for x in range(w):
            o = x * bpp
            if max(abs(ra[o] - rb[o]), abs(ra[o + 1] - rb[o + 1]), abs(ra[o + 2] - rb[o + 2])) > 20:
                count += 1; ys.append(y)
    if not count:
        return dict(changed=0, where="none")
    centre = (min(ys) + max(ys)) / 2
    return dict(changed=count, yMin=min(ys), yMax=max(ys),
                where="bottom" if centre > h / 2 else "top")


# ---------- libmpv probe (same call sequence as reproduce.py) ----------
def background(path):
    frame = bytes([80]) * (160 * 90) + bytes([128]) * (160 * 90 // 2)
    with path.open("wb") as f:
        f.write(b"YUV4MPEG2 W160 H90 F10:1 Ip A1:1 C420jpeg\n")
        for _ in range(80):
            f.write(b"FRAME\n" + frame)


def probe(dll, video, subtitle, out, steps, extra):
    out.mkdir()
    def bind(name, result, *args):
        fn = getattr(dll, name); fn.restype, fn.argtypes = result, args; return fn
    create = bind("mpv_create", C.c_void_p)
    initialize = bind("mpv_initialize", C.c_int, C.c_void_p)
    option = bind("mpv_set_option_string", C.c_int, C.c_void_p, C.c_char_p, C.c_char_p)
    get = bind("mpv_get_property_string", C.c_void_p, C.c_void_p, C.c_char_p)
    setp = bind("mpv_set_property_string", C.c_int, C.c_void_p, C.c_char_p, C.c_char_p)
    command = bind("mpv_command", C.c_int, C.c_void_p, C.POINTER(C.c_char_p))
    free = bind("mpv_free", None, C.c_void_p)
    destroy = bind("mpv_terminate_destroy", None, C.c_void_p)
    error = bind("mpv_error_string", C.c_char_p, C.c_int)
    h = create()
    results = []
    def check(code, label):
        if code < 0: raise RuntimeError(f"{label}: {error(code).decode()} ({code})")
    def prop(name):
        ptr = get(h, name.encode())
        if not ptr: return None
        try: return C.string_at(ptr).decode("utf-8")
        finally: free(ptr)
    def setv(name, value): check(setp(h, name.encode(), str(value).encode()), name)
    def cmd(*args):
        argv = (C.c_char_p * (len(args) + 1))(*[str(a).encode("utf-8") for a in args], None)
        check(command(h, argv), str(args[0]))
    def wait(predicate, label, seconds=10):
        end = time.monotonic() + seconds
        while time.monotonic() < end:
            if predicate(): return
            time.sleep(.03)
        raise RuntimeError(f"Timeout: {label}")
    def capture(name):
        start = float(prop("time-pos"))
        setv("sub-visibility", "yes"); time.sleep(.25)
        cmd("screenshot-to-file", out / (name + ".png"), "window")
        setv("sub-visibility", "no"); time.sleep(.25)
        cmd("screenshot-to-file", out / (name + "-hidden.png"), "window")
        assert abs(float(prop("time-pos")) - start) < .01 and prop("pause") == "yes"
        setv("sub-visibility", "yes")
        r = dict(step=name, pos=prop("time-pos"), sid=prop("sid"),
                 **compare(out / (name + ".png"), out / (name + "-hidden.png")))
        results.append(r); print("  ", r, flush=True)
    options = {
        "config": "no", "load-scripts": "no", "load-auto-profiles": "no",
        "terminal": "no", "osc": "no", "ao": "null", "vo": "gpu-next",
        "gpu-api": "d3d11", "gpu-context": "d3d11",
        "d3d11-output-mode": "composition", "d3d11-composition-size": "960x540",
        "keep-open": "yes", "pause": "yes", "sub-auto": "no", "sid": "no",
        "secondary-sid": "no", "screenshot-format": "png",
        "screenshot-high-bit-depth": "no", "log-file": str(out / "mpv.log"),
        "msg-level": "all=warn,cplayer=debug,lavf=trace,sub=trace,demux=debug,ffmpeg=debug",
    }
    options.update(extra)
    try:
        for k, v in options.items(): check(option(h, k.encode(), v.encode()), k)
        check(initialize(h), "mpv_initialize")
        (out / "versions.json").write_text(json.dumps({key: prop(key) for key in
            ("mpv-version", "ffmpeg-version", "libplacebo-version", "mpv-configuration")}, indent=2), encoding="utf-8")
        cmd("loadfile", video); wait(lambda: prop("time-pos") is not None, "video")
        cmd("sub-add", subtitle, "select"); wait(lambda: prop("sid") not in (None, "no", "auto"), "sid")
        setv("pause", "no"); wait(lambda: float(prop("time-pos") or 0) >= 2.2, "play"); setv("pause", "yes")
        time.sleep(.2); capture("continuous-2.2")
        for index, target in enumerate(steps, 1):
            cmd("seek", target, "absolute+exact")
            wait(lambda: prop("seeking") == "no" and abs(float(prop("time-pos") or -100) - target) < .06, f"seek {target}")
            time.sleep(.4); capture(f"seek-{index}-{target}")
    finally:
        destroy(h)
    (out / "results.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", required=True, type=Path)
    parser.add_argument("--expect-sha256", required=True)
    parser.add_argument("--mode", required=True, choices=("baseline", "control", "candidate"))
    parser.add_argument("--output-root", type=Path, default=EXP / "libmpv-runs")
    args = parser.parse_args()
    dll_path = args.dll.resolve(strict=True)
    digest = hashlib.sha256(dll_path.read_bytes()).hexdigest()
    if digest != args.expect_sha256.lower():
        raise SystemExit("DLL identity mismatch; no engine loaded")
    if (digest == LOCKED_DLL) != (args.mode == "baseline"):
        raise SystemExit("Baseline must use the locked DLL; control/candidate must use a distinct experimental DLL")
    run = args.output_root.resolve() / (args.mode + "-" + datetime.datetime.now().strftime("%Y%m%d-%H%M%S-%f"))
    run.mkdir(parents=True)
    media = ROOT / "artifacts/m5/sup-seek-research/media"
    fixtures = make_fixtures(media, run)
    for name in ("consistent-two-cues.sup", "consistent-two-cues.mks"):
        fixtures[name] = hashlib.sha256((media / name).read_bytes()).hexdigest()
    video = run / "background.y4m"
    background(video)
    dll = C.CDLL(str(dll_path))
    cache = {"cache": "yes", "demuxer-seekable-cache": "yes"}
    cases = [
        ("C1-consistent-sup", media / "consistent-two-cues.sup", [2, 5, 2], {}),
        ("C2-noclear-single", run / "noclear-single.sup", [2, 5], {}),
        ("C3-noclear-two", run / "noclear-two.sup", [2, 5, 2], {}),
        ("C4-noclear-two-cache", run / "noclear-two.sup", [2, 5, 2], cache),
        ("C5-realdts-two", run / "realdts-two.sup", [2, 5, 2], {}),
        ("C6-consistent-mks", media / "consistent-two-cues.mks", [2, 5, 2], {}),
    ]
    summary = dict(scope="C1-C6 direct libmpv D3D11 pixels; not Mani window acceptance",
        mode=args.mode, dllPath=str(dll_path), dllSha256=digest, fixtures=fixtures, cases={})
    for name, subtitle, steps, extra in cases:
        print(name, flush=True)
        results = probe(dll, video, subtitle, run / name, steps, extra)
        for result in results:
            result["expected"] = "bottom" if name.startswith("C2-") or float(result["pos"]) < 4 else "top"
            result["correct"] = result["where"] == result["expected"]
        summary["cases"][name] = results
        (run / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    cache_log = (run / "C4-noclear-two-cache/mpv.log").read_text(encoding="utf-8", errors="replace")
    summary["cacheSeekObserved"] = "using this range for in-cache seek" in cache_log
    summary["correctCaptures"] = sum(r["correct"] for rs in summary["cases"].values() for r in rs)
    summary["totalCaptures"] = sum(len(rs) for rs in summary["cases"].values())
    summary["repairPassed"] = summary["cacheSeekObserved"] and summary["correctCaptures"] == summary["totalCaptures"]
    manifest_path = dll_path.with_name("runtime-manifest.json")
    if not manifest_path.exists():
        manifest_path = dll_path.with_name("mpv-runtime.json")
    if manifest_path.exists():
        manifest = json.loads(manifest_path.read_text())
        versions = [json.loads((run / name / 'versions.json').read_text()) for name, *_ in cases]
        summary['sourceVersionVerified'] = all(
            v['ffmpeg-version'] == 'N-127617-g94cba3150-mani-experiment' and
            v['mpv-version'] == 'mpv v0.41.0-1205-g01e2533f3-mani-experiment'
            for v in versions)
        kernel = C.WinDLL("kernel32", use_last_error=True)
        kernel.GetModuleHandleW.argtypes = [C.c_wchar_p]
        kernel.GetModuleHandleW.restype = C.c_void_p
        kernel.GetModuleFileNameW.argtypes = [C.c_void_p, C.c_wchar_p, C.c_uint]
        kernel.GetModuleFileNameW.restype = C.c_uint
        loaded = []
        for item in manifest["files"]:
            if not item["file"].lower().endswith(".dll"):
                continue
            handle = kernel.GetModuleHandleW(item["file"])
            if not handle:
                continue
            buffer = C.create_unicode_buffer(32768)
            if not kernel.GetModuleFileNameW(handle, buffer, len(buffer)):
                raise OSError(C.get_last_error(), "Cannot identify loaded engine dependency")
            actual = Path(buffer.value).resolve()
            actual_sha = hashlib.sha256(actual.read_bytes()).hexdigest()
            correct = actual.parent == dll_path.parent and actual_sha == item["sha256"]
            loaded.append(dict(file=item["file"], loadedPath=str(actual), sha256=actual_sha, matches=correct))
        summary["loadedEngineModules"] = loaded
        summary["runtimeClosureVerified"] = bool(loaded) and all(row["matches"] for row in loaded)
        summary["repairPassed"] &= summary["runtimeClosureVerified"] and summary['sourceVersionVerified']
        if not summary['runtimeClosureVerified'] or not summary['sourceVersionVerified']:
            (run / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
            raise SystemExit('Experimental engine identity verification failed')
    (run / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print("RUN:", run, flush=True)
    print(f"Correct captures: {summary['correctCaptures']}/{summary['totalCaptures']}; cache path observed: {summary['cacheSeekObserved']}")
    if args.mode == "candidate" and not summary["repairPassed"]:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
