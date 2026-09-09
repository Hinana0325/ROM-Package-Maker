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
├── Pages/
│   ├── HomePage.xaml             # 概览页（工作流引导）
│   ├── UnpackPage.xaml           # 解包工作台（拖放 / 进度 / 取消 / 日志）
│   ├── PackPage.xaml             # 打包工作台（拖放 / 签名选项 / 取消 / 日志）
│   ├── TaskQueuePage.xaml        # 任务队列（按序执行 / 取消 / 进度监控）
│   ├── PayloadPage.xaml          # payload.bin / OTA 解析与分区提取
│   ├── BuildPropPage.xaml        # build.prop 可视化编辑器（搜索 / 编辑 / 备份）
│   ├── DebloatPage.xaml          # 预装应用精简（扫描 / 勾选 / 批量删除）
│   ├── RootPage.xaml             # ROOT 集成（boot.img 替换 / Magisk 内置）
│   ├── AvbPage.xaml              # AVB / dm-verity 处理（禁用验证 / 截断页脚）
│   ├── TemplatePage.xaml         # 定制模板（编辑 / 保存 / 一键应用）
│   ├── SettingsPage.xaml         # 设置页（主题 / 默认路径 / 签名密钥 / 块大小）
│   └── AboutPage.xaml            # 关于页
├── Services/
│   ├── IRomPackService.cs        # ROM 任务管线接口（进度 / 取消 / 日志）
│   ├── RomPackService.cs         # 管线编排（按输入类型分发到各引擎）
│   ├── RomWorkspace.cs           # 工作目录布局约定
│   ├── BuildPropService.cs       # build.prop 解析 / 序列化 / 常见属性说明
│   ├── WorkspaceScanner.cs       # 预装应用扫描 / 删除
│   ├── RootService.cs            # ROOT 集成（Magisk 内置 / boot 替换）
│   ├── TemplateService.cs        # 定制模板存储 / 应用
│   ├── TaskQueueService.cs       # 多任务队列（顺序执行 / 取消 / 进度）
│   ├── AvbService.cs             # AVB / dm-verity（vbmeta 标志 / 页脚截断）
│   ├── WorkspaceState.cs         # 跨页面会话状态（最近工作目录）
│   ├── SparseImage.cs            # Android sparse 镜像解包 / 重打包
│   ├── Ext4Reader.cs             # ext4 文件系统只读解析（extent）
│   ├── Ext4Writer.cs             # ext4 文件系统重建
│   ├── BootImage.cs              # boot / vendor_boot 解析 / 重打包（cpio + gzip）
│   ├── SuperImage.cs             # super 动态分区布局解析
│   ├── ZipSigner.cs              # zip 刷机包 APK v1 签名
│   ├── AppSettings.cs            # 设置持久化（appsettings.json）
│   └── BinaryExtensions.cs       # 二进制读写辅助
├── Assets/                       # 图标与启动资源
├── app.manifest                  # 应用清单
└── Package.appxmanifest          # 打包清单（可选 MSIX 用）

_selftest/
├── Program.cs                    # 端到端回归自测（157 项断言：全闭环 + AOSP 规范字节位置断言）
└── SelfTest.csproj

_realtest/
├── Program.cs                    # 真机样本验证台（boot / avb / super / ext4 / all 子命令）
├── RealTest.csproj
└── unsparse_head.py              # Python 独立 liblp 复核脚本

docs/
├── PLAN.md                       # 下一步开发计划（分期与验收标准）
└── formats/aosp-offsets.md       # AOSP 镜像格式关键偏移（含实测坑位记录）
```

## 开发指南

- 分支规范、提交信息约定与 PR 流程见 [CONTRIBUTING.md](CONTRIBUTING.md)

- 版本历史见 [CHANGELOG.md](CHANGELOG.md)

- 后续开发计划见 [docs/PLAN.md](docs/PLAN.md)；镜像格式偏移速查见 [docs/formats/aosp-offsets.md](docs/formats/aosp-offsets.md)

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

> 初始 Roadmap 已全部完成。v1.0 重点是用真实厂商刷机包（小米 nezha HyperOS 3.0，19 个镜像、14.25 GiB super.img）验证并修复了镜像引擎的规范符合性缺陷。

## 质量保障

- **端到端回归自测**（`_selftest/TestProj`，157 项断言全部通过）：用自身引擎从零构造完整 ROM 并走「解包 → 定制 → 打包 → 再解包」全闭环，其中 25 项按 AOSP `bootimg.h` / sparse / liblp 规范的**真实字节位置**断言（而非仅 Write→Parse 往返自洽）
- **真机样本验证台**（`_realtest`）：独立工程，直接用厂商线刷包实测各镜像引擎——boot 类镜像做**字节级往返比对**（报告首个差异位置），sparse / super / ext4 / vbmeta 分别解析；并用 Python 独立实现交叉复核 liblp 元数据
- **经验教训**：解析器测试不能只用自身引擎合成输入（会通过「自己写出的错误规范」），关键格式必须对照上游规范字节位置或第三方实现交叉验证

## 许可证

本项目基于 [MIT License](LICENSE) 开源。

## 免责声明

本工具仅供学习与合法的设备定制用途。请遵守当地法律法规以及设备厂商的保修条款；因使用本工具刷机导致的任何数据丢失或设备损坏，作者不承担责任。刷机有风险，操作需谨慎，请务必备份重要数据。
