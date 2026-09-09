# DiskSpace 架构说明

`DiskSpace` 目录实现“磁盘空间”页签：系统盘空间不足时，先扫描出可释放的空间并清理，再把大文件夹和页面文件迁移到其他磁盘。

## 为什么不用 OptimizationItem

`OptimizationItem` 的契约是“状态可检测、可来回切换”。清理动作没有持久状态，也不可逆；迁移动作需要选择目标盘并确认。两者都不适合 toggle 模型，所以这里用独立的模型和页签，`OptimizationItem` 和 `Tools` 不再承载清理类功能。

## 核心模型

- `DiskSpaceItem`：页签中一行的抽象。提供本地化文本、`State`（未扫描 / 扫描中 / 已扫描 / 执行中 / 完成 / 失败）、`SizeBytes` 和状态文本。`RefreshAsync()` 重新测量，重入安全。
- `DiskSpaceCleanupItem`：可释放空间项。`ScanCore()` 返回可释放字节数，`CleanCore()` 执行清理，`CleanAsync()` 清理后自动重扫并记录 `FreedBytes`。有 `IsChecked` 参与批量清理，耗时项标记 `IsSlow`。默认勾选的标准是“清理后 Windows 能自己重建”：缓存和日志默认勾选，会失去某种恢复能力的默认不勾选（`DefaultChecked => false`）。`AutoCheckAfterScan` 让项在首次扫描拿到信息后修正一次勾选状态，只生效一次，之后不覆盖用户的选择；这种由扫描推导出的勾选不算用户选择（`HasExplicitCheckedChoice`），不写进设置，每次运行重新推导。清理后 Windows 仍保留一部分内容的项（卷影副本、pnpm）用 `IsReclaimableUpperBound` 标记，汇总里整体标成“最多 X”。执行结果统一按“测得多少算多少”：只要拿到执行前后两次测量，即使清理中途失败也记录已释放量并更新剩余大小；缺任一次测量才把大小和释放量置为未知。
- `DiskSpaceRelocationItem`：迁移项。记录 `CurrentLocation`、可选目标盘 `TargetDrives`（默认是除系统盘和当前所在盘之外的 NTFS 固定盘，所以能换到另一块数据盘；回系统盘走“恢复默认位置”，落在用户配置目录而不是盘根。虚拟磁盘类项目用 `AllowSystemDriveTarget` 打开系统盘作为普通目标），`GetTargetPath()` 给出目标路径，`CheckAsync()` 做本地校验，`MoveAsync()` 执行迁移，`RestoreDefaultAsync()` 恢复 Windows 默认位置（用户目录回到 `%USERPROFILE%`，页面文件回到自动管理）。`IsChecked` 参与“移动选中项”批量操作，默认不勾选。需要重启生效的项（页面文件）通过 `RequiresReboot` 不重扫。目标盘下拉框标注 SSD / HDD（`Disk.IsSSD`），帮助用户在速度和空间之间取舍。
- `DiskSpaceGroup` / `GroupNavItem.FromDiskSpaceGroup`：与优化页、工具页一致的分组和左侧导航。
- `DiskSpaceItemManager`：创建全部项、枚举 NTFS 固定盘（含系统盘，附带 SSD 判断）、读取系统盘用量。

所有成员在 UI 线程使用；重活在实现内部派发到线程池。`MainViewModel.DiskSpace.cs` 持有集合、扫描 / 清理 / 迁移入口、确认对话框和汇总文本。

## 现有项

