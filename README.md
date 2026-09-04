# ROM Package Maker · ROM 制作工具

[![CI](https://github.com/OWNER/REPO/actions/workflows/ci.yml/badge.svg)](https://github.com/OWNER/REPO/actions/workflows/ci.yml)
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

- **boot.img 解包与重打包** — boot / recovery 镜像头解析、cpio（newc）ramdisk 解包重组、gzip 压缩

- **super.img 动态分区** — super 分区动态布局解析，拆出子分区

- **zip 刷机包签名** — APK v1（JAR）签名，内置自签名测试证书，支持在设置中配置自定义密钥

**应用体验**

- **原生 Windows 体验** — WinUI 3 + Mica 材质背景 + Fluent 设计语言

- **任务管线框架** — 解包 / 打包任务统一走 `IRomPackService` 管线，带进度报告、阶段提示与实时日志

- **任务取消** — 长任务可随时取消（`CancellationToken` 贯穿管线）

- **拖放支持** — 解包 / 打包页面可直接拖入 ROM 文件或工作目录

- **日志工作台** — 日志实时输出、一键清空 / 复制

- **持久化设置** — 主题（跟随系统 / 浅色 / 深色，即时切换）、默认工作目录 / 输出目录、签名密钥路径、sparse 块大小、打包完成后自动打开输出目录

- **自包含部署** — Windows App SDK 运行时随应用打包，目标机器零依赖

### 规划中（Roadmap）

- [ ] build.prop 可视化编辑器

- [ ] 预装应用精简管理

- [ ] Magisk / KernelSU ROOT 集成

- [ ] 多任务队列与配置模板

- [ ] AVB / dm-verity 处理

- [ ] payload.bin（OTA 增量包）解析

## 界面预览

> TODO：应用稳定后补充截图

## 技术栈

| 层级    | 选型                                          |
| ----- | ------------------------------------------- |
| 语言    | C# / .NET 8.0                               |
| UI 框架 | WinUI 3（Windows App SDK 2.2）                |
| 布局    | TitleBar + NavigationView + MicaBackdrop    |
| 部署    | 非打包（unpackaged）自包含运行时，目标机器无需安装任何运行时         |
| 目标平台  | Windows 10 1809（17763）及以上，x86 / x64 / ARM64 |

## 环境要求

- Windows 10 1809（build 17763）及以上

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（开发）

- Visual Studio 2022（可选，需「使用 C++ 的桌面开发」与「Windows 应用程序开发」工作负载）

## 快速开始

```bash
# 克隆仓库
git clone https://github.com/OWNER/REPO.git
cd REPO

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
# 输出位于 RomPackageMaker/bin/Release/net8.0-windows10.0.26100.0/win-x64/publish/
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
│   ├── SettingsPage.xaml         # 设置页（主题 / 默认路径 / 签名密钥 / 块大小）
│   └── AboutPage.xaml            # 关于页
├── Services/
│   ├── IRomPackService.cs        # ROM 任务管线接口（进度 / 取消 / 日志）
│   ├── RomPackService.cs         # 管线编排（按输入类型分发到各引擎）
│   ├── RomWorkspace.cs           # 工作目录布局约定
│   ├── SparseImage.cs            # Android sparse 镜像解包 / 重打包
│   ├── Ext4Reader.cs             # ext4 文件系统只读解析（extent）
│   ├── Ext4Writer.cs             # ext4 文件系统重建
│   ├── BootImage.cs              # boot.img 解析 / 重打包（cpio + gzip）
│   ├── SuperImage.cs             # super 动态分区布局解析
│   ├── ZipSigner.cs              # zip 刷机包 APK v1 签名
│   ├── AppSettings.cs            # 设置持久化（appsettings.json）
│   └── BinaryExtensions.cs       # 二进制读写辅助
├── Assets/                       # 图标与启动资源
├── app.manifest                  # 应用清单
└── Package.appxmanifest          # 打包清单（可选 MSIX 用）
```

## 开发指南

- 分支规范、提交信息约定与 PR 流程见 [CONTRIBUTING.md](CONTRIBUTING.md)

- 版本历史见 [CHANGELOG.md](CHANGELOG.md)

- 安全问题反馈见 [SECURITY.md](SECURITY.md)

## 路线图详细说明

| 阶段    | 内容                       | 状态    |
| ----- | ------------------------ | ----- |
| v0.1  | 技术栈锁定、应用骨架、任务管线框架        | ✅ 完成  |
| v0.2  | zip 解包 / 重打包、boot.img 处理 | ✅ 完成  |
| v0.3  | ext4 / sparse 镜像解析与重建    | ✅ 完成  |
| v0.4  | super.img 动态分区、签名        | ✅ 完成  |
| v0.5+ | ROOT 集成、精简管理、模板系统        | 📋 规划 |

## 许可证

本项目基于 [MIT License](LICENSE) 开源。

## 免责声明

本工具仅供学习与合法的设备定制用途。请遵守当地法律法规以及设备厂商的保修条款；因使用本工具刷机导致的任何数据丢失或设备损坏，作者不承担责任。刷机有风险，操作需谨慎，请务必备份重要数据。
