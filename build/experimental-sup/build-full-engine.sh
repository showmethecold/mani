#!/usr/bin/env bash
set -euo pipefail
export PATH="/ucrt64/bin:/usr/bin:$PATH"
repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
variant="${1:?baseline or patched}"
case "$variant" in baseline|patched) ;; *) exit 2;; esac
export MSYSTEM=UCRT64
export CC=gcc CXX=g++
experiment="$repo_root/artifacts/m5/sup-engine-integration"
source_root="$repo_root/artifacts/m5/sup-engine-ab/src/ffmpeg"
if [ "$variant" = baseline ]; then source_root="$source_root-baseline"; fi
python "$repo_root/build/experimental-sup/verify_source.py" "$variant"
python "$repo_root/build/experimental-sup/verify_full_sources.py"
python "$repo_root/build/experimental-sup/lock_environment.py"
prefix="$experiment/prefix-$variant"
ffbuild="$experiment/ffmpeg-$variant"
mpbuild="$experiment/mpv-$variant"
mkdir -p "$prefix" "$ffbuild" "$experiment/tmp-$variant"
export TMPDIR="$experiment/tmp-$variant"
export PKG_CONFIG_PATH="$prefix/lib/pkgconfig:/ucrt64/lib/pkgconfig"
# The downloaded source archive has no .git. Never derive FFmpeg's identity
# from Mani's enclosing repository. Both control and patched builds use this.
export revision=N-127617-g94cba3150
ffmpeg_options=(
  --prefix="$(cygpath -m "$prefix")" --arch=x86_64 --target-os=mingw32
  --cc=gcc --cxx=g++ --pkg-config=pkg-config
  --enable-gpl --enable-version3 --enable-shared --disable-static
  --disable-programs --disable-doc --disable-debug --disable-autodetect
  --enable-runtime-cpudetect --extra-version=mani-experiment
  --extra-libs=-liconv
  --enable-network --enable-openssl
  --enable-iconv --enable-zlib --enable-bzlib --enable-lzma
  --enable-libass --enable-libfontconfig --enable-libfreetype
  --enable-libfribidi --enable-libharfbuzz
  --enable-libdav1d --enable-libaom --enable-libvpx
  --enable-libx264 --enable-libx265 --enable-libwebp --enable-libjxl
  --enable-libopus --enable-libvorbis --enable-libmp3lame
  --enable-libsoxr --enable-librubberband --enable-libzimg
  --enable-libbluray --enable-libdvdnav --enable-libdvdread
  --enable-libxml2 --enable-libplacebo --enable-vulkan
  --enable-d3d11va --enable-d3d12va --enable-dxva2
)
cd "$ffbuild"
printf '%s\n' "${ffmpeg_options[@]}" > configure-requested.txt
if [ ! -f ffbuild/config.mak ] || ! cmp -s configure-requested.txt configure-arguments.txt; then
  "$source_root/configure" "${ffmpeg_options[@]}"
  cp configure-requested.txt configure-arguments.txt
fi
# Makefile sets `revision=$(REVISION)` for version.sh, overriding the exported
# lowercase environment variable. Pin the make variable too, so no enclosing
# Mani Git metadata can leak into FFmpeg's runtime identity.
revision="$revision" "$source_root/ffbuild/version.sh" "$source_root" libavutil/ffversion.h mani-experiment
make -j6 REVISION="$revision"
make install REVISION="$revision"
grep -Fq '#define FFMPEG_VERSION "N-127617-g94cba3150-mani-experiment"' libavutil/ffversion.h
export PATH="$prefix/bin:$PATH"
cd "$repo_root"
if [ ! -f "$mpbuild/build.ninja" ]; then
  meson setup "$mpbuild" "$experiment/src/mpv" \
    --prefix="$(cygpath -m "$prefix")" --libdir=lib --buildtype=release \
    --wrap-mode=nofallback -Ddefault_library=shared -Dlibmpv=true -Dcplayer=true \
    -Dbuild-date=false -Dmanpage-build=disabled -Dhtml-build=disabled -Dpdf-build=disabled \
    -Dlua=enabled -Djavascript=enabled -Dlcms2=enabled -Drubberband=enabled \
    -Duchardet=enabled -Dlibarchive=enabled -Dlibbluray=enabled -Ddvdnav=enabled \
    -Dsdl2-gamepad=enabled -Dopenal=enabled -Dlibcurl=enabled \
    -Dgl=enabled -Degl-angle=enabled -Dshaderc=enabled -Dspirv-cross=enabled \
    -Dd3d11=enabled -Dvulkan=enabled -Dwasapi=enabled
fi
meson compile -C "$mpbuild" -j6
meson install -C "$mpbuild" --no-rebuild
printf 'Full experimental %s engine built at %s\n' "$variant" "$prefix"
