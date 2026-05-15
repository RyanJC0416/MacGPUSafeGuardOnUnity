# kill-unity.sh 使用文档

## 概述

`kill-unity.sh` 是 MacGPUSafeGuard 的 Unity 进程管理工具,用于:
- 快速终止卡死/僵死的 Unity Editor 或 Unity Hub 进程
- 保存进程快照用于事后分析(可选)
- 清理项目 lockfile,解决"project already open"问题

## 基础用法

```bash
# 杀掉 Unity Editor(保留 Hub)
bash kill-unity.sh

# 杀掉 Unity Hub
bash kill-unity.sh --hub

# 杀掉 Editor + Hub
bash kill-unity.sh --all

# 只列出进程,不杀
bash kill-unity.sh --list

# 查看帮助
bash kill-unity.sh --help
```

## 性能优化(2026-05-15)

### 快照模式对比

| 模式 | 耗时 | 快照 | 适用场景 |
|------|------|------|----------|
| **默认模式** | ~0.4秒 | 异步保存 | 日常使用,既快速又保留诊断信息 |
| **极速模式** | ~0.38秒 | 跳过 | 紧急情况,只要快速杀进程 |
| **旧版** | ~5-6秒 | 同步保存 | 已废弃 |

### 极速模式

当你只想快速杀进程,不需要快照时:

```bash
# 方式 1: 使用 --no-snapshot 参数
bash kill-unity.sh --no-snapshot --all

# 方式 2: 使用环境变量
SKIP_SNAPSHOT=1 bash kill-unity.sh --all
```

### 默认模式(推荐)

**异步快照**已优化到 0.4 秒,无需跳过:

```bash
# 快速杀进程,快照在后台保存
bash kill-unity.sh --all
```

输出示例:
```
Killing Unity Editor + Hub:

  Editor: no process
snapshot started (async) => /Users/ryan/Library/.../snapshots/Hub_20260515_202729
  Hub: killing 7 process(es)
  Hub: done
```

## 快照内容

快照保存位置: `~/Library/Application Support/MacGPUSafeGuard/snapshots/`

每个快照包含:
- `Editor.log` - Unity 日志副本
- `sample.txt` - 进程堆栈采样(1 秒)
- `summary.txt` - 汇总信息:
  - 进程 PID/状态/CPU/运行时长
  - heartbeat 心跳时间戳
  - 近期 MacGPUSafeGuard 日志
  - 近期 GPU 错误日志
  - 近期 crash 日志

## 配合 shutdown_unity.sh 使用

完整的清理流程(推荐):

```bash
# 1. 杀掉所有 Unity 进程
bash /Users/ryan/Perforce/mac-gpu-safeguard/kill-unity.sh --all

# 2. 清理 lockfile 并检查残留
bash /Users/ryan/Perforce/Skills/shutdownUnity/scripts/shutdown_unity.sh \
  "/Users/ryan/Perforce/WorkSpace_Ryan_Mac/client/unity"

# 3. 重新打开 Unity Hub 或直接启动 Editor
open -a "Unity Hub"
```

## 性能调优参数

如需进一步调整,编辑 `kill-unity.sh` 顶部:

```bash
# 进程采样时长(秒)
SAMPLE_DURATION=1  # 默认 1 秒,可改为 0 跳过采样

# 日志 grep 范围(行数)
LOG_TAIL_LINES=10000  # 只 grep 最后 N 行

# 跳过快照(默认关闭)
SKIP_SNAPSHOT=0  # 改为 1 则默认跳过快照
```

## 故障排查

### 僵尸进程无法清理

症状:
```
  Hub: WARN, still running: 12345 12346
```

原因:进程卡在内核退出态(`?E`/`?Es`),`kill -9` 无效。

解决:
- 僵尸进程**不会**阻止新 Unity 启动
- 只占用 PID,不占用文件/端口
- 只有重启系统才能清除
- 建议:暂时忽略,正常启动 Unity

### Unity Hub 仍提示"already open"

可能原因:
- lockfile 未清理 → 运行 `shutdown_unity.sh`
- 文件句柄未释放 → 用 `lsof -Fn <project>` 检查
- Unity Hub 缓存 → 重启 Hub

## 参考

- 卡死分析: `/Users/ryan/Perforce/docs/unity_freeze_log_20260515.md`
- 进程清理记录: `/Users/ryan/Perforce/docs/unity_process_cleanup_20260515_2024.md`
- Changelog: `CHANGELOG.md`
