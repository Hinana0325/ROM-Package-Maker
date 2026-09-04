# 更新日志

本项目的所有显著变更都将记录在此文件中。

格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本管理遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [Unreleased](https://github.com/OWNER/REPO/compare/v0.1.0...HEAD)

### 新增

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

### 变更

- 部署模型从单项目 MSIX 切换为**非打包（unpackaged）自包含部署**（`WindowsPackageType=None`），目标机器无需安装 Windows App SDK 运行时，也无需 MSIX 安装

- Windows App SDK NuGet 从 2.4.0 降级至 **2.2.0**，与本机已安装的 Windows App Runtime 2.2 匹配，避免 `REGDB_E_CLASSNOTREG` 启动崩溃

- 解包 / 打包页加载设置中的默认工作目录 / 输出目录，减少重复选择

### 计划中

- build.prop 可视化编辑器

- 预装应用精简管理

- Magisk / KernelSU ROOT 集成

- 多任务队列与配置模板

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
