#!/bin/bash
# Собирает build/FinderSizes.app. Подписывает первым найденным сертификатом
# «Apple Development» (или SIGN_ID), чтобы разрешения macOS переживали пересборку.
set -euo pipefail
cd "$(dirname "$0")"

swift build -c release

APP=build/FinderSizes.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp .build/release/FinderSizes "$APP/Contents/MacOS/"
cp Resources/Info.plist "$APP/Contents/"

ID="${SIGN_ID:-$(security find-identity -p codesigning -v | awk -F'"' '/Apple Development/ {print $2; exit}')}"
codesign --force --sign "${ID:--}" "$APP"
echo "Готово: $APP (подпись: ${ID:-ad-hoc})"
