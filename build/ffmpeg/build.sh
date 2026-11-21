#!/usr/bin/env bash
# Builds the ffmpeg + ffprobe that OurTaikoEdit downloads on demand, for one platform.
#
#   build/ffmpeg/build.sh <rid> <out-dir> [package-version]
#
#   rid              osx-arm64 (on an Apple Silicon Mac) or win-x64 (cross-compiled with mingw-w64,
#                    on Linux or on a Mac with `brew install mingw-w64`)
#   out-dir          receives <rid>/ffmpeg.zip and source/ (everything needed for the GPL source offer)
#   package-version  the release this goes into, e.g. 9.1-1 (named in README.txt's source URL);
#                    default <FFmpeg version>-local
#
# Environment:
#   FFMPEG_DOWNLOAD_DIR  tarball cache (default build/ffmpeg/downloads); kept between runs
#   FFMPEG_WORK_DIR      build trees and the static prefix (default build/ffmpeg/work); can be deleted
#   JOBS                 parallel make jobs (default: the number of CPUs)
#
# Needs: curl, tar, xz, zip, make, pkg-config, nasm, meson, ninja; for win-x64 also mingw-w64 (gcc).
# Every source is pinned with its SHA-256 in versions.env. All libraries are built statically into a
# private prefix, and nothing is taken from the system (pkg-config only looks in that prefix and
# ffmpeg's --disable-autodetect keeps it from finding anything on its own).
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=versions.env
source "$here/versions.env"

usage() { echo "usage: $0 <osx-arm64|win-x64> <out-dir> [package-version]" >&2; exit 2; }
[[ $# -ge 2 ]] || usage
rid=$1
mkdir -p "$2"
out=$(cd "$2" && pwd)
package_version=${3:-$FFMPEG_VERSION-local}
downloads=${FFMPEG_DOWNLOAD_DIR:-$here/downloads}
work=${FFMPEG_WORK_DIR:-$here/work}/$rid
source_url="https://distrib.sparkbarry.com/OurTaikoEdit/tools/ffmpeg/$package_version/source/"

if command -v nproc >/dev/null 2>&1; then jobs=$(nproc); else jobs=$(sysctl -n hw.ncpu); fi
jobs=${JOBS:-$jobs}

log() { printf '\n==> %s\n' "$*"; }
die() { echo "error: $*" >&2; exit 1; }
sha256() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1"; else shasum -a 256 "$1"; fi | cut -d' ' -f1; }

# ---------------------------------------------------------------------------------------------------
# Toolchain per platform

prefix=$work/prefix
src=$work/src
record=$work/configure-$rid.txt   # every configure line run, copied to source/

case "$rid" in
  osx-arm64)
    [[ "$(uname -s)-$(uname -m)" == Darwin-arm64 ]] || die "osx-arm64 is built natively on an Apple Silicon Mac."
    export MACOSX_DEPLOYMENT_TARGET=12.0
    min="-mmacosx-version-min=$MACOSX_DEPLOYMENT_TARGET"
    export CC=clang CXX=clang++ AR=ar RANLIB=ranlib STRIP=strip
    export CFLAGS="-O2 -arch arm64 $min" LDFLAGS="-arch arm64 $min"
    host_args=()
    exe=""
    ;;
  win-x64)
    cross=x86_64-w64-mingw32
    command -v $cross-gcc >/dev/null || die "$cross-gcc not found (apt install mingw-w64 / brew install mingw-w64)."
    export CC=$cross-gcc CXX=$cross-g++ AR=$cross-ar RANLIB=$cross-ranlib STRIP=$cross-strip
    export CFLAGS="-O2" LDFLAGS="-static -static-libgcc"
    host_args=(--host=$cross)
    exe=".exe"
    ;;
  *) usage ;;
esac
export CPPFLAGS="-I$prefix/include"
export LDFLAGS="$LDFLAGS -L$prefix/lib"
# Only our prefix: never a Homebrew or distro library.
export PKG_CONFIG_LIBDIR=$prefix/lib/pkgconfig
unset PKG_CONFIG_PATH CPATH C_INCLUDE_PATH LIBRARY_PATH

