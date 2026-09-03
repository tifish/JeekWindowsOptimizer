# DiskSpace 架构说明

`DiskSpace` 目录实现“磁盘空间”页签：系统盘空间不足时，先扫描出可释放的空间并清理，再把大文件夹和页面文件迁移到其他磁盘。

## 为什么不用 OptimizationItem

`OptimizationItem` 的契约是“状态可检测、可来回切换”。清理动作没有持久状态，也不可逆；迁移动作需要选择目标盘并确认。两者都不适合 toggle 模型，所以这里用独立的模型和页签，`OptimizationItem` 和 `Tools` 不再承载清理类功能。

## 核心模型

- `DiskSpaceItem`：页签中一行的抽象。提供本地化文本、`State`（未扫描 / 扫描中 / 已扫描 / 执行中 / 完成 / 失败）、`SizeBytes` 和状态文本。`RefreshAsync()` 重新测量，重入安全。
- `DiskSpaceCleanupItem`：可释放空间项。`ScanCore()` 返回可释放字节数，`CleanCore()` 执行清理，`CleanAsync()` 清理后自动重扫并记录 `FreedBytes`。有 `IsChecked` 参与批量清理；不可逆的项（Windows.old、ResetBase）通过 `DefaultChecked => false` 默认不勾选，耗时项标记 `IsSlow`。
- `DiskSpaceRelocationItem`：迁移项。记录 `CurrentLocation`、`IsOnSystemDrive`、可选目标盘 `TargetDrives`，`GetTargetPath()` 给出目标路径，`CheckAsync()` 做本地校验，`MoveAsync()` 执行迁移。需要重启生效的项（页面文件）通过 `RequiresReboot` 保持完成状态而不重扫。
- `DiskSpaceGroup` / `GroupNavItem.FromDiskSpaceGroup`：与优化页、工具页一致的分组和左侧导航。
- `DiskSpaceItemManager`：创建全部项、枚举可作为目标的 NTFS 固定盘、读取系统盘用量。

所有成员在 UI 线程使用；重活在实现内部派发到线程池。`MainViewModel.DiskSpace.cs` 持有集合、扫描 / 清理 / 迁移入口、确认对话框和汇总文本。

## 现有项

清理（`Cleanup/`）：回收站、临时文件、Windows Update 下载缓存、传递优化缓存、崩溃转储、系统日志与错误报告、上一个 Windows 版本（走 cleanmgr 处理器）、组件存储（DISM /AnalyzeComponentStore 估算，/StartComponentCleanup /ResetBase 清理）。

迁移（`Relocation/`）：页面文件（WMI `Win32_PageFileSetting`，重启生效）、桌面 / 文档 / 下载 / 图片 / 音乐 / 视频（`IKnownFolderManager::Redirect`，与资源管理器“位置”页签相同的调用）。用户目录的目标固定为目标盘根目录下的英文规范名（如 `D:\Documents`）：用户容易找到、重装系统后仍在、与用户名无关；不考虑多用户机器。目标已存在且非空时确认框会提示合并。

## 系统访问层

底层调用都在 `SystemAccess`：`FileSystemCleaner`（不跟随重解析点的测量与删除）、`RecycleBin`、`WindowsUpdateCache`、`DeliveryOptimizationCache`、`ComponentStore`、`DiskCleanupTool`（用私有 StateFlags 配置驱动 cleanmgr）、`PagingFile`、`KnownFolders`。

`KnownFolders.Redirect` 的注意事项：

- `KF_REDIRECT_FLAGS` 中 `USER_EXCLUSIVE` 是 0x1、`CHECK_ONLY` 是 0x10、`WITH_UI` 是 0x20，写错就会把“试运行”变成真实迁移。
- 不能加 `EXCLUDE_ALL_KNOWN_SUBFOLDERS`：Windows 11 上音乐 / 图片等各有一个解析到同一目录的 `Local*` 已知文件夹，排除它会让整个调用失败。
- 没有可靠的试运行；用 `ValidateRedirectTarget` 做本地校验。
- 调用不报告进度，也不弹复制对话框。别再尝试用 `IFileOperation` 去拿系统自带的复制窗口：同样的调用在独立进程里能正常弹出 `OperationStatusWindow`，但在本进程的工作线程里，一旦 shell 需要显示 UI 就会永久卡死（没有消息泵），只有在对话框出现前就完成的小操作才会成功。
- 迁移进度按目标盘剩余空间推算：每秒一次 `DriveInfo.AvailableFreeSpace`，减少量即复制进度；复制完成后剩下的时间是删除源文件，只显示文字不给字节数（源盘要等删除提交后才反映出空间，过程中一直读到 0）。不要改回遍历目录测量大小——几万个文件的目录每次遍历都是一次全量枚举，会和复制抢 IO。

## 扩展建议

- 新增清理项：继承 `DiskSpaceCleanupItem`，实现 `ScanCore` / `CleanCore`，在 `DiskSpaceItemManager.CreateItems()` 注册，并在 `Languages.tab` 增加 `*Name` / `*Description`。
- 新增迁移项：继承 `DiskSpaceRelocationItem`，实现 `RefreshCoreAsync` / `GetTargetPath` / `MoveCoreAsync`。
- 调试：Debug MCP 提供 `disk_space_items`、`disk_space_scan`、`disk_space_clean`、`disk_space_relocation_check`、`disk_space_relocate`。后两个会真实改动系统，只在 Debug 面向开发者暴露。
