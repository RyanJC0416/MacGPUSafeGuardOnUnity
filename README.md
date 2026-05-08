# GpuSafeGuard

Mac 上 Unity Editor 的 GPU 冻结防护工具。

## 作用

在 macOS Metal 环境下开发 Unity 项目时，编辑器可能因 GPU 驱动/渲染栈卡死导致整个系统无响应。GpuSafeGuard 通过后台 watchdog 自动检测并 kill 卡死的 Unity Editor 进程，同时提供一键 kill 和 Unity 脚本注入管理功能。

## 功能

- **Watchdog 自动防护**：后台轮询检测 Unity Editor 是否卡死，自动 kill + 保存快照用于排查
- **Play Mode 智能检测**：只在 Play Mode 下触发自动 kill，Edit Mode 闲置不会误杀
- **一键 Kill 工具**：手动 kill Unity Editor / Unity Hub
- **Unity 脚本注入**：检查/部署 `MacGPUSafeGuard.cs`、`MacGPUConfig.cs`、`SetURPSettings.cs`
- **自动更新**：启动时检查 GitHub release，一键下载安装重启
- **菜单栏 + Dock 双入口**：常驻菜单栏，同时可在程序坞中找到

## 安装

1. 下载 [Latest Release](https://github.com/RyanJC0416/MacGPUSafeGuardOnUnity/releases/latest) 中的 `GpuSafeGuard.app.zip`
2. 解压到任意目录
3. 首次运行前在终端执行（把 `/你的实际路径/` 替换成 app 实际存放的目录）：
   ```bash
   xattr -cr /你的实际路径/GpuSafeGuard.app
   ```
   不知道路径的话，终端里输入 `xattr -cr `（末尾留空格），再把 app 从 Finder 拖到终端，路径会自动补上，然后回车。
4. 双击打开

## 使用

- **主窗口**：Watchdog 开关、日志查看、Kill 工具
- **Settings**：P4 配置、Unity 路径、脚本注入、手动检查更新
- **菜单栏**：快捷开关 Watchdog、Kill Unity、检查更新

## 更新历史

### v1.2.9
- C# 心跳检测：Unity Play Mode 下每 3s 向外部写心跳，watchdog 10s 无心跳自动杀（shader 编译时自动放宽到 20s）
- 覆盖 C# 死循环类卡顿：原有 render-stack 检测覆盖 GPU 渲染冻结，心跳检测覆盖逻辑死循环
- 所有 kill（自动/手动）快照统一增加心跳状态记录，便于后续排查

### v1.2.2
- 修复 GitHub release API 解析失败的问题

### v1.2.1
- Settings 窗口新增 Update 区域（手动检查 + 错误显示）

### v1.2
- 自动更新：启动时检查 GitHub release，一键下载安装重启
- Play Mode 检测：watchdog 只在 Play Mode 下触发 kill
- CPU 时间检查：区分卡死 vs 编译/导入忙碌状态

### v1.1
- 添加 CPU 时间检查避免误杀
- 移除独立的 editor log stagnant kill 逻辑

### v1.0
- 初始版本：watchdog 自动检测 + kill + snapshot
- 菜单栏 + Dock 双入口
- 一键 Kill Unity Editor / Hub
- Unity 脚本注入管理
