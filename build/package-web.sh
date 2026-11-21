#!/usr/bin/env bash
# Publishes the web version (src/OurTaikoEdit.Browser) as a static site in <out>: index.html, the
# page's scripts and _framework/ (each file next to its .br and .gz). Needs the wasm-tools workload.
# Usage: build/package-web.sh <out> [version] [display-version]
#   version          plain dotted numbers (assembly version), default from Directory.Build.props
#   display-version  shipped version string (InformationalVersion), default version-<commit count>
set -euo pipefail

OUT="${1:?output directory}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${2:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -n 1)}"
DISPLAY="${3:-$VERSION-$(git -C "$ROOT" rev-list --count HEAD)}"
PUBLISH="$(mktemp -d)"
trap 'rm -rf "$PUBLISH"' EXIT

dotnet publish "$ROOT/src/OurTaikoEdit.Browser" -c Release \
  -p:Version="$VERSION" -p:InformationalVersion="$DISPLAY" -p:DebugType=none -o "$PUBLISH"

rm -rf "$OUT"
mkdir -p "$OUT"
cp -a "$PUBLISH/wwwroot/." "$OUT/"
# Source maps of Avalonia's and .NET's loader scripts: not needed to run.
find "$OUT" -name '*.map' -delete -o -name '*.map.br' -delete -o -name '*.map.gz' -delete
test -f "$OUT/index.html"
test -f "$OUT/_framework/dotnet.js"
du -sh "$OUT"
