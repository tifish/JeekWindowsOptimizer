# Tools 简介

`Tools` 目录封装了优化项需要访问的 Windows 系统能力。这里的类型不负责 UI 展示，也不直接管理优化项生命周期，而是为 `OptimizationItem` 和 `MainViewModel` 提供可复用的底层操作。

## 设计定位

- 隔离 WMI、PowerShell、注册表、Win32 API 等系统调用细节。
- 为优化项提供更小、更明确的接口，避免业务类直接拼装底层调用。
- 保持工具类无状态或弱状态，方便多个优化项复用。
- 将失败处理留给调用方，工具类只在必要时记录日志或返回结果。

## 工具类概览

- `AntiVirus`：通过 WMI 查询系统安全中心中的防病毒产品，用于判断是否安装第三方杀毒软件。
- `Battery`：通过 WMI 查询 `Win32_Battery`，用于判断当前设备是否有电池。
- `MicrosoftStore`：通过共享 PowerShell 实例导入 `AppX` 模块，检查和卸载 Microsoft Store 包。
- `PowerShellService`：提供全局共享的 `PowerShellService` 实例，供需要执行 PowerShell 命令的模块复用。
- `WindowsService`：通过 WMI 控制 Windows 服务的启动、停止、暂停、恢复和启动类型。
- `WindowsVisualEffects`：通过注册表和 `SystemParametersInfo` 读写 Windows 视觉效果设置。

## 与优化项的关系

优化项通过这些工具类完成实际系统操作：

- 数据驱动的 `ServiceItem` 使用 `WindowsService` 禁用或恢复服务。
- 数据驱动的 `MicrosoftStoreItem` 使用 `MicrosoftStore` 检查和卸载包。
- 内置的视觉效果相关优化项使用 `WindowsVisualEffects` 切换动画、缩略图和阴影等设置。
- Defender 相关逻辑使用 `AntiVirus` 判断是否存在第三方杀毒软件。
- `MainViewModel` 使用 `Battery` 判断是否展示电源模式优化项。

## 扩展建议

- 新增系统能力时，优先在 `Tools` 中封装底层调用，再由优化项调用。
- 工具类应聚焦单一系统领域，例如服务、商店应用、硬件状态或显示效果。
- 不要在工具类中处理 UI 状态、本地化文本或优化项分组。
- PowerShell 调用前应清理 `PowerShellService.Commands`，避免复用实例时残留命令。
- 涉及注册表和系统服务的工具应保持接口语义明确，让调用方决定何时初始化、回滚或提示用户。
