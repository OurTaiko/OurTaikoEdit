#!/usr/bin/env bash
# Uploads the static site from build/package-web.sh to the bucket behind distrib.sparkbarry.com
# (S3 through CloudFront, which serves the stored bytes as they are), replacing what is there:
#
#   - Each file goes up as its Brotli copy (<file>.br) with Content-Encoding: br where there is one
#     (28 MB becomes about 10 MB); the .br and .gz copies themselves are not uploaded.
#   - _framework's fingerprinted files (name.<10 characters>.wasm/.dat/.js) never change: cached for
#     a year. Everything else (index.html, the page's scripts, dotnet.js, which names the
#     fingerprinted files) is revalidated on every load.
#   - Order: fingerprinted files, then the others, index.html last; once the CDN serves the new
#     index.html, files the new site no longer has are removed.
#
# Usage: build/upload-web.sh <site> <s3://bucket/prefix> <public url of the prefix>
# Needs AWS credentials allowed to write and delete under the prefix.
set -euo pipefail

SITE="${1:?site directory}"
DEST="${2:?s3://bucket/prefix}"
URL="${3:?public url}"
DEST="${DEST%/}"
URL="${URL%/}"

content_type() {
  case "$1" in
    *.html) echo "text/html; charset=utf-8" ;;
    *.js) echo "text/javascript; charset=utf-8" ;;
    *.css) echo "text/css; charset=utf-8" ;;
    *.json) echo "application/json" ;;
    *.md) echo "text/markdown; charset=utf-8" ;;
    *.wasm) echo "application/wasm" ;;
    *.png) echo "image/png" ;;
    *.svg) echo "image/svg+xml" ;;
    *.ico) echo "image/x-icon" ;;
    *) echo "application/octet-stream" ;;
  esac
}

fingerprinted() { [[ "$1" =~ ^_framework/.*\.[a-z0-9]{10}\.(wasm|dat|js)$ ]]; }

upload() {
  local rel="$1" cache="$2" src="$SITE/$1"
  local args=(--only-show-errors --cache-control "$cache" --content-type "$(content_type "$rel")")
  if [[ -f "$src.br" ]]; then
    aws s3 cp "$src.br" "$DEST/$rel" "${args[@]}" --content-encoding br < /dev/null
  else
    aws s3 cp "$src" "$DEST/$rel" "${args[@]}" < /dev/null
  fi
}

sha256() { if command -v sha256sum >/dev/null; then sha256sum; else shasum -a 256; fi | cut -d' ' -f1; }

LIST="$(mktemp)"
trap 'rm -f "$LIST"' EXIT
(cd "$SITE" && find . -type f ! -name '*.br' ! -name '*.gz' | sed 's:^\./::' | LC_ALL=C sort) > "$LIST"
grep -qx index.html "$LIST" || { echo "No index.html in $SITE" >&2; exit 1; }

while read -r f; do
  if fingerprinted "$f"; then upload "$f" "public, max-age=31536000, immutable"; fi
done < "$LIST"
while read -r f; do
  if ! fingerprinted "$f" && [[ "$f" != index.html ]]; then upload "$f" "no-cache"; fi
done < "$LIST"
upload index.html "no-cache"

# The CDN must serve the new index.html before the old site's files go.
want=$(sha256 < "$SITE/index.html")
for i in $(seq 1 30); do
  got=$(curl -fsS --compressed "$URL/index.html" | sha256) || true
  [[ "$got" == "$want" ]] && break
  if [[ $i == 30 ]]; then echo "$URL/index.html is not served correctly through the CDN." >&2; exit 1; fi
  sleep 2
done

prefix="${DEST#s3://*/}"
bucket="${DEST#s3://}"; bucket="${bucket%%/*}"
removed=0
while read -r key; do
  [[ -z "$key" || "$key" == None ]] && continue
  grep -qxF "${key#"$prefix"/}" "$LIST" && continue
  aws s3 rm "s3://$bucket/$key" --only-show-errors < /dev/null
  removed=$((removed + 1))
done < <(aws s3api list-objects-v2 --bucket "$bucket" --prefix "$prefix/" --query 'Contents[].Key' --output text | tr '\t' '\n')
echo "Uploaded $(wc -l < "$LIST" | tr -d ' ') files to $DEST, removed $removed old ones. $URL/index.html"
