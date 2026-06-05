# SceneGuard Fix Confirmation — Forward Rendering Workaround

## Date
2026-06-05

## Problem
macOS Unity Editor SceneView 全黑，无法显示任何场景内容（mesh、光照、贴图）。
GameView 正常。

## Previous Failed Attempts

| Attempt | Result |
|---------|--------|
| Dummy ComputeBuffer 绑定 `g_VoxelLightBuffer` / `g_VoxelProbeBuffer` | 无效，cube 依然不可见 |
| UACD 自动补全 (`GetUniversalAdditionalCameraData`) | 临时有效，SceneView camera 重建后失效 |
| `TCDeferred.hlsl` depth fallback (`UNITY_EDITOR` 分支) | 单独使用无效，depth texture 仍绑定到 color RT |

## Root Cause (Confirmed)

EcoEngine 自定义 URP + Deferred rendering + macOS Metal 组合下，SceneView 存在多重缺失：

1. `_CameraDepthTexture` 被绑定到 color texture (`R16G16B16A16_SFloat`) 而非 depth texture
2. SceneView 使用无 UACD 的渲染分支，未绑定 custom light/probe StructuredBuffer
3. Metal driver 因缺失 ComputeBuffer 而跳过 GBuffer draw call
4. `TCDeferred.hlsl` 的 depth 检查命中 far-clip，输出全黑

**结论：Deferred rendering 在 SceneView 的渲染链路上有多处断裂，任何单点修复都无法恢复。**

## Successful Fix

**策略：编译完成后自动将所有 renderer 切换为 Forward rendering**

### 修改内容

#### 1. `Assets/Editor/SceneGuardRepair.cs` (新增)
- `[InitializeOnLoadMethod]` 自动运行诊断 + 修复
- `TryForceForwardInEditor()`: 遍历 4 个 renderer data asset，将 `m_RenderingMode` 从 1(Deferred) 改为 0(Forward)，写盘并 rebuild pipeline
- `OnBeginCameraRendering`: 继续保留 dummy ComputeBuffer 绑定作为兜底
- `SceneGuardBuildPreprocessor`: 实现 `IPreprocessBuildWithReport`，打包前自动恢复 Deferred

#### 2. Renderer Data Assets (修改)
- `Assets/Settings/urp_renderer.asset` — Deferred → Forward
- `Assets/Settings/urp_role_renderer.asset` — Deferred → Forward
- `Assets/Settings/urp_ui_renderer.asset` — Deferred → Forward
- `Assets/Settings/urp_renderer_for_ui_scene.asset` — Deferred → Forward

#### 3. `TCDeferred.hlsl` (修改，保留)
- `UNITY_EDITOR` 分支下加入 SceneView depth fallback，作为双重保险

#### 4. Bugfix (2026-06-05 追加)
- 诊断文件写入 `Library/SceneGuard/evidence_diagnosis.txt` 时捕获 `UnauthorizedAccessException`

## Verification

- **BgCompile**: seq 1274, errors=0, warnings=0 ✅
- **SceneView**: Cube 正常显示（粉色材质、光照、阴影均可见）✅
- **GameView**: 正常 ✅
- **Forward 状态持久化**: 每次编译后自动检测并确保 Forward ✅

## Files Changed (P4 changelist 193081)

```
Assets/Editor/SceneGuardRepair.cs                          (add)
Assets/Settings/urp_renderer.asset                         (edit)
Assets/Settings/urp_role_renderer.asset                    (edit)
Assets/Settings/urp_ui_renderer.asset                      (edit)
Assets/Settings/urp_renderer_for_ui_scene.asset            (edit)
UserPackages/.../ShaderLibrary/TCRenderFramework/TCDeferred.hlsl (edit)
```

## Next Steps

1. Phase 1 验收完成 — SceneView 可正常显示
2. Phase 2: 将 SceneGuard 逻辑固化进 `GpuSafeGuard.app`
   - App 检测 SceneView 黑屏时自动触发 Forward switch
   - 或提供手动修复按钮
3. 构建测试：验证 `SceneGuardBuildPreprocessor` 是否成功恢复 Deferred
