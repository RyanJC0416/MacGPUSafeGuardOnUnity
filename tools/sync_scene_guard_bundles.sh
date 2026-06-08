#!/usr/bin/env bash
# Split SceneGuard/unity-patches → Resources/scene-guard (core) + scene-guard-tools (diag).
set -euo pipefail
cd "$(dirname "$0")/.."
SRC_ROOT="SceneGuard/unity-patches/Assets/Editor"
CORE_DEST="Resources/scene-guard/Assets/Editor"
TOOLS_DEST="Resources/scene-guard-tools/Assets/Editor"

if [ ! -d "$SRC_ROOT" ]; then
  echo "Missing $SRC_ROOT" >&2
  exit 1
fi

CORE_FILES=(
  SceneGuard.meta
  SceneGuardSceneViewFallbackRenderer.cs
  SceneGuardSceneViewFallbackRenderer.cs.meta
  SceneGuardSceneViewLitFallback.shader
  SceneGuardSceneViewLitFallback.shader.meta
  SceneGuardSceneViewSkyboxFallback.shader
  SceneGuardSceneViewSkyboxFallback.shader.meta
  SceneGuardSceneViewWaterFallback.shader
  SceneGuardSceneViewWaterFallback.shader.meta
)

TOOLS_FILES=(
  SceneGuardDisableAllFeatures.cs
  SceneGuardDisableAllFeatures.cs.meta
  SceneGuardGameVsSceneViewTrace.cs
  SceneGuardGameVsSceneViewTrace.cs.meta
  SceneGuardSceneViewPipelineTrace.cs
  SceneGuardSceneViewPipelineTrace.cs.meta
)

rm -rf Resources/scene-guard Resources/scene-guard-tools
mkdir -p "$CORE_DEST/SceneGuard" "$TOOLS_DEST"

for f in "${CORE_FILES[@]}"; do
  cp "$SRC_ROOT/$f" "$CORE_DEST/$f"
done
cp "$SRC_ROOT/SceneGuard/SceneGuardSceneViewEcoEngineHooks.cs" "$CORE_DEST/SceneGuard/"
cp "$SRC_ROOT/SceneGuard/SceneGuardSceneViewEcoEngineHooks.cs.meta" "$CORE_DEST/SceneGuard/"

for f in "${TOOLS_FILES[@]}"; do
  cp "$SRC_ROOT/$f" "$TOOLS_DEST/$f"
done

echo "Core  → $CORE_DEST (${#CORE_FILES[@]} files + EcoHooks)"
echo "Tools → $TOOLS_DEST (${#TOOLS_FILES[@]} files)"