for tool in curl tar xz zip make pkg-config nasm meson ninja; do
  command -v $tool >/dev/null || die "$tool not found."
done

# ---------------------------------------------------------------------------------------------------
# Sources

fetch() { # <NAME>: download (or reuse) the tarball and check its hash
  local url file want got
  url=$(eval echo "\$${1}_URL"); file=$(eval echo "\$${1}_FILE"); want=$(eval echo "\$${1}_SHA256")
  mkdir -p "$downloads"
  if [[ ! -f "$downloads/$file" ]] || [[ "$(sha256 "$downloads/$file")" != "$want" ]]; then
    log "Downloading $file"
    curl -fL --retry 3 --retry-delay 5 -o "$downloads/$file.part" "$url"
    mv "$downloads/$file.part" "$downloads/$file"
  fi
  got=$(sha256 "$downloads/$file")
  [[ "$got" == "$want" ]] || die "$file: SHA-256 is $got, expected $want (versions.env)."
}

unpack() { # <NAME>: fresh source tree, prints its path
  local file dir
  file=$(eval echo "\$${1}_FILE")
  dir=$src/$1
  rm -rf "$dir" && mkdir -p "$dir"
  tar -xf "$downloads/$file" -C "$dir" --strip-components=1
  echo "$dir"
}

quoted() { local q; q=$(printf '%q ' "$@"); echo "${q% }"; }

run_configure() { # <name> <command...>: run a configure step and keep the exact line
  local name=$1; shift
  printf '%s:\n  %s\n\n' "$name" "$(quoted "$@")" >> "$record"
  "$@"
}

for s in $SOURCES; do fetch "$s"; done

rm -rf "$work" && mkdir -p "$prefix/lib/pkgconfig" "$src"
: > "$record"
{
  echo "# Configure lines for $rid (FFmpeg $FFMPEG_VERSION, package $package_version)"
  echo "# Environment: CC=$CC CFLAGS=$CFLAGS LDFLAGS=$LDFLAGS"
  echo
} >> "$record"

# ---------------------------------------------------------------------------------------------------
# Libraries (static, into $prefix)

log "zlib $ZLIB_VERSION"
cd "$(unpack ZLIB)"
if [[ $rid == win-x64 ]]; then
  # zlib's configure has no --host: it takes the target from CHOST.
  CHOST=$cross run_configure zlib ./configure --prefix="$prefix" --static
else
  run_configure zlib ./configure --prefix="$prefix" --static
fi
make -j"$jobs" libz.a && make install

log "x264 $X264_VERSION"
cd "$(unpack X264)"
x264_args=(--prefix="$prefix" --enable-static --disable-cli --enable-pic --bit-depth=8
  --disable-opencl --disable-avs --disable-swscale --disable-lavf --disable-ffms --disable-gpac --disable-lsmash)
if [[ $rid == win-x64 ]]; then x264_args+=(--host=$cross --cross-prefix=$cross-); fi
x264_args+=(--extra-cflags="$CFLAGS" --extra-ldflags="$LDFLAGS")
run_configure x264 ./configure "${x264_args[@]}"
make -j"$jobs" && make install-lib-static

log "LAME $LAME_VERSION"
cd "$(unpack LAME)"
run_configure lame ./configure --prefix="$prefix" ${host_args[@]+"${host_args[@]}"} --disable-shared --enable-static \
  --disable-frontend --disable-decoder --disable-gtktest
make -j"$jobs" && make install

log "libogg $OGG_VERSION"
cd "$(unpack OGG)"
run_configure libogg ./configure --prefix="$prefix" ${host_args[@]+"${host_args[@]}"} --disable-shared --enable-static
make -j"$jobs" && make install

log "libvorbis $VORBIS_VERSION"
cd "$(unpack VORBIS)"
# 1.3.7's configure passes -force_cpusubtype_ALL on Darwin, which clang rejects for arm64.
sed -i.orig 's/ -force_cpusubtype_ALL//g' configure
run_configure libvorbis ./configure --prefix="$prefix" ${host_args[@]+"${host_args[@]}"} --disable-shared --enable-static \
  --disable-docs --disable-examples --disable-oggtest --with-ogg="$prefix"
