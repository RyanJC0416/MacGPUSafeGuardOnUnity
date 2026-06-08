# SceneGuard — Stable SceneView Fallback (2026-06-08)

## Status

**User confirmed OK** for Mac Editor SceneView editing and Play-mode inspection.

## Problem

Mac Unity Editor SceneView: URP draws passes but final RT stays empty unless `context.Submit()` runs after a manual resubmit. TCRender materials need a fallback draw path. Transparent water (`TCRender/Water/TCWater`) depends on screen-space buffers and disappears entirely without them.

## Solution (shipped)

Single stable path in `SceneGuardSceneViewFallbackRenderer.cs`:

- **Original materials** batch draw (not gray proxy) after clear + skybox
- **Gizmos** on top: `DrawGizmos` in repair pass + `duringSceneGui` overlay
- **Water placeholder**: opaque `SceneViewWaterFallback` shader after transparent pass
- **Play mirror disabled** (`PlayMirrorPathEnabled = false`); extra `Camera.Render()` caused game LOD flicker and BigWorld multi-camera errors

## P4 changelists (WorkSpace_Ryan_Mac)

| CL | Contents |
|----|----------|
| **200404** | Core: FallbackRenderer, lit/skybox/water shaders, EcoHooks |
| **200405** | Diagnostics: GameVsScene trace, Pipeline trace, DisableAllFeatures |
| ~~200406~~ | Deprecated experiments — deleted; archive under `Library/SceneGuard/archive/` |

## Git mirror

Unity sources: `SceneGuard/unity-patches/Assets/Editor/` in this repository.

## Next (Phase 2, not done)

- App injection of SceneGuard patches (same pattern as `MacGPUSafeGuard.cs` templates)
- Optional menu in GpuSafeGuard.app to copy patches into Unity project via P4
