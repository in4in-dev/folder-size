#!/bin/bash
# Builds the app, installs it into /Applications (or ~/Applications if not writable) and launches it.
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
echo "Installed: $DEST/FinderSizes.app"
