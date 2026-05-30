# SceneGuard Design

## 背景与问题

macOS Unity Editor 的 SceneView 长期处于黑屏不可用状态，只能看到黑色背景和 Gizmos，无法看到光照、贴图和场景内容。这不是偶发异常，而是一个持续性的 Editor 渲染管线问题。

旧版方案 `MacSceneGuard.cs`（独立 C# 脚本）已被废弃。当前 `GpuSafeGuard.app` 是完整的产品形态，SceneGuard 应以子模块形式融合进 App。

## 设计目标

1. **先验证业务逻辑可行性**：严格按诊断 → 定位 → 修复的流程，在 Unity 内验证能恢复 SceneView
2. **再产品化整合**：把验证成功的逻辑固化进 `GpuSafeGuard.app` 和 `MacGPUSafeGuard.cs`
3. **不猜测、不盲目修复**：所有修复操作必须基于诊断结果

## 总体架构

整个工作分为两个严格分离的阶段：

```
PHASE 1: 可行性验证（Unity 内，不碰 App）
├── 临时 Editor 脚本: SceneGuardDiagnostics.cs
├── 诊断: 枚举 SceneView / URP / Renderer / 光照 状态
├── 定位: 交叉比对 Editor.log 中的 Metal/Shader 错误
└── 修复: 针对性尝试修复，观察 SceneView 是否恢复

PHASE 2: 产品化整合（App + Unity 双向）
├── Unity 侧: MacGPUSafeGuard.cs 新增 #region SceneGuard
└── App 侧: 新增 SceneGuardTab + 菜单栏入口
```

**关键原则：**
- Phase 1 的脚本可以随时删掉，不影响任何现有代码
- Phase 2 只固化 Phase 1 验证成功的逻辑，不引入新猜测
- App 在 Phase 1 完全不动

## Phase 1: Unity 内诊断脚本

### 脚本位置

`Assets/Editor/SceneGuardDiagnostics.cs`（Editor-only，不进入 Runtime，不参与构建）

### 菜单项

- `Performance → SceneGuard Diagnostics → Run Full Diagnosis` — 完整诊断
- `Performance → SceneGuard Diagnostics → Attempt Repair` — 基于上一次的诊断结果执行修复

### 诊断维度（按优先级排序）

| 优先级 | 维度 | 检查内容 |
|--------|------|----------|
| P0 | SceneView 自身 | `SceneView.lastActiveSceneView` 是否为 null；SceneView 相机是否存在、是否启用、culling mask、clear flags、背景色 |
| P0 | 渲染管线 | `GraphicsSettings.renderPipelineAsset` 是否为 null；当前 URP Asset 类型；RendererDataList 完整性 |
| P0 | Editor.log 错误 | 最近 100 行内是否有 `Error creating pipeline state`、`Fragment input mismatch`、`fallback shader not found` |
| P1 | RendererFeature | 所有 RendererData 中的 RendererFeature：active 状态、名称、是否含 `ShowInSceneView` 字段及其值 |
| P1 | 光照与环境 | 场景主光源数量/类型、环境光设置、光照贴图是否启用 |
| P2 | 兼容性标记 | `PlayerSettings` 中的 Metal API 相关设置、URP Asset 的 MSAA/HDR 设置 |

### 输出格式

诊断结果输出到 Console 和 `~/Library/Logs/Unity/Editor.log`，格式统一为：

```
[SceneGuardDiagnostics] === Phase 1: SceneView Status ===
[SceneGuardDiagnostics] SceneView.lastActiveSceneView: <null|valid>
[SceneGuardDiagnostics] SceneView camera: <null|disabled|enabled, clear=<X>, mask=<Y>>
[SceneGuardDiagnostics] RenderPipelineAsset: <null|type_name>
[SceneGuardDiagnostics] RendererData[0]: <name>, features=<N>
[SceneGuardDiagnostics]   Feature[<i>]: <name>, active=<T|F>, ShowInSceneView=<T|F|N/A>
[SceneGuardDiagnostics] Editor.log errors: <N> found
[SceneGuardDiagnostics]   [ERROR] <error_text>
[SceneGuardDiagnostics] === Assessment: <HEALTHY|DEGRADED|BROKEN> ===
[SceneGuardDiagnostics] Suspected root cause: <description>
```