make -j"$jobs" && make install

log "Opus $OPUS_VERSION"
cd "$(unpack OPUS)"
# No stack protector: with mingw it would need libssp at link time.
run_configure opus ./configure --prefix="$prefix" ${host_args[@]+"${host_args[@]}"} --disable-shared --enable-static \
  --disable-doc --disable-extra-programs --disable-stack-protector
make -j"$jobs" && make install

log "libvpx $VPX_VERSION"
cd "$(unpack VPX)"
# Encoders only: FFmpeg decodes VP8 and VP9 itself.
vpx_args=(--prefix="$prefix" --disable-shared --enable-static --enable-pic
  --disable-examples --disable-tools --disable-docs --disable-unit-tests --disable-webm-io --disable-libyuv
  --enable-vp8 --enable-vp9 --disable-vp8-decoder --disable-vp9-decoder)
if [[ $rid == win-x64 ]]; then
  vpx_args+=(--target=x86_64-win64-gcc --as=nasm)
  CROSS=$cross- run_configure libvpx ./configure "${vpx_args[@]}"
  CROSS=$cross- make -j"$jobs"
  CROSS=$cross- make install
else
  vpx_args+=(--target=arm64-darwin21-gcc)
  run_configure libvpx ./configure "${vpx_args[@]}"
  make -j"$jobs" && make install
fi

log "dav1d $DAV1D_VERSION"
cd "$(unpack DAV1D)"
dav1d_args=(--prefix="$prefix" --libdir=lib --buildtype=release --default-library=static
  -Denable_tools=false -Denable_tests=false -Denable_examples=false -Denable_docs=false)
if [[ $rid == win-x64 ]]; then
  cat > cross.ini <<EOF
[binaries]
c = '$cross-gcc'
cpp = '$cross-g++'
ar = '$cross-ar'
strip = '$cross-strip'
windres = '$cross-windres'
pkg-config = 'pkg-config'

[host_machine]
system = 'windows'
cpu_family = 'x86_64'
cpu = 'x86_64'
endian = 'little'
EOF
  dav1d_args+=(--cross-file=cross.ini)
fi
run_configure dav1d meson setup build "${dav1d_args[@]}"
ninja -C build -j "$jobs" && ninja -C build install

# ---------------------------------------------------------------------------------------------------
# FFmpeg

log "FFmpeg $FFMPEG_VERSION"
ffsrc=$(unpack FFMPEG)
cd "$ffsrc"

encoders=(
  libx264 mpeg4 rawvideo wrapped_avframe png mjpeg           # video export, previews, -f null
  libvpx_vp8 libvpx_vp9
  aac libmp3lame libopus libvorbis flac alac
  pcm_s16le pcm_s24le pcm_s32le pcm_f32le pcm_f64le pcm_u8
  pcm_s16be pcm_s24be pcm_s32be pcm_f32be
)
muxers=(
  mp4 mov ipod matroska matroska_audio webm
  mp3 adts ogg oga opus flac wav
  null rawvideo image2 image2pipe
  pcm_s16le pcm_s24le pcm_s32le pcm_f32le pcm_f64le pcm_u8   # -f s16le, -f f32le... (waveforms)
)
filters=(
  # inserted by the ffmpeg CLI itself (format negotiation, -ss/-t, rotation and cropping metadata)
  null anull format aformat scale aresample trim atrim setpts asetpts transpose hflip vflip rotate crop
  copy acopy split asplit
  # transcoding
  fps pad setsar setdar volume loudnorm afade asetnsamples pan amerge amix channelmap channelsplit apad
  volumedetect silencedetect ebur128
  # sources (-f lavfi): silence, test tones and pictures
  anullsrc sine nullsrc color testsrc2
)
join() { local IFS=,; echo "$*"; }

