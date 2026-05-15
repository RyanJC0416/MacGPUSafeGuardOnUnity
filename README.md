# Mac GPU SafeGuard

<div align="center">

**Unity Mac PlayMode 稳定性保护工具**

防止 Unity Editor 在 macOS 上因 GPU 压力、渲染管线异常等问题导致卡死和崩溃

[![Release](https://img.shields.io/github/v/release/RyanJC0416/MacGPUSafeGuardOnUnity)](https://github.com/RyanJC0416/MacGPUSafeGuardOnUnity/releases)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-macOS%2015.0%2B-lightgrey)](https://www.apple.com/macos/)

[功能特性](#功能特性) • [快速开始](#快速开始) • [使用方式](#使用方式) • [近期更新](#近期更新) • [文档](#文档)

</div>

---

## 项目介绍

Mac GPU SafeGuard 是一套针对 Unity Editor 在 macOS 上 PlayMode 稳定性问题的完整解决方案。通过心跳监控、进程管理和快照诊断,帮助开发者:

- 🛡️ **自动检测并恢复卡死** - 心跳监控 + 自动终止 + 快照保存
- ⚡ **快速清理进程** - 0.4 秒终止所有 Unity 进程(含子进程)
- 🔍 **完整诊断信息** - 堆栈采样、日志分析、心跳状态
- 🎯 **无侵入集成** - 一行代码接入,自动后台运行

### 适用场景

- Unity 项目在 Mac 上 PlayMode 频繁卡死
- 自定义 URP/渲染管线导致的 GPU 压力问题
- 大型场景加载时的 Editor 无响应
- 需要快速清理残留的 Unity 进程(Hub/Editor/子进程)

---

## 功能特性

### 🔄 Watchdog 自动监控
- **心跳机制**: Unity 每帧更新心跳文件,外部脚本监控超时
- **卡死检测**: 默认 12 秒超时,可自定义
- **自动恢复**: 检测到卡死后保存快照并终止 Unity
- **PlayMode 感知**: 区分 PlayMode 和编辑模式,避免误杀

### ⚡ 高性能进程清理
- **异步快照**: 后台保存诊断信息,不阻塞终止流程(0.4 秒)
- **极速模式**: 跳过快照,立即终止(0.38 秒)
- **智能识别**: 区分 Unity Editor、Unity Hub 及所有子进程
- **完整清理**: 包含 Licensing、PackageManager、ShaderCompiler 等

### 📊 快照诊断
每次终止时自动保存:
- 进程堆栈采样(sample)
- Unity Editor 日志副本
- 心跳状态与超时时长
- 编译状态(是否在编译中)
- 关键错误日志提取(ShadowCache、崩溃保护等)

### 🖥️ 菜单栏 App
- 一键启动/停止 Watchdog
- 快速访问进程清理工具
- 实时状态显示
- 自动更新检测

---

## 快速开始

### 下载与安装

1. **下载最新版本**
   ```bash
   # 从 Release 页面下载
   # https://github.com/RyanJC0416/MacGPUSafeGuardOnUnity/releases/latest
   ```

2. **解压并移动到应用程序文件夹**
   ```bash
   unzip GpuSafeGuard.app.zip
   mv GpuSafeGuard.app /Applications/
   ```

3. **首次启动授权**
   - 打开 `系统设置 > 隐私与安全性`
   - 允许打开 GpuSafeGuard.app

### Unity 项目集成

在 Unity 项目中添加心跳代码(仅一行):

```csharp
// Assets/Editor/MacGPUSafeGuard.cs
#if UNITY_EDITOR_OSX
using System;
using System.IO;
using UnityEditor;

public static class MacGPUSafeGuard
{
    private static string heartbeatPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Personal),
        "Library/Application Support/MacGPUSafeGuard/heartbeat"
    );

    [InitializeOnLoadMethod]
    public static void StartHeartbeat()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(heartbeatPath));
        EditorApplication.update += UpdateHeartbeat;
    }

    private static void UpdateHeartbeat()
    {
        try
        {
            File.WriteAllText(heartbeatPath, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        }
        catch { }
    }
}
#endif
```

完成!心跳会在 Unity 启动时自动开始。

---

## 使用方式

### 1. 启动 Watchdog 自动监控

**通过菜单栏 App**:
1. 点击菜单栏的 GpuSafeGuard 图标
2. 点击 "Start Watchdog"
3. 状态显示 "Watchdog: ON"

**通过命令行**:
```bash
# 启动监控(默认 12 秒超时)
bash unity_freeze_watchdog.sh --start

# 自定义超时(20 秒)
bash unity_freeze_watchdog.sh --start 20

# 停止监控
bash unity_freeze_watchdog.sh --stop

# 查看状态
bash unity_freeze_watchdog.sh --status
```

### 2. 手动清理 Unity 进程

**通过菜单栏 App**:
1. 点击菜单栏的 GpuSafeGuard 图标
2. 选择 "Kill Tools" 标签页
3. 点击 "Kill All" 或 "Kill Editor Only"

**通过命令行**:
```bash
# 默认模式:终止 Editor,保留 Hub,异步快照(推荐)
bash kill-unity.sh

# 终止 Editor + Hub,异步快照(0.4 秒)
bash kill-unity.sh --all

# 极速模式:跳过快照,立即终止(0.38 秒)
bash kill-unity.sh --no-snapshot --all

# 仅终止 Unity Hub
bash kill-unity.sh --hub

# 列出所有 Unity 进程(不终止)
bash kill-unity.sh --list
```

### 3. 查看诊断快照

快照保存在:
```bash
~/Library/Application Support/MacGPUSafeGuard/snapshots/
```

每个快照包含:
- `Editor.log` - Unity 日志副本
- `sample.txt` - 进程堆栈采样
- `summary.txt` - 诊断摘要(心跳、编译状态、关键错误)

---

## 近期更新

### v1.4.0 (2026-05-15)

**性能提升**
- ⚡ kill-unity.sh 速度提升 **12-15 倍** (5-6 秒 → 0.4 秒)
  - 异步快照保存,不阻塞主流程
  - 减少 sample 采样时长 (5s → 1s)
  - 限制日志 grep 范围 (全文 → 最后 10k 行)
- 🚀 新增 `--no-snapshot` 极速模式 (0.38 秒完成)

**功能增强**
- ✨ 自动更新检测功能
- 📚 完整文档体系 (README、CHANGELOG、USAGE)
- 🔧 增强快照诊断(心跳分析、编译状态)
- 🎨 UI 优化与菜单栏状态显示

**技术改进**
- 优化参数解析,支持 `--no-snapshot` 与其他参数组合
- 增加 `log_watchdog` 记录手动 kill 操作
- .gitignore 排除 `.bak` 和 `.zip` 文件
- 版本号更新到 1.4.0

### v1.3.2 (之前版本)
- Unity Freeze Watchdog 基础实现
- 快照保存与诊断功能
- Manual kill script 基础版本
- PlayMode 检测与 CPU 时间检查

**完整更新日志**: [CHANGELOG.md](CHANGELOG.md)

---

## 文档

- [📖 CHANGELOG.md](CHANGELOG.md) - 完整版本历史
- [📘 USAGE.md](USAGE.md) - kill-unity.sh 详细使用指南
- [🔧 配置与定制](#配置与定制) - 见下方

### 配置与定制

#### Watchdog 配置

编辑 `unity_freeze_watchdog.sh`:
```bash
TIMEOUT_SECONDS=12  # 心跳超时时间
CHECK_INTERVAL=3    # 检查间隔
```

#### Kill Script 性能调优

编辑 `kill-unity.sh`:
```bash
SAMPLE_DURATION=1       # sample 采样时长(秒)
LOG_TAIL_LINES=10000    # 日志 grep 范围(行)
```

或使用环境变量:
```bash
SKIP_SNAPSHOT=1 SAMPLE_DURATION=2 bash kill-unity.sh --all
```

---

## 系统要求

- **操作系统**: macOS 15.0+ (Sequoia)
- **Unity 版本**: Unity 2022.3+ (推荐)
- **权限**: 需要授予 App 执行权限

---

## 常见问题

### Q: Watchdog 误杀怎么办?
A: 若 Unity 执行长时间阻塞操作(如大场景加载),可能触发误杀。建议:
- 调整超时时间(如 20 秒)
- 关键操作前临时停止 Watchdog

### Q: 僵尸进程无法清理?
A: 极端卡死场景下,Unity 子进程可能进入僵尸状态(`?E`/`?Es`)。这些进程需重启系统清理,但不影响新 Unity 启动。

### Q: 快照保存在哪里?
A: `~/Library/Application Support/MacGPUSafeGuard/snapshots/`

### Q: 如何完全卸载?
```bash
# 删除 App
rm -rf /Applications/GpuSafeGuard.app

# 删除数据(可选)
rm -rf ~/Library/Application\ Support/MacGPUSafeGuard
```

---

## 许可证

MIT License - 详见 [LICENSE](LICENSE)

---

## 贡献

欢迎提交 Issue 和 Pull Request!

### 开发环境
```bash
git clone https://github.com/RyanJC0416/MacGPUSafeGuardOnUnity.git
cd MacGPUSafeGuardOnUnity
bash build.sh
```

---

## 致谢

本项目旨在解决 Unity Mac 开发中的实际痛点,感谢所有使用者的反馈和建议。

---

<div align="center">

**如果这个项目帮助到你,请给个 ⭐️ Star!**

[报告问题](https://github.com/RyanJC0416/MacGPUSafeGuardOnUnity/issues) • [功能建议](https://github.com/RyanJC0416/MacGPUSafeGuardOnUnity/issues/new)

</div>
