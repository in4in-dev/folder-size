#!/bin/bash
# Builds build/FinderSizes.app. Signs it with the first "Apple Development"
# certificate found (or SIGN_ID) so macOS permissions survive rebuilds.
set -euo pipefail
cd "$(dirname "$0")"

swift build -c release

APP=build/FinderSizes.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp .build/release/FinderSizes "$APP/Contents/MacOS/"
cp Resources/Info.plist "$APP/Contents/"
mkdir -p "$APP/Contents/Resources"
cp -R Resources/*.lproj "$APP/Contents/Resources/"

ID="${SIGN_ID:-$(security find-identity -p codesigning -v | awk -F'"' '/Apple Development/ {print $2; exit}')}"
codesign --force --sign "${ID:--}" "$APP"
echo "Done: $APP (signed with: ${ID:-ad-hoc})"
