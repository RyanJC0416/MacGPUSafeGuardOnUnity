# Mac GPU SafeGuard

Unity Mac PlayMode 稳定性保护工具,用于防止 Unity Editor 在 macOS 上因 GPU 压力过高、渲染管线切换异常等问题导致的卡死和崩溃。

## 核心功能

### 1. Unity Freeze Watchdog (卡死监控)
- **心跳监控**: 通过 Unity 内嵌的 MacGPUSafeGuard.cs 定期更新心跳文件
- **卡死检测**: 外部 `unity_freeze_watchdog.sh` 监控心跳超时(默认 12 秒)
- **自动恢复**: 检测到卡死后自动保存快照并终止 Unity,保护现场
- **快照记录**: 完整记录进程状态、堆栈、Unity 日志等诊断信息

### 2. Manual Kill Script (手动终止)
- **快速清理**: `kill-unity.sh` 提供高性能的 Unity 进程终止能力
- **异步快照**: 后台保存诊断快照,不阻塞主流程 (0.4 秒完成)
- **极速模式**: `--no-snapshot` 参数跳过快照,立即终止 (0.38 秒)
- **智能识别**: 自动区分 Unity Editor 和 Unity Hub 进程
- **完整清理**: 递归终止所有子进程(Licensing、PackageManager、ShaderCompiler 等)

### 3. 进程快照与诊断
- **sample 采样**: 捕获进程堆栈信息(可配置时长)
- **日志分析**: 自动提取 MacGPUSafeGuard 标记、ShadowCache 错误、崩溃跳过绘制等关键信息
- **心跳分析**: 记录最后心跳时间与当前时间差,判断卡死时长
- **编译状态**: 检测 Unity 是否处于编译状态(compiling flag)

## 快速开始

### 安装

```bash
# 克隆仓库
git clone <repo-url> mac-gpu-safeguard
cd mac-gpu-safeguard

# 构建 macOS App
bash build.sh

# ⚠️ 重要：将 App 移动到 /Applications/ 以启用自动更新
cp -R GpuSafeGuard.app /Applications/

# 或手动编译
swiftc -o GpuSafeGuard.app/Contents/MacOS/GpuSafeGuard \
    Sources/*.swift \
    -framework Cocoa
```

> **注意**: 由于 macOS App Translocation 机制，从其他位置运行的 App 无法自动更新。请确保将 `GpuSafeGuard.app` 移动到 `/Applications/` 目录后再使用。

### 使用 Watchdog 自动监控

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

**Unity 内嵌心跳**(需在 Unity 项目中添加):
```csharp
// 在 Unity Editor 初始化时启动心跳
#if UNITY_EDITOR_OSX
MacGPUSafeGuard.StartHeartbeat();
#endif
```

### 手动终止 Unity 进程

```bash
# 默认模式:终止 Editor,保留 Hub,异步保存快照(推荐)
bash kill-unity.sh

# 终止 Editor + Hub,异步快照
bash kill-unity.sh --all

# 极速模式:跳过快照,立即终止(最快 0.38 秒)
bash kill-unity.sh --no-snapshot --all

# 仅终止 Unity Hub
bash kill-unity.sh --hub

# 列出所有 Unity 进程(不终止)
bash kill-unity.sh --list
```

**性能对比**:
- 默认模式(异步快照): **0.4 秒** (vs 旧版 5-6 秒)
- 极速模式(跳过快照): **0.38 秒**

## 配置与定制

### Watchdog 配置

编辑 `unity_freeze_watchdog.sh` 顶部变量:

```bash
HEARTBEAT_PATH="${HOME}/Library/Application Support/MacGPUSafeGuard/heartbeat"
TIMEOUT_SECONDS=12  # 心跳超时时间
CHECK_INTERVAL=3    # 检查间隔
```

### Kill Script 性能调优

编辑 `kill-unity.sh` 顶部变量:

```bash
SAMPLE_DURATION=1       # sample 采样时长(秒)
LOG_TAIL_LINES=10000    # 日志 grep 范围(行)
SKIP_SNAPSHOT=1         # 设为 1 跳过快照
```

或使用环境变量:
```bash
SKIP_SNAPSHOT=1 bash kill-unity.sh --all
```

## 快照目录结构

```
~/Library/Application Support/MacGPUSafeGuard/
├── snapshots/
│   ├── Editor_20260515_202439/
│   │   ├── Editor.log          # Unity 日志副本
│   │   ├── sample.txt          # 进程堆栈采样
│   │   └── summary.txt         # 诊断摘要
│   └── Hub_20260515_203012/
├── watchdog/
│   └── watchdog.log            # Watchdog 运行日志
├── heartbeat                   # Unity 心跳文件(时间戳)
└── compiling                   # Unity 编译标记文件
```

