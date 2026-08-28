# GpuSafeGuard (Mac GPU SafeGuard)

macOS 上的 Unity Editor 稳定性工具集：**PlayMode GPU 保护**、**SceneView 修复（SceneGuard）**、**卡死监控 Watchdog**、**进程终止与诊断**。

面向 EcoEngine URP / TCRender 项目在 Mac 上常见的：Play 卡死、Scene 窗口黑屏/无贴图、GPU 压力过高等问题。

**当前版本**: v1.8.8 · [Releases](https://github.com/RyanJC0416/MacGPUSafeGuardOnUnity/releases) · 详细变更见 [CHANGELOG.md](CHANGELOG.md)

---

## 核心功能

### 1. GpuSafeGuard.app（图形界面）

- **Watchdog**：监控 Unity 心跳，超时自动快照并终止 Editor
- **Kill Tools**：手动终止 Editor / Hub，可选保存诊断快照
- **Settings → P4 / Templates**：三条独立 Apply 通道，一键写入 Unity 工程并 `p4 edit/add`
- **Check for Updates**：从 GitHub Releases 自动更新（App 须放在 `/Applications/`）

### 2. Mac GPU Safe Guard（运行时 Play 保护）

通过 Settings **Apply Mac GPU Safe Guard** 注入：

| 文件 | 作用 |
|------|------|
| `MacGPUSafeGuard.cs` | PlayMode GPU 降级、心跳、MagicaCloth 隔离、RendererFeature 黑名单、Game 窗口 VG 裁剪修复等 |
| `MacGPUConfig.cs` | 可配置 ScriptableObject |
| `MacVgGpuVpFixFeature.cs` | Metal Play 下 Game 相机 Virtual Geometry 改用 GPU 投影裁剪，修复 URP 更新后转镜头闪物件 |

> 不再修改 `SetURPSettings.cs`；URP 切换维持项目 depot 原版。

### 3. SceneGuard（SceneView 修复）

Mac Editor SceneView 在 Metal/URP 下易出现黑屏或 TCRender 不显示。v1.8.8 起默认在 `endCameraRendering` 中 **clear → 程序化 LDR 天空 → lit overlay DrawRenderers → 水体占位 → Gizmos → Submit**。

原材质重画在 Mac 上会因缺失烘焙集而全白；只 `Submit` 则全黑。当前路径是可编辑的降级视图，不是 Windows 原材质。

**Apply SceneGuard** 注入核心文件（FallbackRenderer、lit/skybox/water shader、EcoHooks 等）。

**v1.8.4+**：无 Unity `Performance` 菜单；Mac Editor 打开工程后 **自动启用** SceneView 修复。

### 4. SceneGuard Tools（诊断，可选）

**Apply SceneGuard tools** 注入 trace 脚本，可通过 `Library/SceneGuard/command.txt` 触发管线对比（无菜单入口），日常编辑可不装。

### 5. Unity Freeze Watchdog（卡死监控）

- **心跳**：Unity 内 `MacGPUSafeGuard.cs` 写 `~/Library/Application Support/MacGPUSafeGuard/heartbeat`
- **检测**：`unity_freeze_watchdog.sh` 默认 12 秒无心跳则快照并 kill
- **快照**：进程栈、Editor.log、MacGPUSafeGuard 相关日志摘要

### 6. Manual Kill（手动终止）

- `kill-unity.sh`：终止 Editor / Hub，异步快照（约 0.4s）或 `--no-snapshot` 极速模式（约 0.38s）

---

## 快速开始

### 安装 App

```bash
git clone https://github.com/RyanJC0416/MacGPUSafeGuardOnUnity.git
cd MacGPUSafeGuardOnUnity
bash build.sh
cp -R GpuSafeGuard.app /Applications/
```

或从 [Releases](https://github.com/RyanJC0416/MacGPUSafeGuardOnUnity/releases) 下载 `GpuSafeGuard.app.zip` 解压到 `/Applications/`。

> macOS App Translocation：未放入 `/Applications/` 时无法使用应用内自动更新。

### 配置 Settings

1. 打开 **GpuSafeGuard → Settings**
2. 填写 **Unity project path**（`client/unity` 根目录）
3. 配置 **P4**（binary / port / client / user），点 **Refresh**
4. **Default CL**：从列表选择，或点 **New CL…** 创建（默认描述 `[Mac 适配] GpuSafeGuard`）
5. 按需 **Apply** 三条通道之一（或全部）：
   - Mac GPU Safe Guard
   - SceneGuard
   - SceneGuard Tools

### 启动 Watchdog（主界面）

1. 填写 Unity 工程路径与 Unity Editor 可执行文件路径
2. 打开 **Watchdog** 开关

---

## 目录与数据路径

```
~/Library/Application Support/MacGPUSafeGuard/
├── heartbeat              # Unity 心跳时间戳
├── compiling              # 编译中标记
├── in_playmode            # PlayMode 标记
├── snapshots/             # kill / watchdog 诊断快照
├── watchdog/watchdog.log
└── updates/               # 应用内更新下载缓存
```

App 内置资源：

```
GpuSafeGuard.app/Contents/Resources/
├── templates/             # Mac GPU Safe Guard（3 个 .cs）
├── scene-guard/           # SceneGuard 核心
└── scene-guard-tools/     # SceneGuard 诊断
```

---

## 命令行（可选）

与 App 内置脚本相同，仓库根目录也可直接运行：

```bash
# Watchdog
bash unity_freeze_watchdog.sh --start 12
bash unity_freeze_watchdog.sh --status
bash unity_freeze_watchdog.sh --stop

# Kill
bash kill-unity.sh              # Editor + 异步快照
bash kill-unity.sh --all        # Editor + Hub
bash kill-unity.sh --no-snapshot
```

详见 [USAGE.md](USAGE.md)。

---

## 典型场景

| 场景 | 做法 |
|------|------|
| PlayMode 卡死 | Apply Mac GPU Safe Guard + 开 Watchdog |
| URP 更新后 Play 下 Game 窗口转镜头闪物件 | Apply Mac GPU Safe Guard（v1.8.5+） |
| Scene 无贴图/黑屏/全白 | Apply SceneGuard（v1.8.8+ 开 Editor 即自动启用 LDR overlay） |
| 补丁写入 P4 | Settings 选 CL → Apply 对应通道 |
| Hub 显示已打开但无 Editor | `kill-unity.sh --all` |

---

## 版本历史（摘要）

| 版本 | 要点 |
|------|------|
| **v1.8.8** | SceneGuard：Mac SceneView LDR overlay，修复全白/全黑 |
| **v1.8.7** | 主窗口 Settings 去焦点圈，拉高窗口不再被标题栏裁掉 |
| **v1.8.6** | 自动更新：下载失败重试，不再误报 Unzip failed |
| **v1.8.5** | 修复 URP 更新后 Play 下 Game 窗口转镜头闪物件（VG GPU 投影） |
| **v1.8.4** | 移除全部 Performance 菜单；SceneGuard / Mac GPU 自动默认 |
| **v1.8.3** | SceneGuard 天空盒 fallback 从材质/环境光采样 |
| **v1.8.2** | Settings 内 **New CL…** 创建 P4 changelist |
| **v1.8.1** | Mac GPU Safe Guard 不再包含 SetURPSettings |
| **v1.8.0** | 三条独立 Apply 通道（runtime / SceneGuard / tools） |

更早版本与细节 → [CHANGELOG.md](CHANGELOG.md)

---

## 相关文档

- [CHANGELOG.md](CHANGELOG.md) — 完整变更记录
- [USAGE.md](USAGE.md) — kill-unity.sh 说明
- [SceneGuard/README.md](SceneGuard/README.md) — SceneGuard 设计与补丁镜像
- [UPDATE_TROUBLESHOOTING.md](UPDATE_TROUBLESHOOTING.md) — 应用内更新问题

---

**测试环境**: macOS 15+ · Unity 2022.3+ · Apple Silicon  
**维护**: [RyanJC0416/MacGPUSafeGuardOnUnity](https://github.com/RyanJC0416/MacGPUSafeGuardOnUnity)
