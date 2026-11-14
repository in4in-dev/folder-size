#!/bin/bash
# Builds the app and packs it into a DMG installer
# (FinderSizes.app + an Applications shortcut to drag it onto).
# Output folder: first argument or ~/Downloads.
set -euo pipefail
cd "$(dirname "$0")"

./build.sh

VERSION=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' Resources/Info.plist)
OUT_DIR="${1:-$HOME/Downloads}"
DMG="$OUT_DIR/FinderSizes-macOS-$VERSION.dmg"

STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
cp -R build/FinderSizes.app "$STAGE/"
ln -s /Applications "$STAGE/Applications"

rm -f "$DMG"
hdiutil create -volname "FinderSizes" -srcfolder "$STAGE" \
    -fs HFS+ -format UDZO -ov "$DMG" >/dev/null

ID="${SIGN_ID:-$(security find-identity -p codesigning -v | awk -F'"' '/Apple Development/ {print $2; exit}')}"
[ -n "$ID" ] && codesign --force --sign "$ID" "$DMG"

echo "Installer: $DMG"