## 诊断快照内容

每次终止时保存的 `summary.txt` 包含:

- **时间戳**: 终止时刻
- **进程信息**: PID、父进程、状态、CPU 占用、运行时长
- **心跳分析**: 最后心跳时间、超时时长
- **编译状态**: 是否处于编译中
- **关键日志**:
  - `[MacGPUSafeGuard]` 标记的所有日志
  - `ShadowCache out of range` 错误
  - `Skipping draw calls to avoid crashing` 崩溃保护
  - `ComputeBuffer none provided` 空缓冲区错误

## 与 Unity 项目集成

### MacGPUSafeGuard.cs (Unity 端)

```csharp
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

### 编译状态标记(可选)

```csharp
// 在编译开始时创建标记
AssetDatabase.importPackageStarted += (packageName) => {
    File.WriteAllText(compilingPath, "1");
};

// 编译完成时删除标记
CompilationPipeline.compilationFinished += (obj) => {
    if (File.Exists(compilingPath)) File.Delete(compilingPath);
};
```

## 命令行参考

### kill-unity.sh

```bash
Usage: kill-unity.sh [options]

Options:
  -h, --help           显示帮助信息
  -l, --list           仅列出进程,不终止
  -e, --editor         终止 Editor,保留 Hub (默认)
  -a, --all            终止 Editor + Hub
  --hub                仅终止 Unity Hub
  --no-snapshot        跳过快照保存,极速终止

Environment:
  SKIP_SNAPSHOT=1      跳过快照(同 --no-snapshot)
  SAMPLE_DURATION=N    设置 sample 采样时长(秒)
  LOG_TAIL_LINES=N     设置日志 grep 范围(行)
```

### unity_freeze_watchdog.sh

```bash
Usage: unity_freeze_watchdog.sh {--start [timeout]|--stop|--status}

Commands:
  --start [N]    启动监控,可选超时秒数(默认 12)
  --stop         停止监控
  --status       查看监控状态
```

## 版本历史

### v1.4.0 (2026-05-15)
- ✨ **性能优化**: kill-unity.sh 速度提升 12-15x (0.4 秒 vs 5-6 秒)
  - 异步快照保存,不阻塞主流程
  - 减少 sample 采样时长 (5s → 1s)
  - 限制日志 grep 范围 (全文 → 最后 10000 行)
- ✨ **新增极速模式**: `--no-snapshot` 参数,0.38 秒完成终止
- 🐛 **修复**: `--no-snapshot` 参数解析 bug

### v1.3.2 (之前版本)
- Unity Freeze Watchdog 基础实现
- 快照保存与诊断功能
- Manual kill script 基础版本

## 使用场景

### 场景 1: PlayMode 卡死自动恢复
1. Unity 项目集成 `MacGPUSafeGuard.cs` 心跳
2. 启动 watchdog: `bash unity_freeze_watchdog.sh --start`
3. 进入 PlayMode 测试
4. 若卡死超过 12 秒,watchdog 自动保存快照并终止 Unity
5. 查看快照诊断原因: `~/Library/Application Support/MacGPUSafeGuard/snapshots/`

### 场景 2: Unity Hub 进程残留清理
```bash
# Unity Hub 显示"项目已打开",但实际没有 Editor 运行
bash kill-unity.sh --all

# 验证清理完成
bash kill-unity.sh --list
```

### 场景 3: 紧急终止(极速)
```bash
# Unity Editor 严重卡死,需要立即终止
bash kill-unity.sh --no-snapshot

# 或批量清理多个 Unity 实例
SKIP_SNAPSHOT=1 bash kill-unity.sh --all
```

## 已知问题

1. **僵尸进程残留**: 某些极端卡死场景下,Unity 子进程可能进入僵尸状态(`?E`/`?Es`),需重启系统清理。但这不影响新 Unity 启动。

2. **Watchdog 误杀**: 若 Unity 执行长时间阻塞操作(如大型场景加载),可能触发误杀。建议调整超时时间或在关键操作前临时停止 watchdog。

3. **权限问题**: 首次运行可能需要授予脚本执行权限:
   ```bash
   chmod +x kill-unity.sh unity_freeze_watchdog.sh
   ```

## 许可证

MIT License - 详见项目根目录 LICENSE 文件

## 贡献

欢迎提交 Issue 和 Pull Request!

## 相关文档

- [CHANGELOG.md](CHANGELOG.md) - 详细变更历史
- [USAGE.md](USAGE.md) - kill-unity.sh 详细使用指南
- [unity_freeze_log_20260515.md](../docs/unity_freeze_log_20260515.md) - 实际卡死案例分析

---

**项目状态**: ✅ 生产就绪  
**测试环境**: macOS Sequoia 15.0+, Unity 2022.3+  
**维护者**: Ryan Ji