### 修复策略（尝试顺序）

修复不是盲目全做，而是**根据诊断结果选择性地执行**：

1. 如果 `ShowInSceneView` 为 false → 设为 true
2. 如果 SceneView 相机异常 → 重置 SceneView 相机（反射调用 `SceneView.RestoreCameraState()` 或等效操作）
3. 如果 `GraphicsSettings.renderPipelineAsset` 为 null → 恢复为项目默认 URP Asset
4. 如果存在已知的黑名单 RendererFeature active → 临时禁用，观察 SceneView
5. 如果以上均无效 → 强制刷新 SceneView（`SceneView.Repaint()` + 相机重置）

每次修复操作后立即输出结果，并提示用户观察 SceneView 是否恢复。

## Phase 2: 产品化整合

### C# 侧 — 逻辑固化

验证成功的诊断+修复逻辑从临时脚本迁移至 `MacGPUSafeGuard.cs`，以 `#region SceneGuard (Editor-only)` 隔离：

| 来源（Phase 1） | 固化后位置 |
|-----------------|------------|
| `SceneGuardDiagnostics.cs` 的诊断方法 | `MacGPUSafeGuard.cs` 中的 `EditorDiagnoseSceneView()` |
| `SceneGuardDiagnostics.cs` 的修复方法 | `MacGPUSafeGuard.cs` 中的 `EditorRepairSceneView()` |
| 菜单项 | `Performance → Mac GPU SafeGuard → SceneGuard: Diagnose` / `Repair Scene View` |

### App ↔ Unity 通信协议

沿用现有的 heartbeat 文件通信范式：

| 文件 | 方向 | 用途 |
|------|------|------|
| `sceneguard_repair_request` | App → Unity | App 请求修复，Unity 检测到后执行并删除 |
| `sceneguard_status.json` | Unity → App | 包含 `lastRepairTime`, `sceneViewHealthy`, `issuesFound[]` |
| `Editor.log` 中的 `[MacSceneGuard]` | Unity → App | App 通过 tail/grep 解析日志做实时状态判断 |

### App 侧 — UI 扩展

新增 `SceneGuardTab`（主窗口第三个 Tab）：

```
SceneGuard
Status: [Unknown / Broken / OK]

[Run Diagnosis]  [Repair Scene View]

── Last Report ──
SceneView: <status>
RenderPipeline: <status>
RendererFeatures: <N active>
Root Cause: <description>

── Log ──
<scrollable log output>
```

菜单栏同步增加：`SceneGuard → Repair Scene View`

## DevLog 规范

DevLog 统一放在 Git 仓库的 `SceneGuard/devlogs/` 目录下，随代码一起提交到 GitHub。

每次修改脚本或执行诊断后，在 `SceneGuard/devlogs/YYYY-MM-DD-<主题>.md` 记录：
- 修改内容（脚本版本）
- 诊断输出摘要
- 修复尝试及结果
- 如果失败：失败现象 + 根因假设 + 下一步

示例文件名：
- `2026-05-30-phase1-kickoff.md`
- `2026-05-30-diagnosis-v1.md`

## 验收标准

### Phase 1 验收
- [ ] 诊断脚本编译通过，无 CS 错误
- [ ] 运行诊断后输出完整的状态报告
- [ ] 至少找到 1 个导致 SceneView 黑屏的根因
- [ ] 修复操作能让 SceneView 恢复正常（能看到光照和贴图）
- [ ] DevLog 记录了完整的诊断和修复过程

### Phase 2 验收
- [ ] `MacGPUSafeGuard.cs` 新增 SceneGuard 区域，编译通过
- [ ] App 新增 SceneGuardTab，能显示状态和触发修复
- [ ] App 和 Unity 双向通信正常
- [ ] SceneGuard 功能只在 macOS Editor 生效
