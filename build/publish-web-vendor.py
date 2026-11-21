#!/usr/bin/env python3
"""Put the web version's large third-party files on ourtaiko-public, once, at paths that never
change, so that they come from distrib.ourtaiko.org or its Aliyun mirror (which fetches a path
once and keeps it) rather than from jsDelivr:

    fonts/noto-sans-sc/a85815a/                  Noto Sans SC (OFL), the CJK fallback (CjkFont.cs)
    edit/vendor/ffmpeg-core-0.12.10/             @ffmpeg/core 0.12.10 (GPL), ffmpeg.wasm's core
                                                 (ffmpeg-bridge.js), with its source under source/

Each file is downloaded from where it was published, checked against the SHA-256 written here,
and uploaded (fonts and the core as Brotli, Content-Encoding: br) unless the key already exists;
an existing key is never overwritten, only checked. Then each is read back through the CDN.

Needs AWS credentials that may write fonts/* and edit/vendor/* (the CI role may not: run it by
hand), and the brotli module (pip install brotli).

Usage: build/publish-web-vendor.py [--publish]
"""

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import urllib.request

import brotli

BUCKET = 'ourtaiko-public'
CDN = 'https://distrib.ourtaiko.org'
GOOGLE_FONTS = 'https://cdn.jsdelivr.net/gh/google/fonts@a85815a42757630ce188fdad368c2dfc444d4773/ofl/notosanssc/'
CORE = 'https://cdn.jsdelivr.net/npm/@ffmpeg/core@0.12.10/dist/umd/'
FONT_DIR = 'fonts/noto-sans-sc/a85815a/'
CORE_DIR = 'edit/vendor/ffmpeg-core-0.12.10/'

# (key, source, sha256 of the source, Content-Type, Brotli?)
FILES = [
    (FONT_DIR + 'NotoSansSC-VF.ttf', GOOGLE_FONTS + 'NotoSansSC%5Bwght%5D.ttf',
     'a3041811a78c361b1de50f953c805e0244951c21c5bd412f7232ef0d899af0da', 'font/ttf', True),
    (FONT_DIR + 'OFL.txt', GOOGLE_FONTS + 'OFL.txt',
     '1c05c68c34f9708415aada51f17e1b0092d2cea709bf4a94cd38114f9e73d7d9', 'text/plain; charset=utf-8', False),
    # The same bytes as dist/umd/ in the npm package @ffmpeg/core 0.12.10 (sha512-dzNplnn2…).
    (CORE_DIR + 'ffmpeg-core.js', CORE + 'ffmpeg-core.js',
     'b266ab5b952555881dd6310663986994a182acb2b7ff25cf10a25f7a37ac2b21', 'text/javascript; charset=utf-8', True),
    (CORE_DIR + 'ffmpeg-core.wasm', CORE + 'ffmpeg-core.wasm',
     '9f57947a5bd530d8f00c5b3f2cb2a3492faa7e5d823315342d6a8656d0a6b7b7', 'application/wasm', True),
    # The core's source: ffmpeg.wasm at the tag it was built from (its Dockerfile names every
    # library and branch), and the FFmpeg release that Dockerfile builds (n5.1.4).
    (CORE_DIR + 'source/ffmpeg.wasm-v0.12.10.tar.gz', 'https://codeload.github.com/ffmpegwasm/ffmpeg.wasm/tar.gz/refs/tags/v0.12.10',
     None, 'application/gzip', False),
    (CORE_DIR + 'source/ffmpeg-5.1.4.tar.xz', 'https://ffmpeg.org/releases/ffmpeg-5.1.4.tar.xz',
     None, 'application/x-xz', False),
]

