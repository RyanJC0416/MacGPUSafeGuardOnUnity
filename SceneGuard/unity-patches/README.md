# SceneGuard Unity Patches (Mac Editor)

Verified **2026-06-08** on Unity 2022.3 / Metal / EcoEngine URP.

Copy everything under `Assets/Editor/` into the Unity project at the same relative path:

```
<unity-project>/Assets/Editor/
```

## Stable behavior (default)

| Item | Value |
|------|--------|
| Menu | `Performance/SceneGuard/SceneView TCRender Fallback Enabled` **ON** |
| Repair mode | **Submit + Original Materials** |
| Play mirror | **OFF** (`PlayMirrorPathEnabled = false` in source) |
| EcoHooks | **OFF** (EditorPrefs migration) |

**Render flow** (`endCameraRendering`, SceneView camera only):

1. Clear RT → skybox (scene or fallback)
2. `DrawRenderers` opaque + transparent (original materials)
3. Water placeholder (`TCRender/Water/*` → opaque teal fallback shader)
4. `ScriptableRenderContext.DrawGizmos` + `Handles.DrawGizmos` (duringSceneGui)
5. `context.Submit()` (required on Mac Metal)

Edit and Play both use this path. GameView is unchanged.

## Files

### Core (P4 CL 200404)

| File | Role |
|------|------|
| `SceneGuardSceneViewFallbackRenderer.cs` | Main repair + menus + diagnostics |
| `SceneGuardSceneViewLitFallback.shader` | Legacy proxy lit (Proxy Fallback mode) |
| `SceneGuardSceneViewSkyboxFallback.shader` | Skybox when RenderSettings.skybox unusable |
| `SceneGuardSceneViewWaterFallback.shader` | Opaque water placeholder (no screen-space buffers) |
| `SceneGuard/SceneGuardSceneViewEcoEngineHooks.cs` | Optional EcoEngine hooks (default off) |

### Diagnostics (P4 CL 200405)

| File | Role |
|------|------|
| `SceneGuardGameVsSceneViewTrace.cs` | Game vs SceneView comparison trace |
| `SceneGuardSceneViewPipelineTrace.cs` | Pipeline / RT trace |
| `SceneGuardDisableAllFeatures.cs` | One-shot RendererFeature disable helper |

## Not shipped (archived locally)

Deprecated experiments (Harmony hijack, NativePath, DepthToR, SimpleTonemap) live under:

`WorkSpace_Ryan_Mac/client/unity/Library/SceneGuard/archive/cl200406_deprecated_experiments_20260608/`

## Known limitations

- SceneView ≠ GameView: no volumetric clouds, exposure, full water SSR/refraction
- Water is a **solid placeholder** so lakes/rivers remain visible in Scene
- Play-mode game-camera mirror remains in source but disabled (LOD / multi-camera side effects)

## Validation

```bash
echo trigger > <unity>/Library/BgCompile/trigger.txt
# expect Library/BgCompile/last_result.json → state=ok, errors=0
```

Last BgCompile: `build_seq=1578`, `errors=0`.
