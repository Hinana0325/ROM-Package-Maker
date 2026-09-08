# 下一步开发计划（v1.1 起）

> 制定时间：2026-09-08。基于当前代码实况（28 个服务类、15 个页面、自测 157 项、真机验证台 1 套）。

## 一、能力现状盘点

| 能力                | 状态      | 引擎 / 位置                                             |
| ----------------- | ------- | -------------------------------------------------- |
| zip 解包 / 重打包 / v1 签名 | ✅ 完整    | `RomPackService` / `ZipSigner`                     |
| ext4 读 + 写         | ✅ 完整    | `Ext4Reader` / `Ext4Writer`（含 extent 树、SELinux xattr） |
| sparse ↔ raw      | ✅ 完整    | `SparseImage`（大 don't-care 已修）                      |
| boot / vendor_boot | ✅ 完整    | `BootImage`（v0–v4，真机字节级往返一致）                         |
| super / liblp     | ✅ 读 + 写 | `SuperImage`                                       |
| payload.bin（A/B OTA） | ⚠️ 仅读取  | `PayloadBinService`（无生成）                           |
| AVB / vbmeta      | ⚠️ 仅禁用  | `AvbService`（无重签名）                                 |
| 定制能力（prop/精简/预装/ROOT/模板/hosts/对比） | ✅ 完整 | 各 Service + 页面                                     |
| 质量保障              | ⚠️ 半自动  | 自测 157 项（**CI 未跑**）、真机验证台（本地手动）                    |

## 二、已识别的短板（有代码依据）

1. **CI 只编译不跑自测** — `.github/workflows/ci.yml` 最后一步是 `dotnet build ... -warnaserror`，自测与真机验证台完全没接进去，回归只能靠本地手动跑。
2. **打包容量无校验** — `RomPackService.PackExt4Image`（:468）按内容重建镜像，不使用原分区大小；`SuperImage.Pack`（:427）按实际文件长度分配 extent。预装 APK / 加资源后体积超出原分区时，缺少明确的溢出检测与提示。
3. **打包结果无自检** — 打包完成即结束，不回读产物验证；真机闭环只做过「解包 → 解析」，没做过「解包 → 打包 → 回读比对」。
4. **AVB 只能禁用验证** — 无法在改分区后重算 hash / hashtree descriptor 并重新签名，锁定 bootloader 的设备无法直接用产物。
5. **无 CLI / 批处理** — 只能 GUI 点，无法脚本化批量处理。
6. **界面未收尾** — 本轮 UI 优化仍在迭代；README 截图、深色主题细查、窄窗口自适应未做。
7. **仓库未发版** — `v1.0` tag 与 GitHub Release 尚未创建。

## 三、候选任务清单

| # | 任务 | 为什么做 | 工作量 | 优先级 |
|---|---|---|---|---|
| 1 | **CI 接自测**：ci.yml 增加 `dotnet run --project _selftest/TestProj` 步骤 | 一行配置换来每次推送的回归保护 | 0.5h | P0 |
| 2 | **打包容量治理**：Ext4Writer 支持「最小镜像大小 = 原分区大小」；super 打包前做容量预估，溢出时明确报错并给出建议 | 防止改完 system 后打出的包刷不进去 | 0.5–1d | P0 |
| 3 | **打包自检闭环**：打包后自动回读（manifest / 镜像头 / ext4 文件清单 / sparse 展开大小），输出自检报告；真机样本跑「解包→不改→打包→回读」 | 目前完全没有产物验证，是真机可用性最大的未知项 | 1d | P0 |
| 4 | **UI 收尾**：深色主题细查、窄窗口自适应、提示文案统一、README 补截图 | 本轮已开工，需收口 | 1d | P1 |
| 5 | **AVB 重签名**：解析并重算 vbmeta 的 hash / hashtree descriptor，支持导入 RSA 密钥签名 | 唯一能覆盖「锁定 bootloader」场景的路径 | 2–3d | P1 |
| 6 | **工作区文件树**：浏览工作区、单文件提取 / 替换 / 删除，不必整包解包 | 定制时最常用的一步目前缺 UI | 1–2d | P1 |
| 7 | **CLI 批处理模式**：`RomPackageMaker.exe unpack <src> <dir>` / `pack <dir> <out>`，供脚本化 | 批量处理与自动化集成 | 1d | P2 |
| 8 | **payload.bin 生成 / 增量 OTA** | 读已支持，写是另一个量级 | 3d+ | P2 |
| 9 | **多语言（中 / 英）** | 面向非中文用户 | 1d | P2 |
| 10 | **发版**：打 `v1.0` tag + GitHub Release + 发布自包含 zip | 仓库至今无 Release | 0.5h | P1 |

## 四、建议分期

**v1.1（正确性闭环，已完成）** — 任务 1 ✅、2 ✅、3 ✅、10 ✅
- ✅ CI 接入自测（`.github/workflows/ci.yml` 增加 `dotnet run --project _selftest/SelfTest.csproj` 步骤；Restore/Build 需带 `-p:RuntimeIdentifier=win-x64`，否则 NETSDK1112）
- ✅ 打包容量治理：`Ext4Writer.MinSize` 保持重建后不小于原大小；`RomPackService.PackSuperImage` 按 group 估算容量并溢出告警；`PackPreflightService` 加预估（>100% Error / >85% Warn）
- ✅ 打包自检闭环：`PackVerifyService` 打包后回读产物，校验 boot 头 / ext4 superblock / super 子分区数 / ext4 条目数与工作区一致
- ✅ 发版：tag `v1.0` + [GitHub Release](https://github.com/Hinana0325/ROM-Package-Maker/releases/tag/v1.0)
- 验收：CI 全绿（Debug + Release 矩阵，含自测步骤）；自测 159 项 PASS（其中 2 项新加 pack-verify 断言）

**v1.2（能力扩展）** — 任务 4、5、6
目标：UI 收口 + 覆盖更多真实场景（AVB 重签名、文件树）。

**v2.0（工程化）** — 任务 7、8、9
目标：CLI / 自动化、payload 生成、多语言。

## 五、风险与原则

- **镜像格式改动必须按 AOSP 规范字节位置补断言**（`bootimg.h` / sparse / liblp），不能只用自身引擎往返验证——v1.0 的 3 个 P0 全栽在这里。
- **真机样本优于合成样本**：任何引擎改动优先用 `_realtest` 跑真实厂商包。
- **不自欺**：无法验证的东西（例如"能否开机"）要在文档中明确标注为未验证，不写「已支持」。