ff_args=(
  --prefix="$prefix"
  --pkg-config=pkg-config --pkg-config-flags=--static   # the host one, also when cross-compiling
  --extra-version=OurTaikoEdit
  --enable-gpl --enable-version3
  --enable-static --disable-shared
  --disable-autodetect --disable-debug --disable-doc --disable-ffplay
  --disable-network --disable-devices --disable-hwaccels
  --disable-encoders --disable-muxers --disable-protocols --disable-filters
  --enable-zlib
  --enable-libx264 --enable-libmp3lame --enable-libvorbis --enable-libopus --enable-libvpx --enable-libdav1d
  --disable-decoder=libvpx_vp8,libvpx_vp9
  --enable-encoder="$(join "${encoders[@]}")"
  --enable-muxer="$(join "${muxers[@]}")"
  --enable-filter="$(join "${filters[@]}")"
  --enable-protocol=file,pipe,fd   # "-" as an input is fd: since FFmpeg 8
  --enable-indev=lavfi
)
if [[ $rid == win-x64 ]]; then
  ff_args+=(--enable-w32threads --target-os=mingw32 --arch=x86_64 --cross-prefix=$cross- --enable-cross-compile
    --extra-cflags="-I$prefix/include" --extra-ldflags="-static -static-libgcc -L$prefix/lib")
else
  ff_args+=(--enable-pthreads --cc=clang
    --extra-cflags="-arch arm64 $min -I$prefix/include" --extra-ldflags="-arch arm64 $min -L$prefix/lib -Wl,-dead_strip_dylibs")
fi
printf '%s:\n  %s\n\n' ffmpeg "$(quoted ./configure "${ff_args[@]}")" >> "$record"
# FFmpeg's configure takes its flags from the arguments above, not the environment.
( unset CFLAGS LDFLAGS CPPFLAGS CC CXX AR RANLIB STRIP; ./configure "${ff_args[@]}" ) \
  || { tail -50 ffbuild/config.log; exit 1; }
# The build directory is replaced by $WORK in what is published.
ffmpeg_configure=$(quoted ./configure "${ff_args[@]}" | sed "s|$work|\$WORK|g")

# Something wanted but quietly dropped by configure (a library not found) fails the build here.
for c in LIBX264_ENCODER LIBMP3LAME_ENCODER LIBOPUS_ENCODER LIBVORBIS_ENCODER LIBVPX_VP8_ENCODER \
         LIBVPX_VP9_ENCODER LIBDAV1D_DECODER AAC_ENCODER MPEG4_ENCODER ZLIB LAVFI_INDEV \
         MP4_MUXER MOV_MUXER IPOD_MUXER PCM_S16LE_MUXER PCM_F32LE_MUXER WEBM_MUXER MATROSKA_MUXER OPUS_MUXER PNG_DECODER \
         H264_DECODER HEVC_DECODER VP9_DECODER PRORES_DECODER LOUDNORM_FILTER FILE_PROTOCOL PIPE_PROTOCOL FD_PROTOCOL; do
  grep -q "^CONFIG_$c=yes" ffbuild/config.mak || die "configure left out $c."
done
grep -q "^HAVE_THREADS=yes" ffbuild/config.mak || die "configure found no threads."
for c in NETWORK HTTP_PROTOCOL TCP_PROTOCOL; do
  ! grep -q "^CONFIG_$c=yes" ffbuild/config.mak || die "$c should be off."
done

make -j"$jobs"

# ---------------------------------------------------------------------------------------------------
# Check, sign and package

stage=$work/stage
rm -rf "$stage" && mkdir -p "$stage"
cp "ffmpeg$exe" "ffprobe$exe" "$stage/"
"$STRIP" "$stage/ffmpeg$exe" "$stage/ffprobe$exe"

