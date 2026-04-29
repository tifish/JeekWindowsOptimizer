# AGENTS.md

本文件为在本仓库工作的编码代理提供项目约定。作用范围为整个仓库。

## 项目概览

JeekWindowsOptimizer 是一个基于 Avalonia 的 Windows 桌面优化工具，主项目目标框架为 `net10.0-windows`。应用通过优化项体系读取和修改 Windows 设置，涉及注册表、WMI、PowerShell、Windows 服务、Microsoft Store 包和 Win32 API。

## 目录结构

- `JeekWindowsOptimizer/`：主桌面应用。
  - `Views/`：Avalonia UI 和主视图模型。
  - `OptimizationItem/`：优化项抽象、内置优化项和数据驱动优化项。
  - `Tools/`：Windows 系统能力封装。
  - `Assets/`：应用资源。
- `JeekTools.NET/`：通用工具库，包含注册表、命令执行、日志、集合扩展等辅助能力。
- `PowerManagerAPI/`：电源管理相关库。
- `bin/Data/`：运行时数据文件，驱动部分优化项生成。

## 常用命令

在仓库根目录执行：

```powershell
dotnet restore JeekWindowsOptimizer.sln
dotnet build JeekWindowsOptimizer.sln
dotnet build JeekWindowsOptimizer.sln -c Release
```

仅文档改动通常不需要运行构建。涉及 C#、XAML、项目文件或数据文件时，优先运行 `dotnet build JeekWindowsOptimizer.sln`。

## 代码约定

- 使用 C# nullable 和 implicit usings 的现有风格。
- 优先沿用当前的 file-scoped namespace、集合表达式和 CommunityToolkit.Mvvm 源生成属性写法。
- UI 逻辑集中在 `Views/MainViewModel.cs` 和 `Views/MainWindow.axaml`，不要把系统修改逻辑写进 XAML code-behind。
- 系统能力优先封装在 `Tools/`，优化项只组合这些能力。
- 新增优化项时，简单注册表、服务、驱动、Microsoft Store 卸载项优先走 `bin/Data/*.tab` 数据驱动；复杂逻辑才新增 `BuiltIn` 类。
- `OptimizationItem.Initialize()` 只读取状态，不应修改系统。
- `OptimizationItem.IsOptimizedChanging()` 负责单个优化项的实际切换逻辑。
- 需要组策略更新、Explorer 重启、重启提示等全局后处理时，使用 `Should*` 标志，让外层统一处理。

## 本地化与数据

- 展示文本通过 `Jeek.Avalonia.Localization` 和 `bin/Data/Languages.tab` 管理。
- 优化项的 `GroupNameKey`、`NameKey`、`DescriptionKey` 必须能在本地化数据中找到对应文本。
- 修改 `bin/Data/*.tab` 时保持列数和现有命名规则一致。

## 注意事项

- 该项目会修改真实 Windows 系统设置。不要在没有明确需求时新增启动、停止、删除或注册表写入行为。
- PowerShell 复用全局 `PowerShellService`，新增调用前应清理 `PowerShellService.Commands`。
- 不要随意改动 `bin/Tools/Activator` 等随包工具文件。
- 保持文档简洁，偏架构和维护说明，避免生成大段 API 参考。
