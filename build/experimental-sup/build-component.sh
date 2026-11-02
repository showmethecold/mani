#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
variant="${1:?Pass baseline or patched}"
case "$variant" in baseline|patched) ;; *) exit 2;; esac
experiment="$repo_root/artifacts/m5/sup-engine-ab"
source_root="$experiment/src/ffmpeg"
if [ "$variant" = baseline ]; then
    source_root="$experiment/src/ffmpeg-baseline"
fi
python "$repo_root/build/experimental-sup/verify_source.py" "$variant"
output="$experiment/component-$variant"
mkdir -p "$output" "$experiment/tmp"
export TMPDIR="$experiment/tmp"
cd "$output"
# Component test only: this is not a replacement libmpv.
if [ ! -f ffbuild/config.mak ]; then
    "$source_root/configure" --toolchain=msvc --arch=x86_64 \
        --disable-autodetect --disable-everything --disable-x86asm \
        --disable-programs --disable-network --disable-doc \
        --disable-avdevice --disable-avfilter --disable-swscale --disable-swresample \
        --enable-avutil --enable-avcodec --enable-avformat \
        --enable-decoder=pgssub,subrip,pcm_s16le \
        --enable-demuxer=sup,matroska,srt,wav --enable-protocol=file \
        --enable-static --disable-shared --extra-cflags=-experimental:c11atomics
fi
make -j4
cl.exe -nologo -MT -O2 -experimental:c11atomics -I"$(cygpath -w "$source_root")" -I"$(cygpath -w "$output")" \
    "$(cygpath -w "$repo_root/build/experimental-sup/component-probe.c")" \
    -Fe:component-probe.exe -link \
    libavformat/avformat.lib libavcodec/avcodec.lib libavutil/avutil.lib \
    bcrypt.lib advapi32.lib user32.lib
