# ffmpeg for OurTaikoEdit

The installers do not ship ffmpeg. When a feature needs it (video export, transcoding, waveforms), the
app downloads a small ffmpeg + ffprobe built here from the CDN, checking it against a signed manifest.

- `versions.env`: FFmpeg and every library, pinned by version (x264 by commit) and SHA-256.
- `build.sh`: downloads and verifies the sources, builds the libraries statically, then FFmpeg, checks
  the result and packages it.

## What is in it

FFmpeg with `--enable-gpl --enable-version3` (so the binaries are GPLv3, like the app) and, statically:
x264, LAME, libogg + libvorbis, Opus, libvpx (encoders only), dav1d and zlib.

- Decoders, demuxers, parsers and bitstream filters: all of FFmpeg's own (AV1 through dav1d).
- Encoders: libx264, mpeg4, rawvideo, libvpx VP8/VP9, png, mjpeg; aac, libmp3lame, libopus, libvorbis,
  flac, alac, PCM (s16/s24/s32/f32/f64/u8, little and big endian where the containers want it).
- Muxers: mp4, mov, ipod (.m4a), matroska, webm, mp3, adts, ogg, oga, opus, flac, wav, raw PCM
  (`-f s16le`/`f32le`...), rawvideo, image2, image2pipe, null.
- Filters: what the ffmpeg CLI inserts by itself (format, scale, aresample, trim, rotation...) plus
  fps, pad, volume, loudnorm, afade, asetnsamples, pan, amerge, amix, channelmap, apad, volumedetect, silencedetect,
  ebur128, and the sources anullsrc, sine, color, nullsrc, testsrc2 (`-f lavfi`).
- Protocols: file, pipe and fd (`-i -` reads stdin through fd:). No network, devices (except the lavfi input), hardware acceleration,
  ffplay or docs.

`build.sh` lists them exactly; to add one, add it there and publish a new build.

## Building locally

```sh
# macOS (Apple Silicon): brew install nasm meson ninja pkgconf
build/ffmpeg/build.sh osx-arm64 build/ffmpeg/out

# Windows x64, cross-compiled (Linux: apt install mingw-w64 nasm meson ninja-build pkg-config zip,
# or macOS: brew install mingw-w64 nasm meson)
build/ffmpeg/build.sh win-x64 build/ffmpeg/out
```

Output (in the directory given): `<rid>/ffmpeg.zip` (ffmpeg, ffprobe, LICENSE.txt, README.txt) and `source/` (every
source tarball, these scripts and the configure lines that were run). Tarballs are cached in
`build/ffmpeg/downloads`, build trees go to `build/ffmpeg/work` (both ignored by git; set
`FFMPEG_DOWNLOAD_DIR` / `FFMPEG_WORK_DIR` to put them elsewhere). A build takes a few minutes.

The script fails if a wanted component was dropped by configure, if a binary links anything outside
the system (macOS: only `/usr/lib` and system frameworks, minimum macOS 12, ad-hoc signed; Windows:
only system DLLs), or if a download's hash differs from `versions.env`.

## Releases

Run the **FFmpeg** workflow (`.github/workflows/ffmpeg.yml`) by hand with a build number. The release
version is `<FFmpeg version>-<build>`, e.g. `9.1-1`. It builds both platforms, signs `manifest.json`
with `tools/OurTaikoEdit.FfmpegManifest` (key: secret `OURTAIKOEDIT_TOOLS_SIGNING_KEY`) and uploads to
the `sparkbarry-public` bucket, served at <https://distrib.sparkbarry.com/>:

```
OurTaikoEdit/tools/ffmpeg/<version>/osx-arm64/ffmpeg.zip   immutable
OurTaikoEdit/tools/ffmpeg/<version>/win-x64/ffmpeg.zip     immutable
OurTaikoEdit/tools/ffmpeg/<version>/source/                immutable, the GPL source offer
OurTaikoEdit/tools/ffmpeg/manifest.json                    no-cache, uploaded last
```

A version is never overwritten: the workflow stops if `<version>/` exists. To update FFmpeg or a
library, change `versions.env` (new hash included); to rebuild the same versions, use the next build
number.
