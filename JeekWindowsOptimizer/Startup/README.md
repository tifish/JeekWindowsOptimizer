# Startup 架构说明

`Startup` 目录实现"启动项"页签：对开机时会运行的一切建立白名单。勾选允许的项目，其余一律不允许，新出现的项目等用户确认；做过的选择永久记住，包括本机当前不存在的项目，并随配置目录同步到其他电脑。

范围不限于登录启动项，还包括服务、驱动程序、计划任务和资源管理器扩展。

## 为什么不用 OptimizationItem

`OptimizationItem` 的契约是"一个可检测、可来回切换的系统设置"，只有一个状态。启动项有**两个互相独立的状态**：系统现在的行为（`IsEnabled`），和用户的意图（`Decision`）。两者可以不一致，而这个不一致恰恰是页签存在的意义——从另一台电脑同步过来的"禁止"会显示成"已禁止，但仍在运行"，等用户在本机确认后再执行，而不是静默关掉刚装好的软件。

集合也是动态的，不像 `Data\*.tab` 那样可以枚举写死。所以这里和 `DiskSpace` 一样用独立模型和页签。

## 决策账本

这是整个功能的核心，也是最容易静默损坏数据的地方。

- `StartupDecisionLedger`：磁盘格式。`Entries` 是键到决策的字典，外加 `Version`、`Machine`、`Epoch`、`Clock`。`Merge()` 是纯函数、可交换，**按条目**合并。
- `StartupDecisionStore` / `StartupDecisionShardStore`：按机器名分片读写、子目录监视、自动合并、中毒保护、去抖批量写。

### 按机器名分片，避免多机同步冲突

如果所有机器共同写入单个 `Config\StartupDecisions.json`，网盘或文件同步工具在多机并发修改时会产生冲突副本甚至后写覆盖前写。因此这里采用与 `JeekLauncher` 相同的分片方案：

- 每台机器**只写自己的分片**：`Config\StartupDecisions\<MachineName>.json`（机器名经文件名合法化处理），永不修改其他机器的分片或旧版单文件。
- 读取和保存时**合并 `Config\StartupDecisions\*.json` 下的全部分片**，并兼容读取旧版 `Config\StartupDecisions.json`（及遗留冲突副本）作为只读兜底层，旧数据无缝升级。
- 重载（`ReloadFromDisk`）只合并到内存、**绝不回写磁盘**，避免多机之间的同步回声；没有本地改动时 `Flush()` 也不写盘。
- 切换存储位置且不移动文件时，若内存中有决策或纪元，会与目标目录已有分片合并后写入本机分片；否则直接从目标目录加载。

### 混合逻辑时钟（HLC）

同一个键在两台电脑上被赋予不同决策时，用混合逻辑时钟 `Clock`（UTC 毫秒，单调大于已见全表最大值）判胜负：

- 尚未同步的两台机器自然按真实 UTC 时间先后决出胜负。
- 一旦观察到时钟漂移到未来的远端分片，本机下一次修改会取 `maxSeen + 1`，严格胜出，不会被错误挂钟永久压制。
- 保存时把待落盘的本地编辑重新抬高到磁盘最新时钟之上，即使远端分片在监视器防抖期间刚同步落盘也不会压掉用户刚做的决定。
- `DecidedAtUtc`、`Machine`、`Decision` 作为次级确定性判据，保证任意合并顺序收敛到完全一致的结果。

### 没有墓碑

条目缺失本身就表示"未定"，账本只记录"用户做过的选择"，所以不存"未定"状态。合并因此是纯并集，天然幂等。

代价是单项"重置决策"无法跨机器兑现（本机删掉，下次同步又回来），所以界面上不提供这个操作。真正需要的"全部重新开始"用文件级 `Epoch`：清空时加一，合并时纪元小的一方整体作废，这样重置能扛过同步，而不会被还持有旧条目的其他机器分片或旧版 `StartupDecisions.json` 撤销。

### 保留未知字段

`StartupDecisionEntry` 和 `StartupDecisionLedger` 都有 `[JsonExtensionData]`。旧版程序读到新版写的文件后重新序列化，如果丢掉不认识的字段，同步回去就是永久损坏。任意分片文件版本高于本程序时只读不写。

### 外部变更

`AppSettingsStore` 经 `RoamingConfigWatcher` 递归监视漫游 `Config` 目录（`IncludeSubdirectories = true`），实时捕捉 `StartupDecisions\*.json`、`StartupDecisions` 子目录以及旧版 `StartupDecisions.json` 的变更，在事件到达瞬间过滤 `.tmp` 临时文件、目录自身时间戳噪声和本机分片的自写窗口（绝不抑制其他机器分片），静默期后自动重新合并并刷新界面（含宽松匹配重算）。

### 中毒保护

