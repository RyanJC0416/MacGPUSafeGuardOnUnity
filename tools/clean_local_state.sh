#!/usr/bin/env bash
# Wipe GpuSafeGuard local state for clean install / update testing.
set -euo pipefail

SUPPORT="${HOME}/Library/Application Support/MacGPUSafeGuard"

echo "This removes GpuSafeGuard Application Support data:"
echo "  $SUPPORT"
echo "Including: watchdog, updates cache, heartbeat, snapshots (if under support dir)."
read -r -p "Continue? [y/N] " ans
if [[ "${ans,,}" != "y" ]]; then
  echo "Cancelled."
  exit 0
fi

if [ -d "$SUPPORT" ]; then
  rm -rf "$SUPPORT"
  echo "Removed $SUPPORT"
else
  echo "Nothing to remove at $SUPPORT"
fi

echo "Done. Quit GpuSafeGuard, replace/remove the .app, then install fresh or run Check for Updates."