for bin in "$stage/ffmpeg$exe" "$stage/ffprobe$exe"; do
  if [[ $rid == osx-arm64 ]]; then
    bad=$(otool -L "$bin" | tail -n +2 | awk '{print $1}' | grep -Ev '^(/usr/lib/|/System/Library/)' || true)
    [[ -z "$bad" ]] || die "$(basename "$bin") links outside the system: $bad"
    minos=$(otool -l "$bin" | awk '/LC_BUILD_VERSION/{f=1} f&&/minos/{print $2; exit}')
    [[ "$minos" == "12.0" ]] || die "$(basename "$bin") has minos $minos, expected 12.0."
    codesign -s - --force "$bin"
    codesign --verify --strict "$bin"
    "$bin" -hide_banner -version >/dev/null
  else
    dlls=$($cross-objdump -p "$bin" | awk '/DLL Name:/{print tolower($3)}' | sort -u)
    bad=$(echo "$dlls" | grep -Ev '^(kernel32|user32|gdi32|advapi32|bcrypt|msvcrt|ucrtbase|shell32|ole32|oleaut32|shlwapi|psapi|secur32|crypt32|ntdll|winmm|ws2_32|api-ms-win-[a-z0-9-]+)\.dll$' || true)
    [[ -z "$bad" ]] || die "$(basename "$bin") needs non-system DLLs: $bad"
    echo "$(basename "$bin") imports: $(echo $dlls)"
  fi
done

lib_line() { printf '  %-10s %s  (%s)\n' "$1" "$2" "$3"; }
{
  echo "FFmpeg $FFMPEG_VERSION for OurTaikoEdit ($rid), package $package_version"
  echo
  echo "These are ffmpeg and ffprobe from FFmpeg (https://ffmpeg.org), built for OurTaikoEdit with only what it"
  echo "needs. They are licensed under the GNU General Public License version 3 (LICENSE.txt), since the build"
  echo "uses --enable-gpl --enable-version3."
  echo
  echo "Complete corresponding source: $source_url"
  echo "(every source tarball below, the build script, versions file and configure lines)."
  echo
  echo "Built from:"
  lib_line FFmpeg "$FFMPEG_VERSION" "$FFMPEG_URL"
  lib_line x264 "$X264_VERSION" "$X264_URL"
  lib_line LAME "$LAME_VERSION" "$LAME_URL"
  lib_line libogg "$OGG_VERSION" "$OGG_URL"
  lib_line libvorbis "$VORBIS_VERSION" "$VORBIS_URL"
  lib_line Opus "$OPUS_VERSION" "$OPUS_URL"
  lib_line libvpx "$VPX_VERSION" "$VPX_URL"
  lib_line dav1d "$DAV1D_VERSION" "$DAV1D_URL"
  lib_line zlib "$ZLIB_VERSION" "$ZLIB_URL"
  echo
  echo "FFmpeg configure line:"
  echo "  $ffmpeg_configure"
} > "$stage/README.txt"
cp "$ffsrc/COPYING.GPLv3" "$stage/LICENSE.txt"
if [[ $rid == win-x64 ]]; then
  # Windows line endings for the text files, which people open in Notepad.
  for f in README.txt LICENSE.txt; do sed 's/$/\r/' "$stage/$f" > "$stage/$f.crlf" && mv "$stage/$f.crlf" "$stage/$f"; done
fi

chmod 755 "$stage/ffmpeg$exe" "$stage/ffprobe$exe"
chmod 644 "$stage/README.txt" "$stage/LICENSE.txt"
touch -t 202601010000 "$stage"/*
mkdir -p "$out/$rid"
rm -f "$out/$rid/ffmpeg.zip"
(cd "$stage" && zip -X -q -9 "$out/$rid/ffmpeg.zip" "ffmpeg$exe" "ffprobe$exe" LICENSE.txt README.txt)

# Corresponding source: tarballs, this script, versions and the configure lines actually run.
mkdir -p "$out/source"
for s in $SOURCES; do cp "$downloads/$(eval echo "\$${s}_FILE")" "$out/source/"; done
cp "$here/build.sh" "$here/versions.env" "$here/README.md" "$out/source/"
sed "s|$work|\$WORK|g" "$record" > "$out/source/$(basename "$record")"

log "Done: $out/$rid/ffmpeg.zip"
ls -l "$stage"
ls -l "$out/$rid/ffmpeg.zip"