同步工具替换文件的瞬间会短暂暴露锁定、零长度或半截文件。读写均带共享模式与退避重试；若本机自己的分片、唯一旧版文件或任意高版本分片无法安全读取，进入 `IsPoisoned` 禁写并按退避自动重试恢复。单个远端分片暂时损坏或半写时只记录警告并安排重试，不会阻塞本机向自己的分片写入，因为本机写盘不会覆盖任何远端分片。

## 身份

`StartupIdentity` 决定一条决策以后还认不认得同一个项目。账本永久保留，所以键必须与机器无关、也要扛得住软件自身升级。

- 已知文件夹归一成 `%localappdata%` 等记号，否则换台电脑换个用户名就全部失配。
- 只由版本号、GUID 或长哈希构成的路径段替换成 `#ver` / `#guid` / `#hash`，升级不算新项。**最后一段（文件名）永不替换**，否则两个不同程序会共用一条决策。
- 服务的 `\??\` 和 `\SystemRoot\` 原生路径、驱动的相对路径都归一到同一形式。相对路径只在**含目录分隔符**时才按"相对于 Windows 目录"处理（驱动的约定）；光秃秃一个文件名按 shell 的规则查 System32 和 PATH，查不到就原样保留，不能 `GetFullPath` 到本进程的工作目录去凭空造一个路径。
- `ExtractImagePath` 穿过 rundll32、regsvr32、脚本宿主和 cmd，把决策归到真正干活的 DLL 或脚本上。几个坑：rundll32 的 DLL 前面可能有 `/d` 之类的开关，必须先跳过；提取结果若以 `/` 或 `-` 开头说明取到的是开关，要回退；未加引号又带空格的完整路径（`C:\Program Files\Npcap\CheckStatus.bat`）必须先整体判断是不是文件，否则会被截成 `C:\Program`。这些都会直接写进永久键，错一次就是错一辈子。

严格键是 `种类|注册位置|条目名|归一化镜像路径`。其中任何一项变化都是新项目，需要重新确认——这正是要的：一个新的二进制文件顶替了原有的 Run 值，不应该继承它的许可。

宽松键是 `~|种类|条目名|文件名|签名者`，只在严格键未命中时作为兜底，让另一台电脑上的决策能匹配到装在不同位置的同一程序。跨机器合理变化的只有安装位置（和注册在 HKLM 还是 HKCU），所以去掉的只是位置和路径，**条目名必须保留**：否则同一个可执行文件的两个注册会共用宽松键，比如 gupdate 和 gupdatem 都跑 GoogleUpdate.exe，禁止其一会被套到另一个上。

另外两条排除规则，宁可再问一次也不猜：

- 精确键在本机存在的决策，属于那个条目本身，不再借给别的条目做宽松匹配。所以宽松索引要等全部条目扫完再建。
- 两条结论不同的决策撞上同一个宽松键时整条作废。

宽松命中在界面上明确标注，不静默应用。账本条目额外保存 `EntryName` 和 `ImagePath`，因为从键里拆条目名遇到含 `|` 的名字会出错、从命令行推镜像路径对 COM 任务不成立；旧条目缺这两个字段时，只在键恰好拆成四段时回退解析。

## 机器本地状态

`StartupLocalState` 存在 `%LocalAppData%`，永不漫游：

- `BaselineTakenUtc`：本机首次基线是否已接受。
- `FirstSeenUtc`：每个键在本机首次出现的时间。
- `OriginalServiceStart`：服务/驱动被禁止前的启动类型，用于精确还原成原来的自动或手动，而不是猜一个默认值。

分界线是：**漫游的只有用户的意图，本地的只有怎么把这台机器改回去**。同一个服务在一台机器上合理地是自动、在另一台是手动，同步还原值会是错的。

## 扫描

`StartupItemManager.ScanAsync()` 并行跑五个扫描器，然后统一附加签名和决策。扫描只读，不改系统。

`SystemAccess` 下的扫描器：

- `StartupRegistryScanner`：HKLM/HKCU 的 Run 和 RunOnce，含 32 位视图；策略驱动的 `Policies\Explorer\Run` 只读显示。同时读写 `StartupApproved` 开关。**Run 和 RunOnce 是两个分组**：RunOnce 的值被 Windows 执行时就删掉，是安装或更新的一次性残留，不是每天开机都跑的东西，混在一起会让一条永久白名单决策看起来对一个自删除的条目也有意义。RunOnce 也没有对应的 `StartupApproved` 开关，所以只读。
- `StartupFolderScanner`：两个启动文件夹，路径从 `User Shell Folders` 解析而不是假设默认值（被重定向的启动文件夹本身就值得看见）。快捷方式解析到目标程序。
- `ScheduledTaskScanner`：遍历任务树，只收登录/开机/注册/会话状态触发的任务。`GetTasks(1)` 包含隐藏任务——持久化手法常设隐藏标志。注意 `Item` 是带参属性不是方法，必须用 `GetComIndexed`。
  - **COM 处理器动作必须解析**。Windows 自己的任务大多跑 COM 处理器而不是可执行文件，取不到镜像路径就无法归属到发布者，于是全部落成"未签名"、躲过 Windows 隐藏、堆满列表。经 `ComServerRegistry` 把 ClassId 解析到服务器 DLL 后，本机可见的计划任务从 46 条降到 12 条，剩下的基本都是真正的第三方项。
  - 动作的 `Path` 不能直接当镜像路径用：要整条命令走 `StartupIdentity.ExtractImagePath`，否则经 cmd 启动的任务会被记成 cmd，所有这类任务共用一个身份。相对路径再用任务自己的 `WorkingDirectory` 补全。
- `ServiceRegistryScanner`：直接读 `HKLM\SYSTEM\CurrentControlSet\Services`。不用 WMI：一台机器有几百条，`Win32_Service` 每行成本高得多，而且完全查不到驱动和用户服务模板。按 `Type` 区分服务和驱动，跳过有模板的每用户服务实例（它们随会话生死，会让账本无休止地增长）。很多驱动（包括 Windows 自带的 Beep、exfat、fastfat）注册表里**没有 `ImagePath`**，加载器按约定用 `System32\drivers\<服务名>.sys`；扫描器必须补上这个默认路径，否则既无法在资源管理器中定位，也无法验签，Windows 驱动会被当成"未签名"显示。显示的命令行仍保留注册表原值。没有实际文件的行，定位按钮置灰。
- `ShellExtensionScanner`：见下节。

## 进程内扩展按 DLL 归并，并且分成两类

一个网盘或压缩软件通常注册七八个 CLSID：文件右键菜单、文件夹背景右键菜单、图标覆盖、属性页、列处理器。逐条列出等于就同一个程序问用户八遍。所以枚举各注册点后解析到 CLSID，再从 `InProcServer32` 取 DLL 路径，**按归一化后的 DLL 路径归并成一行**，展开显示它注册的每个挂钩点，允许或禁止是整体操作。

解析不到 InProcServer32 的 CLSID 是过时注册，宿主根本不会加载，直接丢弃。

**资源管理器扩展和浏览器加载项是两个分类，因为关闭机制根本不同。**

| 分类 | 注册点 | 关闭机制 |
| --- | --- | --- |
| 资源管理器扩展 | ShellEx 各处理器、图标覆盖、命名空间、ShellExecuteHooks、SharedTaskScheduler、ShellServiceObject(DelayLoad) | `Shell Extensions\Blocked` |
| 浏览器加载项 | Browser Helper Objects、Toolbar、Explorer Bars、URLSearchHooks、Extensions | `Ext\Settings\{CLSID}` 的 `Flags` 第 0 位 |
| 浏览器加载项（协议） | `Protocols\Filter`、`Protocols\Handler` | 无，只读展示 |

`Shell Extensions\Blocked` 是外壳创建 shell 扩展时才查的，**拦不住浏览器宿主加载 BHO**。用它去关加载项会报告成功而加载项照常工作，所以必须分开。加载项用"管理加载项"对话框写的那个开关：`HKCU\Software\Microsoft\Windows\CurrentVersion\Ext\Settings\{CLSID}` 的 `Flags`，第 0 位为 1 表示已关闭。注意**只能改那一位**：真实条目常带 `0x400` 之类的其他标志位，整个覆盖会把它们抹掉。写 HKCU，和对话框一致，也不需要提权。

IE 本体在 Windows 11 已经移除，但这些加载项仍然会被 Edge 的 IE 模式和任何托管 WebBrowser 控件的程序加载，所以照列照关。

协议过滤器和处理器属于 URL moniker，Windows 没有提供受支持的开关。不去删注册表（修复安装会装回来，用户的选择等于白做），而是标成只读展示。一行里只要有一个挂钩点没有可用开关，整行就标只读——只关掉一个 DLL 的部分注册却报告成功是撒谎。

同一个 DLL 如果两类都注册，会在两个分组里各出现一行。这与"一个 DLL 只问一次"看似矛盾，但允许右键菜单和允许浏览器加载项确实是两种不同的授权，机制也不同，所以分开问是对的。实践中很少有 DLL 两边都注册。

资源管理器扩展改动后需要重启资源管理器，复用 `OptimizationItem.RestartExplorer()`；加载项不需要。

## 签名与默认隐藏

`FileSignatureInfo` 用 WinVerifyTrust 验证，嵌入签名失败时**回落到安全目录**——`%SystemRoot%` 下绝大多数文件是目录签名而非嵌入签名，没有这一步几乎每个 Windows 文件都会被报成未签名。签名者取自验证后的证书链主题 CN。

结果按 (路径, 大小, 修改时间) 缓存，文件被替换才重验；一次全量扫描要验几百个文件，只有本机第一次付全价。

带 Windows 系统组件签名的项目默认隐藏（本机 937 项里有 785 项属于此类）。它们仍然参与扫描，所以某个文件一旦不再是 Windows 签名就会重新显示出来——这正是这个功能比任务管理器多出来的价值。另有一档可选开关隐藏其他微软项目（Office、OneDrive、Teams）。

## 基线

一台用了几年的机器首次运行会有上百个项目。全部标成"待确认"的话用户只会不看就点完。`AcceptBaseline` 一步把当前状态记录成决策：现在会启动的记为允许，已关闭的记为禁止，系统本身不动。之后只有新出现或发生变化的项目才会询问。

基线**不包含 Windows 项目**：它们本来就隐藏，为几百条各写一条决策只会淹没真正重要的少数条目。

## 执行

`StartupToggle` 每种机制都用 Windows 自己的开关，从不删除或搬移注册：

| 种类 | 机制 |
| --- | --- |
| Run / 启动文件夹 | `StartupApproved` 开关，与任务管理器一致 |
| 服务 / 驱动 | `Start` 值，还原时用本地记录的原值 |
| 计划任务 | Task Scheduler 的 `Enabled` |
| 资源管理器扩展 | `Shell Extensions\Blocked` |

**服务的禁止要立即生效**：先把启动类型改成禁用，再经 SCM 停止服务并等待到真正停止（`ServiceRuntime`，不用 WMI 的 StopService——它排队就返回，分不清已停止、停止中和被拒绝）。先改启动类型再停，是为了停止失败时服务至少重启后不会再起来。用户服务模板本身不运行，要停的是它的每会话实例（`名称_十六进制`）。

- **绝不连带停止依赖它的服务**：禁止一项不能悄悄停掉用户允许的其他服务。有运行中的依赖就保持运行，行状态显示"仍在运行，因为 X 依赖它"。
- 不接受停止、超时、失败都作为警告显示在状态后面，决定本身照常记录。
- 允许时恢复原启动类型；原来是自动的会立即启动，手动的留给触发条件，这正是手动的含义。
- 驱动不当场卸载，只改启动类型并提示"重启后生效"：很多驱动拒绝卸载，强行卸载正在使用的过滤驱动可能直接蓝屏。

`startup_service_probe` 用一个真实服务（默认 W32Time）走一遍禁止和允许，核对启动类型、停止、启动和原状态（含 DelayedAutostart）的精确恢复，不写账本。

引导关键驱动（Start 为 Boot 或 System）只显示不修改：关掉可能导致开不了机，没有哪条白名单值这个代价。

用户在本机点"允许"或"禁止"会立即生效——那是此时此地的明确操作。仅仅从同步过来的决策不会自动执行，等"应用决定"按钮；`AutoEnforceStartupDecisions` 可选开启自动执行，默认关闭。

## 左侧导航

导航是双向联动的。点导航行把该分组的标题滚到内容区顶部；反过来，滚动内容区会把导航选中项移到当前分组。

反向联动绝对不能回滚内容，否则滚轮和程序会互相打架。`MainWindow` 用 `_applyingProgrammaticScroll` 忽略自己那次滚动引起的布局事件，视图模型用 `_syncingNavFromScroll` 让选中项只记录、不发滚动请求。滚到最底部时直接判定为最后一个分组：短的末尾分组标题永远到不了顶部，否则它永远不会成为当前分组。

副作用：滚动会改变导航列表的选中行，所以在导航列表上按回车激活的是滚动后高亮的那一行。这是预期行为，`group_navigation_probe` 的键盘用例已按此断言。

这套机制四个页签共用，不是启动项特有的。回归测试都在 `group_navigation_probe` 里。

## 调试

Debug MCP：`startup_items`、`startup_scan`、`startup_decide`、`startup_enforce`、`startup_baseline`、`startup_ledger`，以及只跑合成数据和临时目录的 `startup_ledger_probe`（场景：merge | shards | epoch | conflict | poison | identity | addon | all）。probe 不读取任何真实启动项，也不碰真实决策文件。

## 尚未覆盖

Autoruns 里更深的位置留待第二期：Winlogon 的 Shell/Userinit/Notify、AppInit_DLLs、IFEO Debugger、Boot Execute、Winsock/LSA/网络提供程序、打印监视器、WMI 事件订阅、Office 加载项、编解码器。它们数量少但误禁后果重，值得单独设计。Windows 11 新版右键菜单来自应用包清单而不在注册表里，也需要单独枚举。
