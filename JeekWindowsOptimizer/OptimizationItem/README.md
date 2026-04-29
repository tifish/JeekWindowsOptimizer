# OptimizationItem 架构说明

`OptimizationItem` 是优化项体系的核心抽象。每个优化项负责描述一个可检测、可切换的系统设置，并向 UI 暴露名称、描述、当前状态和执行入口。

## 目录结构

- `Core`：定义公共抽象和通用执行流程。
- `BuiltIn`：手写优化项，用于实现复杂或需要定制交互的系统行为。
- `DataDriven`：数据驱动优化项，从 `bin\Data\*.tab` 加载配置并生成优化项实例。

## 核心模型

`OptimizationItem` 提供所有优化项共有的行为：

- 通过 `GroupNameKey`、`NameKey`、`DescriptionKey` 接入本地化文本。
- 通过 `Initialize()` 检测当前系统状态，并设置 `IsOptimized`。
- 通过 `SetIsOptimized(bool value)` 统一切换优化状态。
- 由子类实现 `IsOptimizedChanging(bool value)` 完成实际系统修改。
- 通过 `Category` 决定显示在哪个页签。
- 通过 `ShouldUpdateGroupPolicy`、`ShouldRestartExplorer`、`ShouldReboot` 等标志声明后处理需求。

`SetIsOptimized` 是优化项的标准执行入口。它先处理必要的前置条件，例如关闭 Defender 篡改防护或实时保护，然后调用子类的修改逻辑，成功后再按标志执行组策略更新、Explorer 重启或重启提示。

## 加载与分组

`MainViewModel` 负责创建和组织所有优化项：

1. 加载 `RegistryItemManager`、`DriverItemManager`、`ServiceItemManager`、`MicrosoftStoreItemManager` 生成的数据驱动项。
2. 手动注册 `BuiltIn` 下的内置优化项。
3. 按 `OptimizationItemCategory` 分配到不同页签。
4. 按 `GroupNameKey` 合并为 `OptimizationGroup`。
5. 逐项调用 `Initialize()`，并更新页签中的已优化数量。

UI 只绑定分组、名称、描述、勾选状态和优化状态，不直接关心具体系统修改逻辑。

## 批量优化流程

批量执行由 `MainViewModel.OptimizeCheckedItems()` 统一调度：

1. 进入批处理模式，避免每个优化项重复触发全局前置和后处理。
2. 扫描选中且未优化的项目，统一处理 Defender 相关前置条件。
3. 逐项调用 `SetIsOptimized(true)` 执行优化。
4. 汇总各项声明的后处理需求。
5. 统一执行组策略更新、Explorer 重启和重启提示。

这种设计减少了重复弹窗和重复系统操作，也让单项优化与批量优化共用同一套执行入口。

## 数据驱动项

数据驱动项适合结构固定、只需配置参数的优化：

- `RegistryItem`：修改一个或多个注册表值。
- `ServiceItem`：禁用或恢复 Windows 服务。
- `DriverItem`：删除指定驱动文件或目录，失败时引导用户手动卸载。
- `MicrosoftStoreItem`：卸载指定 Microsoft Store 包。

新增简单优化项时，优先考虑修改 `bin\Data` 下对应的 `.tab` 文件，而不是新增代码类。

## 内置项

`BuiltIn` 适合以下场景：

- 需要调用 PowerShell、外部程序或系统 API。
- 需要多步骤判断或特殊恢复逻辑。
- 需要与用户交互，例如提示、打开系统设置页。
- 不能用固定表格字段表达。

内置项应继承 `OptimizationItem`，实现 `Initialize()` 和 `IsOptimizedChanging(bool value)`，并按需设置后处理标志。

## 扩展建议

- 简单注册表、服务、驱动、Microsoft Store 卸载项优先走数据驱动。
- 复杂逻辑才新增 `BuiltIn` 优化项。
- `Initialize()` 只负责读取状态，不应修改系统。
- `IsOptimizedChanging()` 只负责当前优化项的实际切换逻辑。
- 需要影响全局系统状态时，通过 `Should*` 标志声明，让外层统一处理。
- 新增本地化文本时，保持 `NameKey` 和 `DescriptionKey` 与现有命名规则一致。