README = """# @ffmpeg/core 0.12.10

ffmpeg-core.js and ffmpeg-core.wasm are dist/umd/ of the npm package @ffmpeg/core 0.12.10
(https://www.npmjs.com/package/@ffmpeg/core/v/0.12.10), unchanged: FFmpeg built to WebAssembly by
the ffmpeg.wasm project (https://github.com/ffmpegwasm/ffmpeg.wasm), licensed GPL-2.0-or-later.
OurTaikoEdit's web version loads them to convert audio and video in the browser.

Source (source/):
- ffmpeg.wasm-v0.12.10.tar.gz: the ffmpeg.wasm repository at tag v0.12.10. Its Dockerfile is the
  build: Emscripten 3.1.40, FFmpeg n5.1.4 and each library at the branch it names (x264, x265,
  libvpx, lame, ogg, theora, opus, vorbis, zlib, libwebp, freetype2, fribidi, harfbuzz, libass,
  zimg, from the repositories it names).
- ffmpeg-5.1.4.tar.xz: FFmpeg 5.1.4 (https://ffmpeg.org/releases/).

Files here never change; a new version of the core goes in a new folder.
"""


def download(url, path):
    with urllib.request.urlopen(url, timeout=300) as response, open(path, 'wb') as f:
        while chunk := response.read(1024 * 1024):
            f.write(chunk)


def sha256(path):
    digest = hashlib.sha256()
    with open(path, 'rb') as f:
        while chunk := f.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def existing(key):
    """The sha256 the object was uploaded with, or None when there is no such object."""
    result = subprocess.run(['aws', 's3api', 'head-object', '--bucket', BUCKET, '--key', key, '--output', 'json'],
                            capture_output=True, text=True)
    if result.returncode != 0:
        if 'Not Found' in result.stderr or '404' in result.stderr:
            return None
        raise RuntimeError(result.stderr.strip())
    return json.loads(result.stdout).get('Metadata', {}).get('sha256', '')


def verify(key, body_sha, content_type, encoding):
    request = urllib.request.Request(f'{CDN}/{key}', headers={'Accept-Encoding': 'identity'})
    with urllib.request.urlopen(request, timeout=300) as response:
        digest = hashlib.sha256()
        while chunk := response.read(1024 * 1024):
            digest.update(chunk)
        if digest.hexdigest() != body_sha:
            raise ValueError(f'CDN content mismatch: {key}')
        if response.headers.get_content_type() != content_type.split(';')[0]:
            raise ValueError(f'CDN Content-Type mismatch: {key}')
        if response.headers.get('Content-Encoding') != encoding:
            raise ValueError(f'CDN Content-Encoding mismatch: {key}')


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--publish', action='store_true', help='Upload what is missing (otherwise only download and check)')
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='ourtaikoedit-vendor-') as directory:
        work = Path(directory)
        readme = work / 'README.md'
        readme.write_text(README)
        files = [(CORE_DIR + 'README.md', None, None, 'text/markdown; charset=utf-8', False)] + FILES
        for key, url, want, content_type, compress in files:
            source = readme if url is None else work / key.replace('/', '_')
            if url is not None:
                download(url, source)
                got = sha256(source)
                if want is not None and got != want:
                    raise ValueError(f'{url}: SHA-256 {got}, expected {want}')
            body = source
            if compress:
                body = source.with_name(source.name + '.br')
                body.write_bytes(brotli.compress(source.read_bytes(), quality=11))
            body_sha = sha256(body)
            encoding = 'br' if compress else None
            print(f'{key}: {source.stat().st_size} bytes, uploaded as {body.stat().st_size} ({body_sha[:12]})')
            if not args.publish:
                continue
            have = existing(key)
            if have is None:
                cmd = ['aws', 's3', 'cp', str(body), f's3://{BUCKET}/{key}', '--only-show-errors',
                       '--content-type', content_type, '--cache-control', 'public,max-age=31536000,immutable',
                       '--metadata', f'sha256={body_sha}']
                if encoding:
                    cmd += ['--content-encoding', encoding]
                subprocess.run(cmd, check=True, stdin=subprocess.DEVNULL)
            elif have != body_sha:
                # Brotli's output for the same input and settings does not change; a different
                # hash means a different file, and these keys are never replaced.
                raise ValueError(f'{key} already exists with sha256 {have}; not replaced')
            verify(key, body_sha, content_type, encoding)
            print(f'  {"uploaded" if have is None else "already there"}, served by the CDN')


if __name__ == '__main__':
    main()
