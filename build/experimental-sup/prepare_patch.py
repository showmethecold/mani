"""Generate the narrow A+B patch against the locked FFmpeg source.
No engine/config files are replaced. Source must be the pinned upstream archive.
"""
from pathlib import Path
import difflib
import hashlib
import json

ROOT = Path(__file__).resolve().parents[2]
EXP = ROOT / "artifacts/m5/sup-engine-ab"
SRC = EXP / "src/ffmpeg-baseline"
PATCH = Path(__file__).with_name("ffmpeg-sup-keyframes-flush.patch")
COMMIT = "94cba3150c6b83057ed48b833689adc806715cc0"


def once(text, before, after):
    if text.count(before) != 1:
        raise RuntimeError("Source context differs from the fixed upstream revision")
    return text.replace(before, after, 1)


def main():
    files = ("libavformat/demux.h", "libavformat/demux.c",
             "libavformat/supdec.c", "libavcodec/pgssubdec.c")
    originals = {name: (SRC / name).read_text(encoding="utf-8") for name in files}
    changed = dict(originals)
    name = "libavformat/demux.h"
    changed[name] = once(changed[name],
        "#define FF_INFMT_FLAG_ID3V2_AUTO                               (1 << 2)\n",
        "#define FF_INFMT_FLAG_ID3V2_AUTO                               (1 << 2)\n\n"
        "/**\n"
        " * Preserve demuxer key flags for packets that are not complete frames.\n"
        " * In particular, a raw PGS segment is not necessarily a random access point.\n"
        " */\n"
        "#define FF_INFMT_FLAG_PRESERVE_KEY_FLAGS                       (1 << 3)\n")
    name = "libavformat/demux.c"
    changed[name] = once(changed[name],
        "    if (st->codecpar->codec_type == AVMEDIA_TYPE_DATA || ff_is_intra_only(st->codecpar->codec_id))\n"
        "        pkt->flags |= AV_PKT_FLAG_KEY;\n",
        "    if (!(ffifmt(s->iformat)->flags_internal & FF_INFMT_FLAG_PRESERVE_KEY_FLAGS) &&\n"
        "        (st->codecpar->codec_type == AVMEDIA_TYPE_DATA || ff_is_intra_only(st->codecpar->codec_id)))\n"
        "        pkt->flags |= AV_PKT_FLAG_KEY;\n")
    name = "libavformat/supdec.c"
    changed[name] = once(changed[name], "    pkt->flags |= AV_PKT_FLAG_KEY;\n", "")
    changed[name] = once(changed[name],
        "    if (pkt->size >= 3) {\n",
        "    if (pkt->size != 3)\n"
        "        return AVERROR_INVALIDDATA;\n\n"
        "    {\n")
    changed[name] = once(changed[name],
        "        if ((ret = av_append_packet(s->pb, pkt, len)) < 0)\n"
        "            return ret;\n",
        "        if ((ret = av_append_packet(s->pb, pkt, len)) < 0)\n"
        "            return ret;\n"
        "        if (pkt->size != len + 3)\n"
        "            return AVERROR_INVALIDDATA;\n\n"
        "        if (pkt->data[0] == 0x16) { // Presentation Composition Segment\n"
        "            unsigned state;\n"
        "            if (len < 11)\n"
        "                return AVERROR_INVALIDDATA;\n"
        "            state = pkt->data[10] & 0xc0;\n"
        "            // Epoch start / acquisition point includes all required state.\n"
        "            // Normal updates and END segments cannot be decoded independently.\n"
        "            if (state == 0x80 || state == 0x40)\n"
        "                pkt->flags |= AV_PKT_FLAG_KEY;\n"
        "        }\n")
    changed[name] = once(changed[name],
        "    .p.flags        = AVFMT_GENERIC_INDEX,\n",
        "    .p.flags        = AVFMT_GENERIC_INDEX,\n"
        "    .flags_internal = FF_INFMT_FLAG_PRESERVE_KEY_FLAGS,\n")
    name = "libavcodec/pgssubdec.c"
    changed[name] = once(changed[name],
        "static av_cold int close_decoder(AVCodecContext *avctx)\n",
        "static void flush_decoder(AVCodecContext *avctx)\n"
        "{\n"
        "    PGSSubContext *ctx = avctx->priv_data;\n\n"
        "    flush_cache(avctx);\n"
        "    memset(&ctx->presentation, 0, sizeof(ctx->presentation));\n"
        "    ctx->presentation.pts = AV_NOPTS_VALUE;\n"
        "}\n\n"
        "static av_cold int close_decoder(AVCodecContext *avctx)\n")
    changed[name] = once(changed[name],
        "    .close          = close_decoder,\n",
        "    .close          = close_decoder,\n"
        "    .flush          = flush_decoder,\n")
    patch = "".join("".join(difflib.unified_diff(
        originals[name].splitlines(True), changed[name].splitlines(True),
        fromfile="a/" + name, tofile="b/" + name)) for name in files)
    PATCH.write_text(patch, encoding="utf-8", newline="\n")
    manifest = {
        "ffmpegCommit": COMMIT,
        "patchSha256": hashlib.sha256(PATCH.read_bytes()).hexdigest(),
        "files": [{
            "file": name,
            "originalSha256": hashlib.sha256(originals[name].encode()).hexdigest(),
            "patchedSha256": hashlib.sha256(changed[name].encode()).hexdigest(),
        } for name in files],
        "scope": "SUP-specific preservation of key flags plus PGS decoder flush; no mpv or other dependency changes",
    }
    (EXP / "patch-identity.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(PATCH)


if __name__ == "__main__":
    main()
