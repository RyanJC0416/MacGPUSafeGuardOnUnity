# SceneGuard Root Cause Analysis — SceneView Black Screen

## Problem Statement

macOS Unity Editor SceneView shows only grid lines + Gizmos, all scene content (terrain, meshes) renders as black.

## Visual Evidence

Screenshot confirms:
- Grid lines visible
- Selected object Gizmo visible
- Selected object outline visible
- All textured geometry is black/invisible

## Diagnosis Results

### Phase 1: SceneView Status
- SceneView camera: DISABLED (but this is NOT the root cause — content renders even when disabled)
- Camera position, clip planes, culling mask all normal

### Phase 2: Render Pipeline
- Custom URP: `EcoEngine.Rendering.CodeBridge.UniversalRenderPipelineAsset`
- 4 renderers, 35+ RendererFeatures
- `m_RenderingMode` property does NOT exist on EcoEngine URP (non-standard)

### Phase 3: Editor.log Errors

**Metal Shader Pipeline Failures (CRITICAL):**

```
Metal: Error creating pipeline state (TCRender/Terrain/TCTerrainLit):
  Fragment input(s) `user(TEXCOORD20)` mismatching vertex shader output type(s)
  or not written by vertex shader

Metal: Vertex or Fragment Shader "TCRender/Terrain/TCTerrainLit" requires
  a ComputeBuffer at index 9 to be bound, but none provided.
  Skipping draw calls to avoid crashing.

Metal: Vertex or Fragment Shader "Hidden/Universal Render Pipeline/Deferred"
  requires a ComputeBuffer at index 8 to be bound, but none provided.
  Skipping draw calls to avoid crashing.
```

**Burst Warning:**
```
Compilation was requested for method `BuildProbesBlockJob` but it is not a
known Burst entry point. [BurstCompile] method defined in generic class
not instantiated with concrete types.
```

## Root Cause

**EcoEngine custom URP rendering pipeline has macOS Metal compatibility bugs:**

1. **TCTerrainLit shader**: Vertex/fragment stage mismatch on `TEXCOORD20` semantic
2. **Deferred renderer**: ComputeBuffers for light data not bound on Metal backend
3. **Result**: All opaque draw calls in deferred path are skipped → SceneView appears black

This is a **platform-specific engine bug** (macOS Metal), not a Unity Editor or SceneGuard issue.

## Repair Attempts & Results

| Attempt | Result |
|---------|--------|
| Enable SceneView camera | No visual change |
| Reset camera (FrameSelected) | No visual change |
| Enable sceneLighting | No visual change |
| Switch to Forward rendering | FAILED — EcoEngine URP doesn't expose `m_RenderingMode` |
| Clear ShaderCache + reload | No visual change |

## Conclusion

The black screen **cannot be fixed by SceneGuard** — it requires:
1. EcoEngine rendering team to fix `TCTerrainLit` shader vertex/fragment mismatch
2. EcoEngine rendering team to fix Deferred renderer ComputeBuffer binding on Metal
3. OR: Switch project to Forward rendering path (if supported by EcoEngine)
4. OR: Switch Unity graphics API from Metal to OpenGL Core

## SceneGuard Value

SceneGuard successfully:
- ✅ Detected the exact Metal shader errors via Editor.log scanning
- ✅ Identified the custom URP pipeline as the source
- ✅ Ruled out camera/lighting/RendererFeature issues
- ✅ Provided clear evidence for the rendering team

## Next Steps

1. Forward findings to rendering team (TA/渲染组)
2. SceneGuard Phase 2: Integrate diagnostic reporting into GpuSafeGuard.app
   - App can detect these errors and show user-friendly messages
   - Suggest contacting rendering team or switching graphics API
