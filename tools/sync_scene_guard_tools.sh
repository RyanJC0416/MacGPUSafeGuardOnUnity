#!/usr/bin/env bash
# Sync git mirror → app bundle source tree (Resources/scene-guard-tools).
set -euo pipefail
cd "$(dirname "$0")/.."
SRC="SceneGuard/unity-patches/Assets"
DEST="Resources/scene-guard-tools"
if [ ! -d "$SRC" ]; then
  echo "Missing $SRC — run from MacGPUSafeGuardOnUnity repo root." >&2
  exit 1
fi
rm -rf "$DEST"
mkdir -p "$DEST"
cp -R "$SRC" "$DEST/"
echo "Synced $SRC → $DEST/"
