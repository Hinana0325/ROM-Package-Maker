# 贡献指南

感谢你有意为 ROM Package Maker 贡献代码！请先花几分钟阅读以下约定。

## 开发环境

- Windows 10 1809（build 17763）及以上
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- 可选：Visual Studio 2022 +「Windows 应用程序开发」工作负载

## 本地构建

应用为**非打包（unpackaged）自包含**模式：编译产物是普通 exe 目录，直接运行即可，无需 MSIX 安装或注册包身份。

```bash
# 还原 + 编译
dotnet build RomPackageMaker/RomPackageMaker.csproj

# 运行
dotnet run --project RomPackageMaker/RomPackageMaker.csproj

# 发布为可分发的自包含目录（免安装）
dotnet publish RomPackageMaker/RomPackageMaker.csproj -c Release -p:RuntimeIdentifier=win-x64 -p:SelfContained=true
```

提交前请确保 `Debug` 与 `Release` 两种配置均编译通过、无警告。

> 注意：本地调试时 Windows App SDK NuGet 版本需与机器上安装的 Windows App Runtime 版本匹配（当前为 2.2.x）；自包含发布产物不依赖系统运行时，可直接分发。

## 分支规范

- `master` — 稳定主分支，保持随时可构建状态
- `feature/<简短功能名>` — 新功能分支，如 `feature/unpack-engine`
- `fix/<问题简述>` — 缺陷修复分支，如 `fix/progress-bar-overflow`

## 提交信息约定

采用 [Conventional Commits](https://www.conventionalcommits.org/zh-hans/) 格式：

```
<type>(<scope>): <subject>

[可选正文]
```

常用 type：

| type | 说明 |
|---|---|
| `feat` | 新功能 |
| `fix` | 缺陷修复 |
| `docs` | 文档变更 |
| `style` | 代码格式（不影响逻辑） |
| `refactor` | 重构 |
| `perf` | 性能优化 |
| `test` | 测试 |
| `chore` | 构建 / 工具链变更 |

示例：`feat(services): 接入 sparse 镜像头解析`

## 代码风格

- 遵循根目录 [.editorconfig](.editorconfig)（`dotnet format` 默认约定）
- C# 文件顶部启用 `nullable`，避免引入可空警告
- UI 文案使用中文；代码注释跟随改动范围，用中文说明「为什么」而非「是什么」
- XAML 命名与资源引用遵循模板既有风格

## 提交 PR 流程

1. Fork 或基于 `master` 创建功能分支
2. 完成开发并本地验证（Debug + Release 编译、实际运行）
3. 如涉及用户可见变更，同步更新 [CHANGELOG.md](CHANGELOG.md) 的 `Unreleased` 段
4. PR 标题遵循提交信息约定，正文描述动机与验证方式
5. 等待 CI 通过与维护者评审

## 报告问题

提 Issue 时请附带：

- Windows 版本与 CPU 架构
- 复现步骤与预期 / 实际行为
- 相关日志（应用日志区输出）

安全漏洞请勿公开提 Issue，参照 [SECURITY.md](SECURITY.md) 私密反馈。
