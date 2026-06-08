# MacGPUSafeGuard Changelog

## v1.8.3 - 2026-06-08 - SceneGuard skybox fallback sampling

### 变更

- SceneGuard **天空盒 fallback** 不再使用硬编码渐变色
- 绘制前从 `RenderSettings.skybox` → URP NepheleSky 材质 → 环境光 采样 `_SkyTint` / `_GroundColor` 等，尽量贴近 Game 天空
- Diagnostics 开启时可查看 `skybox fallback sampled source=...`

## v1.8.2 - 2026-06-08 - P4 New CL in Settings

### 新增

- Settings → **Default CL** 旁增加 **New CL…**，通过 `p4 change -i` 创建 pending changelist 并自动选为默认 CL
- 默认描述：`[Mac 适配] GpuSafeGuard`

## v1.8.1 - 2026-06-08 - Mac GPU Safe Guard apply scope trim

### 变更

- **Mac GPU Safe Guard** Apply / Re-check 不再包含 `SetURPSettings.cs`（仅保留 `MacGPUSafeGuard.cs` + `MacGPUConfig.cs`）
- 删除 bundled `Resources/templates/SetURPSettings.cs`（项目内 URP 切换脚本维持 depot 原版即可）

## v1.8.0 - 2026-06-08 - SceneGuard three-channel apply

### 新增

- **三条独立 Apply 通道**（Settings → P4 / Templates）：
  1. **Mac GPU Safe Guard** — 运行时保护（`Resources/templates/`）
  2. **SceneGuard** — SceneView 修复核心（Fallback + shaders + EcoHooks，`Resources/scene-guard/`）
  3. **SceneGuard Tools** — 诊断脚本（trace / DisableAllFeatures，`Resources/scene-guard-tools/`）
- SceneGuard 核心含：原材质重绘、gizmo 叠加、水体不透明占位
- 维护脚本：`tools/sync_scene_guard_bundles.sh`

### 文档

- `SceneGuard/unity-patches/` git 镜像与 devlog `2026-06-08-sceneview-fallback-stable.md`

## v1.7.0 - 2026-05-28 - Unity Tmp Cleanup

### 新增功能

#### 1. Unity Tmp 文件清理 (Settings 窗口)
- 新增 **Unity Tmp Cleanup** 面板，位于 Settings 窗口 Snapshot Cleanup 下方
- 扫描 `/private/tmp/Unity_*.sample.txt` 和 `unity_console_mirror.log`
- 显示文件数量和总大小
- 一键清理所有 Unity 临时采样文件
- 解决 Unity Editor 长期运行后 `/private/tmp` 堆积数十 GB 的问题

### 实现
- 新增 `UnityTmpCleaner.swift`，参照 `SnapshotManager` 模式实现
- 在 `AppState` 中添加 `unityTmpSizes` 和 `lastUnityTmpDeleteSummary` 状态
- 在 `SettingsWindow` 中添加清理 UI 面板

---

## v1.5.1 - 2026-05-20 - Watchdog 根因分类增强

### 背景
v1.5.0 前发生一次 kill（5/19 22:26），经诊断确认为 ComputeShader kernel 编译卡死主线程引发，
验证了 v1.5.0 RendererFeature 黑名单的必要性。同时发现 watchdog kill summary 缺少根因分类，
排查效率低。

### 新增功能

#### 1. 根因自动分类 (`classify_freeze_cause`)
Kill 时自动扫描 sample 堆栈 + Editor.log 尾部，输出以下分类：
- **ComputeShader kernel compilation hang** — 检测 sample 中 `ComputeShader_CUSTOM_Dispatch` / `CreateKernelVariant` 特征
- **BigWorld resource loading deadlock** — 检测 `[BigWorld]Material is null` 等资源加载错误
- **ShadowCache / Metal GPU timeout** — 检测 `The RT of per object shadow is out of range` 等
- **MagicaCloth compute pipeline hang** — 检测 MagicaCloth 相关堆栈
- **main thread deadlock** — 检测 semaphore/monitor 等待模式
- **async asset loading hang** — 检测 `AsyncGameObjectPool` 等

#### 2. Kill Summary 扩展字段
- 新增 `classification=` 行，包含上述分类结果
- 新增 `--- top blocked call chain (first 8 frames) ---` 快速堆栈摘要
- 新增 `--- recent BigWorld errors ---` 资源加载错误诊断
- 新增 `--- recent ComputeShader / Metal warnings ---` GPU 错误诊断
- 新增 `--- last 30 log lines ---` 尾部日志完整快照

#### 3. 检测模式扩展
- `recent_issue_present()` 新增 `[BigWorld]Material is null` 模式
- sample 签名提取覆盖范围扩展

### Kill 案例分析 (2026-05-19 22:26)
- **原因**: `editor log stagnant for 15s in play mode`
- **分类**: ComputeShader kernel compilation hang (Metal compiler blocking main thread)
- **根因**: ComputeShader_CUSTOM_Dispatch → CreateKernelVariant 阻塞主线程
- **影响**: 主线程完全冻结，heartbeat 消失，进程 CPU 0.3%
- **预防**: v1.5.0 RendererFeature 黑名单可禁用触发源 (SSGI/SSR/HBAO 等)

### 文件变更
- `Resources/watchdog.sh.tmpl` — 新增 `classify_freeze_cause()` 函数，扩展 `snapshot_and_kill()` 诊断输出

---

## v1.5.0 - 2026-05-19 - URP 渲染策略更新

