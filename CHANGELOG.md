# 更新日志

本项目的所有显著变更都将记录在此文件中。

格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本管理遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [Unreleased](https://github.com/OWNER/REPO/compare/v0.1.0...HEAD)

### 新增

- **build.prop 可视化编辑器**：

  - `BuildPropService` 解析 / 序列化引擎，保留注释、空行与原始行序；未修改的行保存时沿用原文，仅重写修改过的行；换行符固定 LF（Android 要求），无 BOM UTF-8 写出

  - 「从工作目录查找」自动发现解包产物中的 build.prop（`workspace/<分区>/build.prop` 与 `workspace/_zip/**/build.prop`），多候选时弹窗选择

  - 列表化编辑：键值分栏，等宽字体；40+ 常见属性内置中文说明（悬停键名显示）

  - 实时搜索过滤（键或值，忽略大小写）

  - 添加属性（校验键合法性、查重）、行内删除

  - 状态栏显示文件路径、属性数、未保存修改计数；保存前自动创建 `.bak` 备份

  - 快捷键 Ctrl+O 打开 / Ctrl+S 保存

- **预装应用精简管理**：

  - `WorkspaceScanner` 扫描 system / vendor / product / system\_ext / odm（含 super 分区嵌套布局）下的 app 与 priv-app

  - 同时支持目录型（`system/app/<Name>/<Name>.apk`）与单文件型（老式 `system/app/<Name>.apk`）布局

  - **APK 解析（AndroidManifest / AXML）**：`ApkParser` 自研二进制 XML 解析——字符串池（UTF-8 / UTF-16 两种编码）+ 块遍历，提取包名、versionName / versionCode、minSdk / targetSdk、uses-permission 权限列表、debuggable / extractNativeLibs / hasCode 开关；扫描后后台逐个解析主 APK（目录型多 APK 取最大），列表副行显示「包名 · 版本」，解析失败标记占位不中断

  - **精简安全知识库（`DebloatSafetyService`）**：内置 100+ 常见包名风险等级（禁删 / 谨慎 / 可删，覆盖 AOSP 核心、GMS、小米 MIUI、三星 One UI、华为），依据解析出的包名自动标注彩色徽标（红 / 橙 / 绿，悬停显示原因）；删除确认对话框分级警示——含禁删项时列出包名清单并将按钮改为「仍要删除（风险自负）」

  - 点击应用行弹详情窗：删除风险 / 包名 / 版本 / SDK / 权限清单 / 关键开关（可选中复制）

  - 搜索过滤（应用名 / 包名 / 路径）、全选 / 全不选、批量勾选删除（含体积统计），删除时自动清理同名 odex / vdex / art 变体

- **应用预装与资源替换**（新页面「应用预装 / 资源」）：

  - `PreinstallService`：APK 预装到 system/app 或 system/priv-app（标准 `<Name>/<Name>.apk` 目录布局，目标已存在时拒绝并提示），打包时随 system 分区进入 ROM

  - 资源替换：开机动画（bootanimation.zip，写入前校验 zip 内含 desc.txt）、字体（.ttf → system/fonts）、铃声 / 通知音 / 闹钟（ogg / mp3 / wav / m4a → system/media/audio 对应目录），覆盖式写入

  - 待预装 APK 列表实时解析包名 / 版本 / SDK 确认；无 system 分区的工作区直接报错引导先解包

- **打包前防呆检查（`PackPreflightService`）**：

  - 打包前自动检查：解包清单完整性（manifest 缺失 / 分区目录丢失）、boot 参数文件缺失、vbmeta 未禁用验证（定制分区会校验失败导致无法开机）、输出磁盘剩余空间不足

  - 分级弹窗确认：错误红 / 警告橙，含错误项时默认聚焦「取消」并要求显式选择「仍要打包」

  - **fastboot 刷机脚本生成**：打包完成后按解包清单在输出目录生成 flash\_all.bat（逐分区 fastboot flash + reboot）

- **build.prop 预设中心（`BuildPropPresets`）**：编辑器工具栏新增预设下拉（开发者调试 / 机型伪装 Pixel 8 示例 / 流畅度优化 / 网络优化 / 省电流畅均衡），一键应用——已存在的键改值、不存在的键追加；幂等（重复应用无副作用），应用后提示手动保存

- **ROOT 集成（Magisk / KernelSU）**：

  - `RootService` 两种方式：① 用已修补的 boot.img 替换原镜像——分区标记为 raw 直通，打包时直接复制修补镜像不再重建；支持 boot / init\_boot / vendor\_boot 目标分区，工作区无 boot 分区时自动新建条目；② 将 Magisk 管理器 APK 内置到 `system/priv-app/Magisk/`

  - ROOT 集成页显示工作区检测状态（boot 类分区、system 是否解包）

- **定制模板系统**：

  - `TemplateService`：模板 = build.prop 属性覆盖 + 精简应用清单 + ROOT 集成配置，JSON 存储（`%APPDATA%\RomPackageMaker\templates`）

  - 模板页支持新建 / 编辑 / 保存 / 删除 / 一键应用到工作目录，应用结果以日志清单展示

  - 应用逻辑：属性覆盖优先写入 system 分区的 build.prop（存在则改、不存在则追加，保留注释并自动 `.bak` 备份）；精简按应用名忽略大小写匹配；ROOT 按 `RootModes` 分发，失败不中断并记录警告

- **多任务队列**：

  - `TaskQueueService`：解包 / 打包任务按加入顺序依次执行（同一时间仅运行一个，避免磁盘争用）；排队任务直接取消、运行中任务经 `CancellationToken` 中断；每项带进度 / 阶段 / 错误与状态通知（`INotifyPropertyChanged`）

  - UI 线程调度经注入的 `UiMarshal`（App 启动注入 `DispatcherQueue.TryEnqueue`，保持服务可独立测试）

  - 任务队列页：内联新建解包 / 打包任务（含签名选项）、逐项进度条与状态、取消 / 移除 / 移除已结束、队列统计（排队 / 运行 / 已结束）

  - 队列完成解包后自动更新会话工作目录（`WorkspaceState`）

- **AVB / dm-verity 处理**：

  - `AvbService`：vbmeta 镜像头解析（flags / release string）、禁用验证与哈希树标志（`flags |= 3`，等价 `fastboot --disable-verity --disable-verification`，幂等）、镜像末尾 AVB 校验页脚（`AVBf`）检测与截断（恢复原像大小，sparse 镜像自动跳过）

  - 工作区扫描：全部分区目录镜像 + `_zip` 内的 `vbmeta*.img`（含 vbmeta\_system 等）

  - AVB 处理页：检测结果列表（类型 / 验证状态 / 页脚信息）、一键禁用全部 vbmeta 验证、一键截断全部校验页脚、执行日志

- **payload.bin（A/B OTA 有效载荷）解析与提取**：

  - `PayloadBinService`：头解析（`CrAU` 魔数 / 版本 / manifest 大小 / 元数据签名，v1=20B v2=24B 变长头）+ 自研最小 protobuf wire 解码器（无需 protoc 生成代码）

  - manifest 解析字段号对照 AOSP update\_metadata.proto 核实（block\_size=3 / minor\_version=12 / partitions=13 / operations=8 / new\_partition\_info=7 / 枚举值 ZERO=6·DISCARD=7·REPLACE\_XZ=8 等）

  - 分区提取：REPLACE（raw）/ REPLACE\_BZ（BZip2Stream）/ REPLACE\_XZ（XZStream）/ ZERO / DISCARD / MOVE（同镜像内块拷贝）；增量类型（SOURCE\_\* / BSDIFF / PUFFDIFF / LZ4DIFF 等需要旧分区镜像）标记不可提取并说明原因

  - new\_partition\_info（精确分区大小 + SHA-256 哈希）解析：分区列表按 manifest 精确大小显示（无该信息时按最大目标 extent 估算）；提取完成后自动对整个镜像做 SHA-256 校验，不匹配抛错并列出期望 / 实际哈希前缀

  - REPLACE 家族零填充语义：操作数据不足块大小时按 update\_engine 规范补零到块边界（数据超出 extents 总长仍报错）

  - OTA zip 支持直接选择（自动定位并解出 payload.bin 到临时文件）

  - Payload 页：解析 → 分区列表（大小 / 操作数 / 可提取性 / 含哈希标记，默认勾选可提取项）→ 勾选批量提取（进度 / 取消 / 结果日志，逐分区报告 SHA-256 校验结果），提取出的 .img 可继续在「解包」页处理

  - 解包 zip 时检测到 payload.bin 会提示改用 Payload 页（A/B OTA 包内无独立 .img）

- **ext4 镜像解析与重建**：`Ext4Reader` / `Ext4Writer`，支持 extent 布局、4096 块大小、inode 256，可从 `system.img` 等分区镜像中读出文件并写回

- **sparse 镜像解包 / 重打包**：`SparseImage`，支持 raw / fill / don't-care 块类型，sparse ↔ raw 往返转换

- **boot.img 解包与重打包**：`BootImage`，boot / recovery 镜像头解析、cpio（newc）ramdisk 解包重组、gzip 压缩

- **super.img 动态分区解析**：`SuperImage`，解析 super 分区动态布局，拆出子分区

- **zip 刷机包签名**：`ZipSigner`，APK v1（JAR）签名，内置自签名测试证书（`System.Security.Cryptography.Pkcs`）

- **设置持久化**：`AppSettings`（`appsettings.json`），主题、默认工作目录 / 输出目录、签名密钥路径、sparse 块大小、打包后自动打开输出目录

- **设置页**：主题切换（跟随系统 / 浅色 / 深色，即时生效）、默认路径浏览 / 清除、签名密钥（.pk8 / .pfx / .pem）配置、sparse 块大小选择

- **任务取消**：解包 / 打包页新增取消按钮，`CancellationTokenSource` 贯穿 `RomPackService` 管线，取消后正确回退 UI 状态

- **拖放支持**：解包 / 打包页面支持拖入 ROM 文件（.zip / .img / .bin）或工作目录文件夹

- **日志工作台**：日志区新增清空 / 复制按钮，最新日志置顶显示

- **主题启动恢复**：应用启动时读取并应用保存的主题

- **打包完成自动打开输出目录**（可在设置中开关）

- **全局异常捕获**：未处理异常写入 `%TEMP%\rompkg_crash.log` 便于诊断

- **端到端回归自测**（`_selftest/TestProj`）：用自身引擎从零构造完整 ROM（Ext4Writer 生成 system.img、BootImage 生成 boot.img、合成 vbmeta → zip），执行「解包 → build.prop 修改 / 新增 → 应用精简 → AVB 禁用 → 打包签名 → 再解包」全闭环，21 项断言覆盖：镜像生成、解包产物、prop 注释保留与往返、精简生效（Browser 删除 / Shell 保留）、vbmeta flags=3、boot kernel 二进制往返一致、打包 zip 条目完整性。另含 payload 专项 15 项：合成 payload.bin（REPLACE / REPLACE\_XZ / REPLACE\_BZ / ZERO / MOVE / DISCARD + new\_partition\_info 哈希，XZ/BZ2 blob 由 Python 预生成）验证解析字段、逐字节提取一致、SHA-256 校验通过、坏哈希拒绝、无哈希分区跳过校验、零填充语义、OTA zip 直选。再含 APK 专项 10 项：测试内构造 AXML 编码器生成合成 manifest（UTF-8 / UTF-16 双字符串池），验证包名 / 版本 / SDK / 权限 / 开关解析、缺 manifest 报错、扫描器主 APK 路径记录与列表丰富。新功能专项 27 项：知识库分级查询（AOSP / GMS / OEM / 未知 / 原因文本）、预装（app 与 priv-app 布局 / 重复拒绝 / 扫描器可见 / bootanimation 合法与非法 / 字体 / 三类音频 / 无 system 拒绝）、打包预检（干净工作区零问题 / 非工作区报错 / flash\_all.bat 生成与内容）、build.prop 预设（修改与新增计数 / 机型伪装生效 / 幂等 / 序列化往返）

### 变更

- 新增依赖 **SharpCompress 0.50.4**（纯托管，用于 payload REPLACE\_XZ 解压）

- 部署模型从单项目 MSIX 切换为**非打包（unpackaged）自包含部署**（`WindowsPackageType=None`），目标机器无需安装 Windows App SDK 运行时，也无需 MSIX 安装

- Windows App SDK NuGet 从 2.4.0 降级至 **2.2.0**，与本机已安装的 Windows App Runtime 2.2 匹配，避免 `REGDB_E_CLASSNOTREG` 启动崩溃

- 解包 / 打包页加载设置中的默认工作目录 / 输出目录，减少重复选择

- 设置存储路径从 `ApplicationData.LocalFolder` 改为 `%APPDATA%\RomPackageMaker\appsettings.json`

### 修复

- **启动即崩溃（0xC000027b）**：非打包应用访问 `Windows.Storage.ApplicationData.Current` 会触发 fail-fast 直接终止进程且无法被 try/catch 捕获；`AppSettings` 改用 `Environment.SpecialFolder.ApplicationData` 后应用可正常启动运行

- 移除 `App.ApplyTheme` 中窗口未创建时的 `Resources` 写入回退分支（在应用初始化早期抛 COM 异常，且该资源从未被读取）

- `.gitignore` 补充签名密钥排除项（`*.pk8` / `*.pem` / `*.key` / `*.keystore`）

- build.prop 解析缺陷（由 17 项往返测试发现并修复）：文件结尾换行符在 `Split('\n')` 产生的空元素被解析为额外空行，导致保存后文件每次多出一个空行；修改判断基线改为解析瞬间的标准形式（`Original`），修复 `key = value` 带空格的原始行被误判为已修改、保存时格式被意外重写的问题

- **页面状态丢失**：每次点击导航项都会 `Navigate` 重建页面实例，切页后已选路径、扫描结果、编辑中的模板等全部被清空。所有页面启用 `NavigationCacheMode.Required` 缓存实例，并在导航事件中跳过"已在当前页"的重复导航（避免重复入栈）

- **工作区上下文不共享**：解包使用的工作目录没有传递给其他页面（打包 / 精简 / ROOT / build.prop / 模板页各自只读"设置里的默认目录"，刚解包的目录对它们不可见）。新增 `WorkspaceState` 会话状态：解包完成 / 浏览或拖入目录时记录，各消费页面在 `OnNavigatedTo` 自动跟随（用户手动选过目录后不再覆盖）

- **精简页勾选不刷新**：单个复选框勾选后「删除选中」按钮与统计不更新（此前仅全选/全不选/扫描时刷新）。订阅 `WorkspaceApp.PropertyChanged` 即时刷新；同时修复删除失败的提示被统计文本覆盖的顺序问题

- **UI 线程阻塞**：精简页扫描（遍历整个 system 树）与删除、ROOT 页复制大 boot.img 均移至后台线程，按钮防重入，完成后回 UI 刷新

- **回退导航不同步**：标题栏返回（GoBack）后 NavView 高亮停留在旧页面。新增 `Navigated` 处理同步选中项；`Frame.CanGoBack` 无变更通知导致标题栏返回按钮永远不显示，改为导航完成时手动驱动

- **模板删除误删**：删除按钮读取编辑器当前内容（用户可能已改名），而非列表选中项；改为按列表选中项删除

- **XAML 编译器崩溃（WMC9999 掩盖真实错误）**：页面元素 `x:Name="StatusText"` 与 DataTemplate 中 `{x:Bind StatusText}` 属性同名引发 XamlCompiler 符号解析崩溃（错误消息资源缺失导致只显示内部错误）。AVB 页元素更名为 `HintText`；经验：**页面元素名不能与模板绑定属性同名**

- 另修复同类 `init` 属性问题：`AvbEntry` / `AvbRow` / `TaskQueueItem` 的 `get; init` 属性会使 XamlTypeInfo 生成非法赋值代码（此前已在 `BuildPropLine` 遇到过）——XAML 绑定类型的属性一律使用 `set`

- **导航崩溃（遗留 bug）**：上一轮重写 `MainWindow` 时 `PageTypeForTag` 漏掉了 `taskqueue` / `avb` 条目（switch 表达式不要求穷尽，编译不报错），点击「任务队列」或「AVB / dm-verity」导航项会抛 `Unknown navigation item tag` 异常；已补全全部 10 项映射

- **boot kernel 往返损坏（由端到端测试发现）**：重打包 boot.img 时 `KernelSize` 始终为 0——`BootInfo` 是 public 字段的 record，保存侧 JSON 序列化加了 `IncludeFields` 但读取侧没有加，字段全部读回默认值；且 kernel 装载后未像 ramdisk / second / dt 那样重算 size。读取侧补上 `IncludeFields` 并按实际数据重算 `KernelSize`，往返后 kernel 二进制逐字节一致；经验：**序列化选项两侧必须对称，头部 size 字段应从数据重算而非信任存量参数**

- **payload REPLACE 数据不足块时越界（由 payload 专项测试发现）**：`WriteToExtents` 要求操作数据长度与目标 extents 总长严格相等，遇到不足块的非对齐数据直接 `ArgumentOutOfRangeException`。按 update\_metadata.proto 规范（REPLACE 家族 "zero padding out to block size"）改为不足部分保持预置零、超出才报错；经验：**解析器行为要对照上游规范而非只依赖块对齐的常见样本**

### 计划中

- （Roadmap 已全部完成，后续按需求规划）

## [0.1.0](https://github.com/OWNER/REPO/releases/tag/v0.1.0) - 2026-09-05

### 新增

- WinUI 3 + Windows App SDK 技术栈骨架（.NET 8.0，Mica 背景，Fluent 设计）

- NavigationView 主窗口与中文导航结构（主页 / 解包 / 打包 / 关于）

- 解包工作台页面：ROM 文件选择（zip / img）、工作目录选择、进度条与实时日志

- 打包工作台页面：工作目录选择、输出路径选择、签名选项（预留）、进度条与日志

- 主页工作流引导（解包 → 定制 → 打包）

- `IRomPackService` 任务管线接口：统一进度报告（百分比 / 阶段 / 日志）与取消支持

- 自包含部署（`WindowsAppSDKSelfContained`），目标机器无需安装 Windows App SDK 运行时

- 仓库规范文件：README、LICENSE（MIT）、CHANGELOG、CONTRIBUTING、SECURITY、.editorconfig、.gitattributes、CI 工作流

### 修复

- 禁用 `PublishTrimmed`（WinUI 3 依赖 XAML 反射，裁剪会导致运行时故障，同时解决 Release 构建失败）

