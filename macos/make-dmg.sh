#!/bin/bash
# Собирает приложение и упаковывает его в установщик DMG
# (FinderSizes.app + ярлык «Программы» для перетаскивания).
# Куда положить: первый аргумент или ~/Downloads.
set -euo pipefail
cd "$(dirname "$0")"

./build.sh

VERSION=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' Resources/Info.plist)
OUT_DIR="${1:-$HOME/Downloads}"
DMG="$OUT_DIR/FinderSizes-macOS-$VERSION.dmg"

STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
cp -R build/FinderSizes.app "$STAGE/"
ln -s /Applications "$STAGE/Программы"

rm -f "$DMG"
hdiutil create -volname "FinderSizes" -srcfolder "$STAGE" \
    -fs HFS+ -format UDZO -ov "$DMG" >/dev/null

ID="${SIGN_ID:-$(security find-identity -p codesigning -v | awk -F'"' '/Apple Development/ {print $2; exit}')}"
[ -n "$ID" ] && codesign --force --sign "$ID" "$DMG"

echo "Установщик: $DMG"