清理（`Cleanup/`）：回收站、临时文件、Windows Update 下载缓存、传递优化缓存、崩溃转储、系统日志与错误报告、上一个 Windows 版本（走 cleanmgr 处理器；默认不勾选，但用 Windows.old 创建时间和回滚期天数判断，回滚期已过或只剩升级残留时首次扫描后自动勾选，回滚期读 `HKLM\SYSTEM\Setup\Uninstall` 的 `UninstallWindow`，缺省 10 天）、组件存储（DISM /AnalyzeComponentStore 估算，`/StartComponentCleanup` 清理，**不加** `/ResetBase`——那会连“卸载近期更新”所需的组件一起删掉，属于不可重建的能力，放在工具页作为单独的深度清理项）、浏览器缓存、应用崩溃转储、显卡安装残留、累积更新 LCU 暂存、Windows Installer 基线缓存、驱动库旧驱动、卷影副本、休眠文件，以及单独成组的开发工具缓存（NuGet、pip、npm、npx、pnpm、Yarn、Gradle、Maven、Cargo、Go、Visual Studio、VS Code、Package Cache）。行的先后按“日常缓存 → 安装残留与更新缓存 → 修复/回滚数据与系统功能 → 开发工具缓存”排列；开发缓存的行序在 `CreateItems()` 里逐条写死，不切 `DeveloperCachePaths.Kinds`。

命令驱动的清理（pnputil、vssadmin）逐项执行：单项失败只记为“未完成”，不丢弃已经删掉的部分，也不放弃后面的项目。子进程输出按控制台代码页解码（`CleanupCommand.Decode`），否则中文系统上的分隔符会在 UTF-8 解码里被吞掉，解析随之错位。

`FixedDirectoryCleanupItem` 只清单个固定目录：拒绝盘根，也拒绝 Windows / Program Files / ProgramData / 用户目录这些根本身（厂商缓存是它们的子目录，仍然允许）。显卡残留另外要求 msiexec / setup 没有在跑。

迁移（`Relocation/`）：页面文件（WMI `Win32_PageFileSetting`，重启生效）、WSL 2 发行版、Docker Desktop 磁盘数据、桌面 / 文档 / 下载 / 图片 / 音乐 / 视频（`IKnownFolderManager::Redirect`，与资源管理器“位置”页签相同的调用）。用户目录的目标固定为目标盘根目录下的英文规范名（如 `D:\Documents`）：用户容易找到、重装系统后仍在、与用户名无关；不考虑多用户机器。目标已存在且非空时确认框会提示合并。批量移动按各行自己选的目标盘执行，用户目录在前、页面文件最后（重启提示只弹一次），开始前按目标盘汇总校验剩余空间。“恢复默认位置”只做单项，不进批量；恢复用户目录后会顺手删掉 Windows 11 给 `Local*` 双胞胎留下的显式覆盖项（`KnownFolders.ClearLocalTwinOverride`）。

## 系统访问层

底层调用都在 `SystemAccess`：`FileSystemCleaner`（不跟随重解析点的测量与删除）、`RecycleBin`、`WindowsUpdateCache`、`DeliveryOptimizationCache`、`ComponentStore`、`DiskCleanupTool`（用私有 StateFlags 配置驱动 cleanmgr）、`PagingFile`、`KnownFolders`、`CleanupCommand`（System32 下的控制台工具，按控制台代码页解码、非零退出即报错、可取消）、`DriverStoreCleanup`、`ShadowCopyStorage`、`WindowsServicingState`、`DeveloperCachePaths`、`NuGetCache`、`PnpmStore`、`WslStorage`、`DockerDiskStorage`。

`KnownFolders.Redirect` 的注意事项：

- `KF_REDIRECT_FLAGS` 中 `USER_EXCLUSIVE` 是 0x1、`CHECK_ONLY` 是 0x10、`WITH_UI` 是 0x20，写错就会把“试运行”变成真实迁移。
- 不能加 `EXCLUDE_ALL_KNOWN_SUBFOLDERS`：Windows 11 上音乐 / 图片等各有一个解析到同一目录的 `Local*` 已知文件夹，排除它会让整个调用失败。
- 没有可靠的试运行；用 `ValidateRedirectTarget` 做本地校验。
- 调用不报告进度，也不弹复制对话框。别再尝试用 `IFileOperation` 去拿系统自带的复制窗口：同样的调用在独立进程里能正常弹出 `OperationStatusWindow`，但在本进程的工作线程里，一旦 shell 需要显示 UI 就会永久卡死（没有消息泵），只有在对话框出现前就完成的小操作才会成功。
- 迁移进度按目标盘剩余空间推算：每秒一次 `DriveInfo.AvailableFreeSpace`，减少量即复制进度；复制完成后剩下的时间是删除源文件，只显示文字不给字节数（源盘要等删除提交后才反映出空间，过程中一直读到 0）。不要改回遍历目录测量大小——几万个文件的目录每次遍历都是一次全量枚举，会和复制抢 IO。

