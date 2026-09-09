# ROM Package Maker · ROM 制作工具

[![CI](https://github.com/Hinana0325/ROM-Package-Maker/actions/workflows/ci.yml/badge.svg)](https://github.com/Hinana0325/ROM-Package-Maker/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![WinUI 3](https://img.shields.io/badge/WinUI-3%20%2B%20Windows%20App%20SDK-0063B1)](https://learn.microsoft.com/windows/apps/windows-app-sdk/)

Android ROM 解包、定制与打包的 Windows 原生桌面工具。基于微软官方推荐的现代 Windows 开发技术栈构建，为 ROM 定制者提供一个流畅、可靠、可视化的本地化工作台。

## 功能特性

### 已实现

**镜像格式引擎（纯托管实现，无外部依赖）**

- **zip 刷机包解包 / 重打包** — 载入刷机包 zip，解出可编辑的工作目录，打包时重新生成 zip

- **ext4 镜像解析与重建** — `system.img` / `vendor.img` 等分区镜像的文件级读取与写回（4096 块大小、extent 布局、inode 256）

- **sparse 镜像处理** — Android sparse image（`system.img` 常见格式）的 raw / fill / don't-care 块解包与重新 sparse 化

- **boot.img 解包与重打包** — boot / recovery / vendor\_boot 镜像头解析（v0–v4 全版本，按 AOSP `bootimg.h` 真实偏移）、cpio（newc）ramdisk 解包重组、gzip 压缩、分区间填充保真

- **super.img 动态分区** — super 分区动态布局解析，拆出子分区

- **zip 刷机包签名** — APK v1（JAR）签名，内置自签名测试证书，支持在设置中配置自定义密钥

- **build.prop 可视化编辑器** — 保留注释与行序的解析引擎、从解包工作目录自动查找、列表化编辑与搜索过滤、40+ 常见属性中文说明、保存前自动 `.bak` 备份

- **预装应用精简** — 扫描 system / vendor / product / system\_ext / odm（含 super 嵌套）的 app 与 priv-app，目录型与单 APK 文件型布局均支持；搜索过滤、批量勾选删除（自动清理同名 odex / vdex / art），显示每个应用的分区与体积

- **ROOT 集成（Magisk / KernelSU）** — 两种方式：用已修补的 boot.img 替换原镜像（打包时 raw 直通，支持 boot / init\_boot / vendor\_boot，无 boot 分区时可新建条目）；或把 Magisk 管理器 APK 内置为 system 应用

- **定制模板** — 把 build.prop 属性覆盖 + 预装精简清单 + ROOT 集成保存为 JSON 模板（`%APPDATA%\RomPackageMaker\templates`），一键应用到任意工作目录；属性覆盖优先写入 system 分区的 build.prop，未命中按名称匹配删除

- **多任务队列** — 解包 / 打包任务加入队列按序执行（同一时间仅运行一个），逐项进度条、排队 / 运行中取消、失败原因展示、完成记录清理

- **AVB / dm-verity 处理** — 修改分区后一键禁用 vbmeta 验证标志（等价 `fastboot --disable-verity --disable-verification`），或截断镜像末尾的 AVB 校验页脚恢复原像；自动扫描工作区全部 vbmeta 与带页脚镜像（含 super 嵌套与 `_zip`）

**应用体验**

- **原生 Windows 体验** — WinUI 3 + Mica 材质背景 + Fluent 设计语言

- **任务管线框架** — 解包 / 打包任务统一走 `IRomPackService` 管线，带进度报告、阶段提示与实时日志

- **任务取消** — 长任务可随时取消（`CancellationToken` 贯穿管线）

- **拖放支持** — 解包 / 打包页面可直接拖入 ROM 文件或工作目录

- **日志工作台** — 日志实时输出、一键清空 / 复制

- **持久化设置** — 主题（跟随系统 / 浅色 / 深色，即时切换）、默认工作目录 / 输出目录、签名密钥路径、sparse 块大小、打包完成后自动打开输出目录

- **自包含部署** — Windows App SDK 运行时随应用打包，目标机器零依赖

### 规划中（Roadmap）

- [x] build.prop 可视化编辑器
- [x] 预装应用精简管理
- [x] Magisk / KernelSU ROOT 集成
- [x] 定制模板系统
- [x] 多任务队列
- [x] AVB / dm-verity 处理
- [x] payload.bin（OTA 增量包）解析

> 初始 Roadmap 已全部完成。

## 界面预览

> TODO：应用稳定后补充截图

## 技术栈

| 层级    | 选型                                          |
| ----- | ------------------------------------------- |
| 语言    | C# / .NET 10.0                              |
| UI 框架 | WinUI 3（Windows App SDK 2.4）                |
| 布局    | TitleBar + NavigationView + MicaBackdrop    |
| 部署 | 非打包（unpackaged）自包含运行时，目标机器无需安装任何运行时 |
| 压缩 | SharpCompress（payload XZ 数据解压，纯托管） |
| 目标平台  | Windows 10 1809（17763）及以上，x86 / x64 / ARM64 |

## 环境要求

- Windows 10 1809（build 17763）及以上

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（开发）

- Visual Studio 2022（可选，需「使用 C++ 的桌面开发」与「Windows 应用程序开发」工作负载）

## 快速开始

```bash
# 克隆仓库（也可直接打开 RomPackageMaker.sln，含主项目与两个测试工程）
git clone https://github.com/Hinana0325/ROM-Package-Maker.git
cd ROM-Package-Maker

# 编译运行（首次会还原 NuGet 包）
dotnet run --project RomPackageMaker/RomPackageMaker.csproj
```

仅编译：

```bash
dotnet build RomPackageMaker/RomPackageMaker.csproj -c Release
```

发布为可分发的自包含目录（免安装，直接运行 exe）：

```bash
dotnet publish RomPackageMaker/RomPackageMaker.csproj -c Release -p:RuntimeIdentifier=win-x64 -p:SelfContained=true
# 输出位于 RomPackageMaker/bin/Release/net10.0-windows10.0.26100.0/win-x64/publish/
```

## 项目结构

```
RomPackageMaker/
├── App.xaml / App.xaml.cs        # 应用入口、全局异常捕获与主题切换
├── MainWindow.xaml               # 主窗口（TitleBar + NavigationView 导航）
├── Pages/                        # 15 个功能页
│   ├── HomePage.xaml             # 概览页（工作流引导）
│   ├── UnpackPage.xaml           # 解包工作台（拖放 / 进度 / 取消 / 日志）
│   ├── PackPage.xaml             # 打包工作台（拖放 / 签名选项 / 取消 / 日志）
│   ├── TaskQueuePage.xaml        # 任务队列（按序执行 / 取消 / 进度监控）
│   ├── PayloadPage.xaml          # payload.bin / OTA 解析与分区提取
│   ├── BuildPropPage.xaml        # build.prop 可视化编辑器（搜索 / 编辑 / 备份）
│   ├── DebloatPage.xaml          # 预装应用精简（扫描 / 勾选 / 批量删除）
│   ├── PreinstallPage.xaml       # 应用预装 / 资源替换
│   ├── RootPage.xaml             # ROOT 集成（boot.img 替换 / Magisk 内置）
│   ├── AvbPage.xaml              # AVB / dm-verity 处理（禁用验证 / 截断页脚）
│   ├── TemplatePage.xaml         # 定制模板（编辑 / 保存 / 一键应用）
│   ├── DiffPage.xaml             # 工作区差异对比
│   ├── InfoPage.xaml             # ROM 信息分析
│   ├── SettingsPage.xaml         # 设置页（主题 / 默认路径 / 签名密钥 / 块大小）
│   └── AboutPage.xaml            # 关于页
├── Core/                         # 零依赖核心（3 文件）
│   ├── IRomPackService.cs        # ROM 任务管线接口（进度 / 取消 / 日志）
│   ├── BinaryExtensions.cs       # 二进制读写扩展（小端读写 / 精确读 / 流复制）
│   └── Ext4Metadata.cs           # ext4 元数据 DTO
├── Engine/                       # 镜像格式引擎（11 文件，纯托管、流式优先）
│   ├── Ext4Reader.cs             # ext4 只读解析（extent 树 / inode 256，大文件流式提取）
│   ├── Ext4Writer.cs             # ext4 重建（inode 表按块组流式写出）
│   ├── BootImage.cs              # boot / vendor_boot 解析 / 重打包（cpio + gzip 流式）
│   ├── SparseImage.cs            # sparse 镜像解包 / 重打包
│   ├── SuperImage.cs             # super 动态分区布局解析
│   ├── PayloadBinService.cs      # payload.bin（OTA）解析 / 分区提取
│   ├── PayloadMoveAnalyzer.cs    # payload MOVE 操作重叠分析（等真实样本取证）
│   ├── AvbService.cs             # AVB / dm-verity（vbmeta 标志 / 页脚截断）
│   ├── ApkParser.cs              # APK 二进制 XML 解析
│   └── BuildPropService.cs / BuildPropPresets.cs   # build.prop 解析 / 预设
├── Application/                  # 业务编排与工作流（16 文件）
│   ├── RomPackService.cs         # 管线编排（按输入类型分发到各引擎）
│   ├── TemplateService.cs        # 定制模板（build.prop + 精简 + ROOT，即 Workflow v0）
│   ├── TaskQueueService.cs       # 多任务队列（顺序执行 / 取消 / 进度）
│   ├── WorkspaceScanner.cs       # 预装应用扫描 / 删除
│   ├── WorkspaceDiffService.cs   # 工作区差异对比
│   ├── RomInfoService.cs         # ROM 信息聚合
│   ├── RootService.cs            # ROOT 集成（Magisk 内置 / boot 替换）
│   ├── HostsService.cs / DebloatSafetyService.cs / PreinstallService.cs
│   ├── PackPreflightService.cs / PackVerifyService.cs   # 打包防呆 / 产物自检
│   ├── RomWorkspace.cs / WorkspaceState.cs              # 工作区布局 / 会话状态
│   └── AppSettings.cs / ZipSigner.cs                    # 设置持久化 / APK v1 签名
├── Assets/                       # 图标与启动资源
├── app.manifest                  # 应用清单
└── Package.appxmanifest          # 打包清单（可选 MSIX 用）

_selftest/
├── Program.cs                    # 端到端回归自测（159 项断言：全闭环 + AOSP 规范字节位置断言）
└── SelfTest.csproj

_realtest/
├── Program.cs                    # 真机样本验证台（boot / avb / super / ext4 / all 子命令）
├── RealTest.csproj
└── unsparse_head.py              # Python 独立 liblp 复核脚本

_benchmark/
├── Program.cs                    # 性能基准工程（prepare / measure / bench-write / analyze-payload）
└── _benchmark.csproj

Benchmarks/
├── Memory.md                     # 内存基准（Ext4 提取 / Ramdisk 打包 / Ext4 写入，含剩余热点）
├── Performance.md                # 耗时与回归记录
└── TestData.md                   # 基准样本说明（约 5 GB，git 忽略不入库）

docs/
├── PLAN.md                       # 下一步开发计划（分期与验收标准）
└── formats/aosp-offsets.md       # AOSP 镜像格式关键偏移（含实测坑位记录）
```



## 开发指南

- 分支规范、提交信息约定与 PR 流程见 [CONTRIBUTING.md](CONTRIBUTING.md)

- 版本历史见 [CHANGELOG.md](CHANGELOG.md)

- 后续开发计划见 [docs/PLAN.md](docs/PLAN.md)；镜像格式偏移速查见 [docs/formats/aosp-offsets.md](docs/formats/aosp-offsets.md)

- 性能基准与测量方法见 [Benchmarks/Memory.md](Benchmarks/Memory.md)；重跑用 `dotnet run --project _benchmark/_benchmark.csproj -c Release -- measure after`

- 性能基准与测量方法见 [Benchmarks/Memory.md](Benchmarks/Memory.md)；重跑用 `dotnet run --project _benchmark/_benchmark.csproj -c Release -- measure after`

- 安全问题反馈见 [SECURITY.md](SECURITY.md)

## 路线图详细说明

| 阶段    | 内容                       | 状态    |
| ----- | ------------------------ | ----- |
| v0.1  | 技术栈锁定、应用骨架、任务管线框架        | ✅ 完成  |
| v0.2  | zip 解包 / 重打包、boot.img 处理 | ✅ 完成  |
| v0.3  | ext4 / sparse 镜像解析与重建    | ✅ 完成  |
| v0.4  | super.img 动态分区、签名        | ✅ 完成  |
| v0.5  | build.prop 可视化编辑器        | ✅ 完成  |
| v0.6  | 预装精简、ROOT 集成、定制模板        | ✅ 完成  |
| v0.7 | 多任务队列、AVB / dm-verity 处理 | ✅ 完成 |
| v0.8 | payload.bin（OTA 增量包）解析与提取 | ✅ 完成 |
| v1.0 | 镜像引擎规范符合性修复（boot v4 / vendor\_boot / sparse 大块）、真机样本验证台 | ✅ 完成 |
| v1.1 | 正确性闭环（CI 接自测 / 打包容量治理 / 产物自检 / 发版）、.NET 10 升级、品牌双主题 | ✅ 完成 |
| v1.2 | 性能治理（Ext4Reader / CreateCpio / inode 表流式化）+ 基准体系 + Engine/Application/Core 分层 | ✅ 完成 |

> 初始 Roadmap 已全部完成。v1.0 重点是用真实厂商刷机包（小米 nezha HyperOS 3.0，19 个镜像、14.25 GiB super.img）验证并修复了镜像引擎的规范符合性缺陷。
>
> v1.1 补齐正确性闭环（CI 跑自测、容量治理、打包自检）并完成 .NET 10 / Windows App SDK 2.4 升级与品牌双主题 UI。v1.2 转向性能治理：核心路径全部流式化（Ext4 提取 500MB APK 峰值内存 1013MB → 3MB、Ramdisk 打包 1315MB → 147MB、inode 表 85MB → 55MB），建立可复跑的基准体系，并将 30 个服务拆为 Core / Engine / Application 三层。
>
> v1.1 补齐正确性闭环（CI 跑自测、容量治理、打包自检）并完成 .NET 10 / Windows App SDK 2.4 升级与品牌双主题 UI。v1.2 转向性能治理：核心路径全部流式化（Ext4 提取 500MB APK 峰值内存 1013MB → 3MB、Ramdisk 打包 1315MB → 147MB、inode 表 85MB → 55MB），建立可复跑的基准体系，并将 30 个服务拆为 Core / Engine / Application 三层。

## 质量保障

- **端到端回归自测**（`_selftest/SelfTest.csproj`，159 项断言全部通过）：用自身引擎从零构造完整 ROM 并走「解包 → 定制 → 打包 → 再解包」全闭环，其中 25 项按 AOSP `bootimg.h` / sparse / liblp 规范的**真实字节位置**断言（而非仅 Write→Parse 往返自洽）
- **真机样本验证台**（`_realtest`）：独立工程，直接用厂商线刷包实测各镜像引擎——boot 类镜像做**字节级往返比对**（报告首个差异位置），sparse / super / ext4 / vbmeta 分别解析；并用 Python 独立实现交叉复核 liblp 元数据
- **经验教训**：解析器测试不能只用自身引擎合成输入（会通过「自己写出的错误规范」），关键格式必须对照上游规范字节位置或第三方实现交叉验证

## 许可证

本项目基于 [MIT License](LICENSE) 开源。

## 免责声明

本工具仅供学习与合法的设备定制用途。请遵守当地法律法规以及设备厂商的保修条款；因使用本工具刷机导致的任何数据丢失或设备损坏，作者不承担责任。刷机有风险，操作需谨慎，请务必备份重要数据。
