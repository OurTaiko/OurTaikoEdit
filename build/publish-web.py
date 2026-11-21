#!/usr/bin/env python3
"""Publish the web version (build/package-web.sh) to ourtaiko-public, as the web player does
(OurTaikoView_Web scripts/publish-player.py):

    edit/<build id>/...     every file, never changed once uploaded (cached a year)
    edit-<channel>.json     which build is current: {"path", "version", "commit", "files"}

<build id> is from the hashes of the files, so the same build gets the same folder. The files
are uploaded, then each is read back through the CDN (bytes, type, encoding), and only then is
the channel's manifest replaced. The Aliyun mirror (static.sakura-bot.cn/ourtaiko/) fetches a
path once and keeps it, which is right for the build folders; the manifests are JSON, which its
worker rewrites (they are listed in OurTaikoNet's deploy/aliyun-mirror.json).

Each file goes up as its Brotli copy (<file>.br, made by the .NET publish) under the file's own
name with Content-Encoding: br, where there is one: about 10 MB instead of 28. index.html goes up
as it is: OurTaikoNet reads it to serve the editor at net.ourtaiko.org/edit/.

Preview: build/publish-web.py <site> --channel nightly
Publish: build/publish-web.py <site> --channel nightly --publish
Uses the AWS CLI's existing credentials; never deletes anything.
"""

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import urllib.request

TYPES = {
    '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8',
    '.css': 'text/css; charset=utf-8', '.json': 'application/json', '.md': 'text/markdown; charset=utf-8',
    '.wasm': 'application/wasm', '.dat': 'application/octet-stream', '.png': 'image/png',
    '.svg': 'image/svg+xml', '.ico': 'image/x-icon',
}


def entries(site):
    """name -> (file to upload, Content-Type, Content-Encoding or None), every file of the site."""
    result = {}
    for path in sorted(site.rglob('*')):
        if not path.is_file() or path.suffix in ('.br', '.gz') or path.name == '.DS_Store':
            continue
        name = path.relative_to(site).as_posix()
        br = path.with_name(path.name + '.br')
        content_type = TYPES.get(path.suffix, 'application/octet-stream')
        # index.html as it is: other servers read it (OurTaikoNet's /edit/ adds a <base> to it).
        compress = br.is_file() and name != 'index.html'
        result[name] = (br, content_type, 'br') if compress else (path, content_type, None)
    if 'index.html' not in result or '_framework/dotnet.js' not in result:
        raise ValueError(f'{site} is not the web version (no index.html or _framework/dotnet.js)')
    return result


def sha256(path):
    digest = hashlib.sha256()
    with open(path, 'rb') as f:
        while chunk := f.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def make_manifest(files, version, commit):
    hashes = {name: sha256(path) for name, (path, _, _) in files.items()}
    build_id = hashlib.sha256(json.dumps(hashes, sort_keys=True).encode()).hexdigest()[:16]
    return {'path': f'/edit/{build_id}/index.html', 'version': version, 'commit': commit, 'files': hashes}


def verify_cdn(url, expected_hash, content_type, encoding):
    request = urllib.request.Request(url, headers={'Accept-Encoding': 'identity'})
    with urllib.request.urlopen(request, timeout=120) as response:
        # urllib leaves an encoded body as it is: it is compared with the uploaded bytes.
        digest = hashlib.sha256()
        while chunk := response.read(1024 * 1024):
            digest.update(chunk)
        if digest.hexdigest() != expected_hash:
            raise ValueError(f'CDN content mismatch: {url}')
        if response.headers.get_content_type() != content_type.split(';')[0]:
            raise ValueError(f'CDN Content-Type mismatch: {url}')
        if response.headers.get('Content-Encoding') != encoding:
            raise ValueError(f'CDN Content-Encoding mismatch: {url}')


def publish(site, channel, bucket, cdn_url, version, commit, execute=False, run=None, verify=None):
    run = run or subprocess.run
    verify = verify or verify_cdn
    files = entries(site)
    manifest = make_manifest(files, version, commit)
    prefix = manifest['path'].rsplit('/', 1)[0].lstrip('/')
    print(f'Build: {prefix} ({len(files)} files)\nEntry: {cdn_url}{manifest["path"]}')
    if not execute:
        print(f'Preview only. --publish uploads to s3://{bucket}/{prefix}/ and then replaces edit-{channel}.json.')
        return manifest
    for name, (path, content_type, encoding) in files.items():
        args = ['aws', 's3', 'cp', str(path), f's3://{bucket}/{prefix}/{name}', '--only-show-errors',
                '--content-type', content_type, '--cache-control', 'public,max-age=31536000,immutable',
                '--metadata', f'sha256={manifest["files"][name]}']
        if encoding:
            args += ['--content-encoding', encoding]
        run(args, check=True, stdin=subprocess.DEVNULL)
    # The manifest names the build only once every file is served, byte for byte, by the CDN.
    for name, (_, content_type, encoding) in files.items():
        verify(f'{cdn_url}/{prefix}/{name}', manifest['files'][name], content_type, encoding)
    with tempfile.TemporaryDirectory(prefix='ourtaikoedit-manifest-') as directory:
        path = Path(directory) / f'edit-{channel}.json'
        path.write_text(json.dumps(manifest, indent=2) + '\n')
        run(['aws', 's3', 'cp', str(path), f's3://{bucket}/edit-{channel}.json', '--only-show-errors',
             '--content-type', 'application/json', '--cache-control', 'no-store'], check=True, stdin=subprocess.DEVNULL)
    print(f'Published: {cdn_url}/edit-{channel}.json')
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('site', type=Path, help='The site from build/package-web.sh')
    parser.add_argument('--channel', required=True, choices=['nightly', 'stable'])
    parser.add_argument('--version', default='', help='The build\'s version, recorded in the manifest')
    parser.add_argument('--commit', default='', help='The build\'s commit, recorded in the manifest')
    parser.add_argument('--bucket', default='ourtaiko-public')
    parser.add_argument('--cdn-url', default='https://distrib.ourtaiko.org')
    parser.add_argument('--publish', action='store_true', help='Upload and switch the channel\'s manifest')
    args = parser.parse_args()
    if args.publish and not shutil.which('aws'):
        parser.error('The AWS CLI is not installed.')
    # A copy, so that nothing changes the build between hashing and uploading it.
    with tempfile.TemporaryDirectory(prefix='ourtaikoedit-web-') as directory:
        site = Path(directory) / 'site'
        shutil.copytree(args.site, site, ignore=shutil.ignore_patterns('.DS_Store'))
        publish(site, args.channel, args.bucket, args.cdn_url.rstrip('/'), args.version, args.commit, args.publish)


if __name__ == '__main__':
    main()