## 扩展建议

- 新增清理项：继承 `DiskSpaceCleanupItem`，实现 `ScanCore` / `CleanCore`，在 `DiskSpaceItemManager.CreateItems()` 注册，并在 `Languages.tab` 增加 `*Name` / `*Description`。
- 新增迁移项：继承 `DiskSpaceRelocationItem`，实现 `RefreshCoreAsync` / `GetTargetPath` / `MoveCoreAsync`。
- 调试：Debug MCP 提供 `disk_space_items`、`disk_space_scan`、`disk_space_clean`、`disk_space_enqueue`、`disk_space_relocation_check`、`disk_space_relocate`、`disk_space_restore_default`、`disk_space_move_checked`，以及只跑隔离样本的 `disk_space_cleanup_probe`、`group_navigation_probe`、`virtual_disk_migration_probe`、`wsl_native_migration_probe`。会真实改动系统的接口只在 Debug 面向开发者暴露。

扫描状态与清理 / 迁移的忙碌状态独立：逐项更新汇总，已完成扫描的项目可以立即操作，无需等待 WinSxS。扫描期间禁止重复全量扫描，清理只包含已完成扫描且不忙碌的项目；汇总会提示仍在扫描。Debug MCP 的 `disk_space_items` 同时返回扫描状态与命令可用性。

清理结果：执行前重新测量，避免沿用过期扫描。`IsFreedBytesKnown` 表示本次是否完成前后测量；取消、执行异常或重扫失败时不沿用上次释放量，也不把未知剩余空间当作零。批量汇总提示失败项目。Debug MCP 的 `disk_space_cleanup_probe`（`scenario=accuracy`）可在进程内验证这些分支，不接触真实文件。

浏览器缓存：Edge / Chrome 分别注册，扫描标准 User Data 下 Default、Profile N 和 Guest Profile 的 Cache / Code Cache。运行中拒绝清理，不结束浏览器进程。跳过带重解析点的配置与缓存路径，保留 Cookie、密码、历史和网站存储；占用文件残留时显示未完成。Debug MCP `disk_space_cleanup_probe` 的 `browser` 场景用独立样本验证多配置、运行检查、锁定文件、数据保留和链接跳过。

NuGet：下载缓存与已解压全局包分开显示；全局包默认不勾选。通过安装在 Program Files 下的 dotnet 调用 `nuget locals <kind> --list/--clear --force-english-output`，尊重用户配置及环境变量，只处理系统盘位置；清理前再次验证并固定路径。含链接或指向受保护目录时拒绝清理。CLI 非零退出明确报错，子进程有超时并可取消。Debug MCP `nuget` 场景仅向子进程传入临时缓存位置，运行真实官方命令验证扫描、两类缓存隔离、文件占用及重试。

应用崩溃转储：新增独立行，仅清理系统盘上当前用户 `%LOCALAPPDATA%\CrashDumps` 的顶层 `*.dmp`，不递归、不扩展到应用自定义目录。默认不勾选，保留用户对既往崩溃诊断资料的选择。跳过链接，文件占用时报告未完成。Debug MCP `user_dumps` 场景验证后缀范围、只读/占用文件、保留其他文件、重试、链接与目录不存在。

清理与迁移队列：每行提供独立操作按钮；单项清理、批量清理、单项移动、批量移动和恢复默认位置都进入 `DiskSpaceOperationQueue`，确认后按入队顺序串行执行。AsyncRelayCommand 允许继续接收其他行的请求，同一项执行中或排队中不重复入队。`QueuePosition` 显示等待顺序，并锁定该行的目标盘和操作。批量移动仍先排用户目录、再排页面文件；用户后续点击的项目追加到末尾。执行前重新扫描源位置、校验移动目标并读取目标盘当前可用空间；源位置变化或校验失败时跳过该项并继续队列。队列结束统一汇总成功/失败及清理释放量，需重启时提示一次。队列仅保存在本次进程内。

