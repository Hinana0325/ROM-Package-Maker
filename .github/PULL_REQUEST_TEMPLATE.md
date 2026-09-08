## 改动说明

<!-- 一两句话说明动机与做了什么 -->

## 类型

- [ ] 新功能（feat）
- [ ] 缺陷修复（fix）
- [ ] 文档（docs）
- [ ] 重构 / 性能（refactor / perf）
- [ ] 构建 / 工具链（chore）

## 验证方式

- [ ] `dotnet build RomPackageMaker/RomPackageMaker.csproj` Debug 与 Release 均通过、无警告
- [ ] `dotnet run --project _selftest` 全部 PASS
- [ ] 涉及镜像格式（boot / vendor_boot / sparse / super / ext4）时：已按
      [docs/formats/aosp-offsets.md](docs/formats/aosp-offsets.md) 的**真实字节位置**补充断言，
      而非只用自身引擎 Write→Parse 往返验证
- [ ] 有真机样本时已用 `_realtest` 跑过（列出跑过的子命令与结论）

## 用户可见变更

- [ ] 已在 [CHANGELOG.md](CHANGELOG.md) 顶部新增 / 更新 `## [Unreleased]` 段

## 截图 / 日志

<!-- 界面改动请附截图；镜像相关改动请附关键日志 -->
