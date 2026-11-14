#!/bin/bash
# Собирает приложение, ставит его в /Applications (или ~/Applications, если нет прав) и запускает.
set -euo pipefail
cd "$(dirname "$0")"

./build.sh

DEST=/Applications
if [ ! -w "$DEST" ]; then
    DEST="$HOME/Applications"
    mkdir -p "$DEST"
fi

pkill -x FinderSizes || true
rm -rf "$DEST/FinderSizes.app"
cp -R build/FinderSizes.app "$DEST/"
open "$DEST/FinderSizes.app"
echo "Установлено: $DEST/FinderSizes.app"
