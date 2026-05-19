# MacGPUSafeGuard Changelog

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