### 背景
Unity 项目 URP 管线新增了大量重型渲染特效（TAA High、SSGI、SSR、体积云、体积光、HBAO、海洋、毛发等），
以及 CameraSystem 默认开启了 `allowHDR=true`、`allowMSAA=true`、`AntialiasingMode.TemporalAntiAliasing High`，
这些配置在 Mac Metal 平台上会导致 GPU 超时崩溃风险显著增加。

### 新增功能

#### 1. 相机抗锯齿控制 (Camera Anti-Aliasing)
- **MacGPUConfig**: 新增 `antiAliasingMode` (0=None, 1=FXAA, 2=SMAA, 3=TAA) 和 `taaQuality` (0-2)
- **MacGPUSafeGuard**: 通过反射访问 `UniversalAdditionalCameraData`，运行时设置 AA 模式 + TAA 质量
- 默认 Mac 平台关闭 TAA（约节省 15-25% GPU）

#### 2. MSAA / HDR 控制
- **MacGPUConfig**: 新增 `allowMSAA` (默认 false) 和 `allowHDR` (默认 false)
- 直接设置 `Camera.main.allowMSAA` / `allowHDR`（UnityEngine 原生属性，无需反射）
- TAA + MSAA 在 Metal 上有驱动兼容性问题

#### 3. RendererFeature 黑名单
- **MacGPUConfig**: 新增 `disabledRendererFeatures[]` 字符串数组，名称部分匹配
- **MacGPUSafeGuard**: 运行时通过反射遍历 URP asset 的 `m_RendererDataList` → `rendererFeatures`，匹配名称并设置 `isActive=false`
- Editor 菜单也同步支持 `Apply All Settings` 一键禁用
- 默认黑名单 14 项：
  - `ScreenSpaceGlobalIllumination` (SSGI, ~20-30% GPU)
  - `ScreenSpaceReflection` (SSR, ~10-15% GPU)
  - `VolumetricClouds` (体积云系统, ~15-25% GPU)
  - `Volumetric Lighting` (体积光照, ~10-15% GPU)
  - `HorizonBasedAmbientOcclusion` (HBAO, ~5-10% GPU)
  - `Fur` (毛发渲染, ~5-10% GPU)
  - `Ocean` + `FastFourierTransform` (海洋系统, ~10-15% GPU)
  - `SubsurfaceScattering` (SSS, ~3-5% GPU)
  - `角色高精度阴影` (~2-5% GPU)
  - `CloudShadow`, `ParticleCloud`, `GlobalVolumeCloud` (~3-5% GPU each)
  - `NepheleSky` (天空系统, ~3-5% GPU)

### 技术要点
- 所有新增功能遵循现有设计原则：**零 URP 命名空间依赖**，全部通过反射 + SerializedObject 实现
- 运行时反射路径覆盖 EcoEngine.URP 自定义分支和标准 URP 两种属性名约定
- Camera.main 为 null 时在 `Start()` 中自动重试
- Editor 侧使用 `SerializedObject.FindProperty()` 直接修改 asset 文件

### 文件变更
- `Resources/templates/MacGPUSafeGuard.cs` — 新增 ~500 行防护逻辑
- `Resources/templates/MacGPUConfig.cs` — 新增 49 行配置字段

---

## 2026-05-15 - kill-unity.sh 性能优化

### 问题
手动杀进程功能因为保存快照耗时导致响应很慢(5+ 秒),体验不佳。

### 优化内容

#### 1. 异步快照保存
- **慢操作后台化**: `sample` 命令和大日志 `grep` 放入后台子进程,不阻塞主流程
- **快速路径**: 基础信息(pid/heartbeat/ps)同步写入,耗时操作异步完成
- **提示改进**: 从"snapshot saved"改为"snapshot started (async)"

#### 2. 减少 sample 采样时长
- **Before**: `sample "$pid" 5` (5 秒)
- **After**: `sample "$pid" 1` (1 秒)
- 可通过 `SAMPLE_DURATION` 变量调整

#### 3. 限制日志 grep 范围
- **Before**: 全文件 grep(Unity 日志可达 GB 级)
- **After**: 只 grep 最后 10000 行(`tail -n $LOG_TAIL_LINES | grep`)
- 避免大文件全文扫描

#### 4. 新增快照跳过开关
- **环境变量**: `SKIP_SNAPSHOT=1`
- **命令行参数**: `--no-snapshot`
- **用法**: 
  ```bash
  # 极速模式,完全跳过快照
  bash kill-unity.sh --no-snapshot --all
  
  # 或使用环境变量
  SKIP_SNAPSHOT=1 bash kill-unity.sh --all
  ```

### 性能对比

| 模式 | Before | After | 提升 |
|------|--------|-------|------|
| 默认(带快照) | ~5-6 秒 | ~0.4 秒 | **12x** |
| 极速(跳过快照) | N/A | ~0.38 秒 | **15x** |

### 测试验证
- ✅ `--no-snapshot --list` 正常列出进程
- ✅ `--no-snapshot --all` 快速杀掉所有进程
- ✅ 默认模式异步保存快照,不阻塞
- ✅ 快照内容完整性保留(sample/grep 在后台完成)

### 向后兼容
- 默认行为不变(仍保存快照,只是异步化)
- 新增参数和环境变量为可选功能
- 快照目录结构和内容格式不变

### 文件变更
- `kill-unity.sh` - 主要优化
- `release/GpuSafeGuard.app/Contents/Resources/kill-unity.sh` - 同步更新
- `kill-unity.sh.bak` - 原始版本备份

---

## 历史版本

(Previous changelog entries here...)