Debug MCP：`disk_space_enqueue` 接受单个 `item`、`action=clean|move|restore` 和移动时可选的 `drive`，立即返回；`disk_space_items` 包含当前项、等待数量、每行队列位置及按钮可用性。原来的常规等待式清理/移动/恢复接口也经过同一队列；Debug 专用 target_path 直接重定向仍供底层调试使用。`disk_space_cleanup_probe` 的 `queue` 场景通过真实 ViewModel 与模拟清理/迁移项验证混合 FIFO、批量顺序、重复请求、目标盘固定、失败继续、恢复、汇总，以及执行前空间/源位置检查；不移动用户目录或清理用户文件。

休眠文件：`HibernationDiskSpaceItem` 既不是清理项也不是迁移项（没有勾选框，不进批量清理，也不计入总可释放量），提供缩减 / 关闭 / 恢复三个按钮，走 powercfg（缩减前先 `/h /size 0` 复位自定义大小）。当前模式只看 hiberfil.sys 是否存在加上 `HiberFileType`（1 为缩减，其余按完整算），所以执行后一定能判定是否生效，不会因为读不到注册表值把成功报成失败。

卷影副本：只处理系统盘上客户端可见的快照，保留最新一个，其余逐个 `vssadmin delete shadows`；不关闭系统还原。大小取卷影存储已用量，是上限而不是保证释放量。

驱动库：`pnputil /enum-drivers` 枚举后按 INF 原始名、厂商、类别、架构分组，只删同组中版本较低且没有设备在用的包（设备占用交给 pnputil 自己兜底）。执行时只枚举一次，逐个删除，失败的包不影响其他包。

LCU：清理 `Windows\servicing\LCU`。待重启、更新维护进行中、目录内有本次启动之后新增或修改的文件时都不允许清理；这三项在开始清理前校验一次，删除过程中只复查“维护是否正在运行”——删除本身会更新残留目录的时间戳，逐条复查会把自己的动作当成新的暂存。

开发工具缓存：`DeveloperCachePaths` 是固定白名单加显式的环境变量 / 配置覆盖（npm 只读 `.npmrc` 的 cache 项，Maven 只读 settings.xml 的 localRepository），解析出的路径必须是绝对路径、在系统盘上、不是盘根、不在安装或系统目录里、不覆盖用户目录本身、不含链接、目录里没有项目标志文件。清理前后都重新解析并比对，路径变了就中止。pnpm 走 `pnpm store path` / `store prune`，把元数据缓存钉在临时目录，只影响被测量的 store。

选择与持久化：每个分组有“全选 / 全不选”，作用于当前显示的行（搜索过滤后同样只作用于可见行），跳过忙碌行，迁移项还要有可用目标盘。全选不区分风险等级，确认框会逐条列出待清理项。清理项的勾选状态按 `NameKey` 存进 `RoamingSettings.DiskSpaceCleanupSelections`，在窗口失焦和关闭时写盘。

WSL / Docker 迁移：WSL 2 发行版用 WSL 自己的 `--manage --move`（先 `--terminate`，迁移后不自动启动，避免拉起用户的服务），目标是 `<盘>:\WSL\<名称>-<id 前 8 位>`；旧版 Docker 数据以 `docker-desktop-data` 发行版的形式出现，`docker-desktop` 不显示。Docker 托管磁盘（`DockerDiskStorage`）离线复制 `docker_data.vhdx`：全程要求 Docker 已退出，复制后按 SHA256 校验，再用目录联接把原路径指向新位置，Docker 自己的配置不动；切换前后有备份目录和领占目录，切换成功后不再回滚，只在清理残留失败时提示人工处理。恢复默认位置把数据搬回联接前的原路径。

分组导航：左侧导航点击（包括再次点击当前项）和回车 / 空格都会重新定位，把该分组标题对齐到视口顶部；折叠的分组先展开。为最后一个很短的分组补的尾部空白按本次目标实际需要计算，不需要就清零，避免留下一屏滚不到内容的空白。
