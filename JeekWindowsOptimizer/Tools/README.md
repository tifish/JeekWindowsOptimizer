# Tools 简介

`Tools` 目录管理应用内的外部工具列表功能。这里的类型负责读取 `bin/Data/Tools.tab`，生成工具分组和工具项，并为主界面提供运行入口。

## 设计定位

- 工具定义来自 `bin/Data/Tools.tab`。
- 工具程序统一放在运行时目录 `bin/Tools` 下。
- 工具路径必须相对 `bin/Tools`，并由代码限制不能跳出该目录。
- 工具启动时使用可执行文件所在目录作为工作目录。

## 类型概览

- `ToolItem`：表示单个外部工具，负责解析路径、启动进程和暴露运行状态。
- `ToolGroup`：表示工具分组，负责本地化分组名称并持有工具项集合。
- `ToolItemManager`：读取 `Tools.tab` 并生成 `ToolItem` 列表。

## 扩展建议

- 新增工具时优先修改 `bin/Data/Tools.tab` 和 `bin/Data/Languages.tab`。
- 不要在这里实现具体系统优化逻辑；系统访问能力应放在 `SystemAccess`，优化行为应放在 `OptimizationItem`。
- 不要随意改动 `bin/Tools` 下随包工具文件。
