#!/usr/bin/env bash
# Publishes OurTaikoEdit self-contained for one runtime and lays it out for shipping:
#   osx-arm64  -> <out>/OurTaikoEdit.app (Info.plist, icon, ad-hoc signed; run on macOS)
#   win-x64    -> <out>/ holding OurTaikoEdit.exe and everything next to it
# Usage: build/package.sh <rid> <out> [version] [display-version]
#   version          plain dotted numbers (assembly and CFBundleShortVersionString), default from Directory.Build.props
#   display-version  shipped version string (InformationalVersion, CFBundleVersion), default version-<commit count>
set -euo pipefail

RID="${1:?rid (osx-arm64 or win-x64)}"
OUT="${2:?output directory}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${3:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -n 1)}"
DISPLAY="${4:-$VERSION-$(git -C "$ROOT" rev-list --count HEAD)}"
NAME=OurTaikoEdit
PUBLISH="$(mktemp -d)"
trap 'rm -rf "$PUBLISH"' EXIT

dotnet publish "$ROOT/src/$NAME.App" -c Release -r "$RID" --self-contained true \
  -p:Version="$VERSION" -p:InformationalVersion="$DISPLAY" -p:DebugType=none -o "$PUBLISH"
# Native symbol files from NuGet packages (SkiaSharp, HarfBuzz) are ~100 MB and never needed by players.
find "$PUBLISH" -name '*.pdb' -delete

rm -rf "$OUT"
mkdir -p "$OUT"
case "$RID" in
  osx-*)
    APP="$OUT/$NAME.app"
    mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
    cp -a "$PUBLISH/." "$APP/Contents/MacOS/"
    sips -s format icns "$ROOT/src/$NAME.App/Assets/app.ico" --out "$APP/Contents/Resources/$NAME.icns" >/dev/null
    cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>$NAME</string>
  <key>CFBundleDisplayName</key><string>$NAME</string>
  <key>CFBundleIdentifier</key><string>org.ourtaiko.edit</string>
  <key>CFBundleExecutable</key><string>$NAME</string>
  <key>CFBundleIconFile</key><string>$NAME</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$DISPLAY</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
</dict></plist>
PLIST
    # Apple silicon refuses unsigned code; an ad-hoc signature is enough outside Gatekeeper's quarantine.
    codesign --force --deep --sign - "$APP"
    codesign --verify --deep --strict "$APP"
    ;;
  win-*)
    cp -a "$PUBLISH/." "$OUT/"
    ;;
  *)
    echo "Unsupported runtime: $RID" >&2
    exit 1
    ;;
esac
echo "Packaged $NAME $DISPLAY ($RID) in $OUT"
